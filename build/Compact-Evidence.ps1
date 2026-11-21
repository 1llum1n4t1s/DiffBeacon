param(
    [Parameter(Mandatory)][string[]]$SourcePath,
    [Parameter(Mandatory)][string]$ArchiveRoot,
    [Parameter(Mandatory)][string]$PythonPath,
    [string[]]$AdditionalEvidenceRoot = @(),
    [string]$ExistingArchive,
    [switch]$CompressOnly,
    [switch]$OwnerAttestsQuiescentAndNoMixedWork
)
$ErrorActionPreference = 'Stop'
if (-not $OwnerAttestsQuiescentAndNoMixedWork) { throw '展開元の所有者が利用中プロセスなし・他作業混在なしを確認してから -OwnerAttestsQuiescentAndNoMixedWork を指定してください。' }
if (-not $IsWindows) { throw 'Windowsでは pwsh -NoProfile -File build/Compact-Evidence.ps1 で実行してください。圧縮だけなら CompactEvidence.py を使用できます。' }
. "$PSScriptRoot/DevelopmentOutput.ps1"
$workRoot = Get-DiffBeaconWorkRoot
if (-not $workRoot) { $workRoot = Join-Path ([IO.Path]::GetTempPath()) 'Codex/DiffBeacon' }
Assert-DiffBeaconEnvironment (Get-DiffBeaconWorkRoot)
$allowedRoots = @($workRoot)
$allowedRoots += @($AdditionalEvidenceRoot | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\', '/') })

function Assert-NoLink([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "リンク経由の操作を拒否: $cursor" }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Assert-Source([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $inside = @($allowedRoots | Where-Object { $full.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })
    if ($inside.Count -eq 0) { throw "証拠rootの下位ディレクトリ以外は対象外: $full" }
    Assert-NoLink $full
    $item = Get-Item -LiteralPath $full -Force
    if (-not $item.PSIsContainer) { throw "対象はディレクトリに限ります: $full" }
    return $full
}

$archivePath = Assert-DiffBeaconPath $ArchiveRoot $workRoot
Assert-NoLink $archivePath
if (-not $CompressOnly -and $archivePath -match '[^A-Za-z0-9_:\\./-]') { throw 'Prunlet paths-fileのCMD引数に使える空白/特殊文字なしのArchiveRootを指定してください。' }
$indexPath = Join-Path $archivePath 'index.json'
$null = Assert-DiffBeaconPath $indexPath $workRoot -AllowFileLeaf
$sources = @($SourcePath | ForEach-Object { Assert-Source $_ })
if ($ExistingArchive -and $sources.Count -ne 1) { throw '途中ZIPの再照合は入力1件に限ります。' }
if (@($sources | Select-Object -Unique).Count -ne $sources.Count) { throw '重複した入力を拒否します。' }
foreach ($source in $sources) {
    if ($archivePath -eq $source -or $archivePath.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '格納先は入力の外へ指定してください。' }
    foreach ($other in $sources) {
        if ($source.StartsWith($other + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '入力に親子関係があります。' }
    }
}
# 明示対象の実行中アプリ・取得プロセスがある場合は圧縮を開始しない。
foreach ($process in Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(dotnet|DiffBeacon|gh)\.exe$' }) {
    foreach ($source in $sources) {
        if ($process.CommandLine -and $process.CommandLine.Contains($source, [StringComparison]::OrdinalIgnoreCase)) { throw "対象を参照するプロセスが実行中: PID $($process.ProcessId)" }
    }
}
New-Item -ItemType Directory -Path $archivePath -Force | Out-Null
foreach ($source in $sources) {
    Assert-Source $source | Out-Null
    $id = [guid]::NewGuid().ToString('N')
    $safeLeaf = [regex]::Replace((Split-Path -Path $source -Leaf), '[^A-Za-z0-9_-]', '_')
    $name = "$safeLeaf-$id"
    $archive = Join-Path $archivePath "$name.zip"
    $reportPath = Join-Path $archivePath "$name.json"
    $extraArguments = @()
    if ($ExistingArchive) {
        $archive = [IO.Path]::GetFullPath($ExistingArchive)
        Assert-NoLink $archive
        if ([IO.Path]::GetDirectoryName($archive) -ne $archivePath) { throw '途中ZIPは指定した格納先の直下に限ります。' }
        $reportPath = [IO.Path]::ChangeExtension($archive, '.json')
        $extraArguments = @('--verify-existing')
    }
    $logPath = Join-Path $archivePath "$name.compress.log"
    $null = Assert-DiffBeaconPath $archive $workRoot -AllowFileLeaf
    $null = Assert-DiffBeaconPath $reportPath $workRoot -AllowFileLeaf
    $null = Assert-DiffBeaconPath $logPath $workRoot -AllowFileLeaf
    & $PythonPath -B -X utf8 (Join-Path $PSScriptRoot 'CompactEvidence.py') --source $source --archive $archive --report $reportPath @extraArguments 2>&1 | Tee-Object -FilePath $logPath
    if ($LASTEXITCODE -ne 0) { throw "圧縮照合に失敗。元データを保持: $source; 途中ZIP: $archive" }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.source -ne $source -or $report.archive -ne $archive -or
        -not $report.sourceUnchanged -or -not $report.allFileHashesVerified -or $report.files -ne $report.verifiedFiles) { throw '除去前の照合条件を満たしません。' }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $report.archiveSha256) { throw '照合後のZIPが変化しました。' }
    Assert-Source $source | Out-Null
    # PowerShellから削除を呼び出さない。正規Prunletは呼出側がCMD直接で実行する。
    $entry = [ordered]@{ source=$source; archive=$archive; report=$reportPath; archiveSha256=$report.archiveSha256; files=$report.files; bytes=$report.sourceBytes; log=$logPath; sourceRemoved=$false; cleanupState='not-requested' }
    if (-not $CompressOnly) {
        $pathsPath = Join-Path $archivePath "$name.paths.json"
        $resultPath = Join-Path $archivePath "$name.delete.json"
        foreach ($output in @($pathsPath, $resultPath)) { $null = Assert-DiffBeaconPath $output $workRoot -AllowFileLeaf }
        if (Test-Path -LiteralPath $pathsPath) { throw '既存paths-fileを上書きしません。' }
        $pathsJson = ConvertTo-Json -InputObject @($source)
        [IO.File]::WriteAllText($pathsPath, $pathsJson, [Text.UTF8Encoding]::new($false))
        $entry.cleanupState = 'awaiting-prunlet-cmd-direct'
        $entry.pathsFile = $pathsPath
        $entry.deleteResult = $resultPath
        $entry.command = "C:\Users\IMT\.local\Prunlet\bin\prunlet.exe --config C:\Users\IMT\.local\Prunlet\config.json delete --recursive --paths-file $pathsPath > $resultPath"
    }
    $index = if (Test-Path -LiteralPath $indexPath) { Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{ schema=1; entries=@() } }
    if ($index.schema -ne 1 -or $null -eq $index.entries) { throw '既存indexのschemaが不明なため更新しません。' }
    $index.entries = @($index.entries) + @([pscustomobject]$entry)
    $null = Assert-DiffBeaconPath $indexPath $workRoot -AllowFileLeaf
    $json = $index | ConvertTo-Json -Depth 10
    $encoded = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = [IO.File]::Open($indexPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($encoded, 0, $encoded.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    [pscustomobject]$entry | ConvertTo-Json -Depth 6 -Compress
}
param(
    [Parameter(Mandatory)][string[]]$SourcePath,
    [Parameter(Mandatory)][string]$ArchiveRoot,
    [Parameter(Mandatory)][string]$PythonPath,
    [string[]]$AdditionalEvidenceRoot = @(),
    [string]$ExistingArchive
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$allowedRoots = @([IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')))
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

$archivePath = [IO.Path]::GetFullPath($ArchiveRoot).TrimEnd('\', '/')
Assert-NoLink $archivePath
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
    $name = "$(Split-Path -Path $source -Leaf)-$id"
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
    & $PythonPath -X utf8 (Join-Path $PSScriptRoot 'CompactEvidence.py') --source $source --archive $archive --report $reportPath @extraArguments
    if ($LASTEXITCODE -ne 0) { throw "圧縮照合に失敗。元データを保持: $source; 途中ZIP: $archive" }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.source -ne $source -or $report.archive -ne $archive -or
        -not $report.sourceUnchanged -or -not $report.allFileHashesVerified -or $report.files -ne $report.verifiedFiles) { throw '除去前の照合条件を満たしません。' }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $report.archiveSha256) { throw '照合後のZIPが変化しました。' }
    Assert-Source $source | Out-Null
    # 対象の絶対パス・root範囲・リンク・全entry SHAを検査した同じPowerShellセッションで除去する。
    Remove-Item -LiteralPath $source -Recurse -Force
    if (Test-Path -LiteralPath $source) { throw "展開元が残っています: $source" }
    $report.sourceRemoved = $true
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
    $report | ConvertTo-Json -Depth 6 -Compress
}

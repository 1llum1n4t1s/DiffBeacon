param(
    [Parameter(Mandatory)][string[]]$SourcePath,
    [Parameter(Mandatory)][string]$ArchiveRoot,
    [Parameter(Mandatory)][string]$PythonPath,
    [string[]]$AdditionalEvidenceRoot = @(),
    [string]$ExistingArchive,
    [switch]$OwnerAttestsQuiescentAndNoMixedWork
)
$ErrorActionPreference = 'Stop'
$WindowsCleanupHelper = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.codex/scripts/Remove-CodexItem.ps1'
if (-not $OwnerAttestsQuiescentAndNoMixedWork) { throw '展開元の所有者が利用中プロセスなし・他作業混在なしを確認してから -OwnerAttestsQuiescentAndNoMixedWork を指定してください。' }
if (-not $IsWindows -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Windowsでは pwsh -STA -NoProfile -File build/Compact-Evidence.ps1 で実行してください。圧縮だけなら CompactEvidence.py を使用できます。' }
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
    # 全entry照合後も、ごみ箱への移動と今回項目の完全消去を別々に記録する。
    $cleanupPath = Join-Path $archivePath "$name-recycle-clean.json"
    $evidenceRoot = @($allowedRoots | Where-Object { $source.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) } | Sort-Object Length -Descending)[0]
    if (-not (Test-Path -LiteralPath $WindowsCleanupHelper -PathType Leaf)) { throw '指定されたWindows共通清掃ヘルパーがないため、展開元とZIPを保持します。' }
    & $WindowsCleanupHelper -AllowedRoot $evidenceRoot -LiteralPath @($source) -LedgerPath $cleanupPath
    $cleaned = Get-Content -LiteralPath $cleanupPath -Raw | ConvertFrom-Json
    if ($cleaned.state -ne 'complete' -or @($cleaned.items | Where-Object { -not $_.sourceAbsent -or -not $_.recycleAbsent -or -not $_.metadataAbsent }).Count -gt 0) { throw '元データ・ごみ箱実体・管理情報の消失を確認できません。' }
    if (Test-Path -LiteralPath $source) { throw "展開元が残っています: $source" }
    $report.sourceRemoved = $true
    $report | Add-Member -NotePropertyName cleanupProtocol -NotePropertyValue 'recycle-exact-item-purge' -Force
    $report | Add-Member -NotePropertyName cleanupReport -NotePropertyValue $cleanupPath -Force
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
    $report | ConvertTo-Json -Depth 6 -Compress
}

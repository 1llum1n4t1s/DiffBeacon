param(
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'macOS の SDK で実行してください。' }
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $repositoryRoot 'tests/Fixtures/FolderSync/mac-file-kind-reference.c'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory, $repositoryRoot) } else { Join-Path ([IO.Path]::GetTempPath()) ('Codex/DiffBeacon/folder-reference-' + [guid]::NewGuid().ToString('N')) }
# 作業出力は外部の作業別領域へ限定し、作成前に全祖先を検査する。
$allowedOutputRoots = @(
    (Join-Path ([IO.Path]::GetTempPath()) 'Codex/DiffBeacon'),
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Codex/TaskArtifacts/DiffBeacon')
)
if ($env:RUNNER_TEMP -and $env:GITHUB_ACTIONS -eq 'true') {
    $allowedOutputRoots += Join-Path $env:RUNNER_TEMP 'Codex/DiffBeacon'
}
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$allowedOutput = $false
foreach ($allowedRoot in $allowedOutputRoots) {
    $prefix = [IO.Path]::GetFullPath($allowedRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($output.StartsWith($prefix, $comparison)) { $allowedOutput = $true; break }
}
$repoPrefix = $repositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$codexRoot = if ($env:CODEX_HOME) { [IO.Path]::GetFullPath($env:CODEX_HOME) } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex' }
$codexPrefix = $codexRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $allowedOutput -or $output.StartsWith($repoPrefix, $comparison) -or $output.Equals($repositoryRoot, $comparison) -or $output.StartsWith($codexPrefix, $comparison) -or $output.Equals($codexRoot, $comparison) -or $output -match '(?i)(^|[\\/])(artifacts|\.codex)([\\/]|$)') {
    throw '生成先はrepo／Codexユーザーディレクトリ外のCodex/DiffBeacon作業別領域を指定してください。artifactsという名前のパス要素は使用できません。'
}
$currentPath = $output
while ($currentPath) {
    $existingAncestor = $null
    try { $existingAncestor = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop }
    catch [Management.Automation.ItemNotFoundException] { }
    if ($existingAncestor) {
        if ($existingAncestor.PSProvider.Name -ne 'FileSystem' -or $existingAncestor -isnot [IO.DirectoryInfo]) {
            throw "生成先の祖先はファイルシステムのディレクトリである必要があります: $currentPath"
        }
        if ($existingAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "生成先にリンクは使用できません: $currentPath"
        }
    }
    $currentPath = [IO.Path]::GetDirectoryName($currentPath)
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = (Get-Command clang -CommandType Application).Source
$reference = Join-Path $output 'mac-file-kind-reference'
& $compiler --version 1> (Join-Path $output 'compiler.stdout.txt') 2> (Join-Path $output 'compiler.stderr.txt')
if ($LASTEXITCODE -ne 0) { throw "clang --version に失敗しました: $LASTEXITCODE" }
$sdk = & xcrun --show-sdk-path
if ($LASTEXITCODE -ne 0) { throw "SDK の取得に失敗しました: $LASTEXITCODE" }
& $compiler -std=c11 -Wall -Wextra -Werror $source -o $reference 1> (Join-Path $output 'build.stdout.txt') 2> (Join-Path $output 'build.stderr.txt')
$buildExit = $LASTEXITCODE
if ($buildExit -ne 0) { throw "SDK reference の build に失敗しました: $buildExit" }
& $reference 1> (Join-Path $output 'sdk-stat.json') 2> (Join-Path $output 'probe.stderr.txt')
$probeExit = $LASTEXITCODE
[ordered]@{
    createdUtc = [DateTime]::UtcNow.ToString('O')
    processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    compiler = $compiler
    sdk = $sdk
    sourceSha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    executableSha256 = (Get-FileHash -LiteralPath $reference -Algorithm SHA256).Hash
    buildExitCode = $buildExit
    probeExitCode = $probeExit
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'reference-receipt.json') -Encoding utf8NoBOM
if ($probeExit -ne 0) { throw "SDK の stat ABI が製品の境界と一致しません: $probeExit" }
Get-Content -LiteralPath (Join-Path $output 'sdk-stat.json') -Raw | ConvertFrom-Json | Out-Null

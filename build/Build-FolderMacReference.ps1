param(
    [Parameter(Mandatory)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'macOS の SDK で実行してください。' }
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repositoryRoot 'tests/Fixtures/FolderSync/mac-file-kind-reference.c'
$output = [IO.Path]::GetFullPath($OutputDirectory)
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

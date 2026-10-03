[CmdletBinding()]
param(
    [string] $OutputDirectory = 'artifacts/z-reference'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = Join-Path $repoRoot 'tests/Fixtures/Archives/TarZ/reference-source'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
if (-not $outputRoot.StartsWith((Join-Path $repoRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'reference buildの生成先はこのrepoのartifacts配下を指定してください。'
}
foreach ($source in @(
    @{ Name = 'compress.c'; SHA = '29C5A78005921A7881D8C83EA711E826C8C87F354B4A2F47F8C474117F6646D8' },
    @{ Name = 'patchlevel.h'; SHA = '458F2AF3AA2862B80E52D45F277FDA6D105A225DD0629ECA9153DD3B060B47A3' },
    @{ Name = 'UNLICENSE'; SHA = '7E12E5DF4BAE12CB21581BA157CED20E1986A0508DD10D0E8A4AB9A4CF94E85C' }
)) {
    if ((Get-FileHash -LiteralPath (Join-Path $sourceRoot $source.Name) -Algorithm SHA256).Hash -ne $source.SHA) {
        throw "reference原本SHAが一致しません: $($source.Name)"
    }
}
$currentPath = $outputRoot
while ($currentPath) {
    if ((Test-Path -LiteralPath $currentPath) -and ((Get-Item -LiteralPath $currentPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "reference生成先にリンクは使用できません: $currentPath"
    }
    $currentPath = [IO.Path]::GetDirectoryName($currentPath)
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($architecture -notin @('x64', 'arm64')) { throw 'reference decoderはx64/arm64のhostに対応します。' }
$sourcePath = Join-Path $sourceRoot 'compress.c'
if ($IsWindows) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'MSVCの既存Visual Studioを確認できません。' }
    $vsRoot = & $vswhere -latest -products '*' -property installationPath
    if (-not $vsRoot) { throw 'MSVCが見つかりません。' }
    $toolVersion = (Get-Content -LiteralPath (Join-Path $vsRoot 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
    $vcRoot = Join-Path $vsRoot "VC/Tools/MSVC/$toolVersion"
    $hostTool = if (Test-Path -LiteralPath (Join-Path $vcRoot "bin/Host$architecture/$architecture/cl.exe")) { "Host$architecture" } else { 'Hostx64' }
    $compiler = Join-Path $vcRoot "bin/$hostTool/$architecture/cl.exe"
    if (-not (Test-Path -LiteralPath $compiler)) { throw "host architecture用のMSVCがありません: $architecture" }
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
    $sdkVersion = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $sdkRoot "Lib/$($_.Name)/ucrt/$architecture") } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
    if (-not $sdkVersion) { throw 'host architecture用Windows SDKがありません。' }
    $previousInclude = $env:INCLUDE; $previousLib = $env:LIB
    $env:INCLUDE = "$vcRoot/include;$sdkRoot/Include/$sdkVersion/ucrt;$sdkRoot/Include/$sdkVersion/shared;$sdkRoot/Include/$sdkVersion/um"
    $env:LIB = "$vcRoot/lib/$architecture;$sdkRoot/Lib/$sdkVersion/ucrt/$architecture;$sdkRoot/Lib/$sdkVersion/um/$architecture"
    $decoder = Join-Path $outputRoot 'ncompress.exe'
    $arguments = @('/nologo', '/O2', '/MT', '/D_CRT_SECURE_NO_WARNINGS', "/Fo$outputRoot/compress.obj", "/Fe$decoder", $sourcePath, '/link', '/INCREMENTAL:NO')
} elseif ($IsMacOS) {
    $compiler = (Get-Command clang -ErrorAction Stop).Source
    $decoder = Join-Path $outputRoot 'ncompress'
    $arguments = @('-O2', '-DUTIME_H', '-DLSTAT', '-arch', $(if ($architecture -eq 'arm64') { 'arm64' } else { 'x86_64' }), '-o', $decoder, $sourcePath)
} else { throw 'reference buildはWindows/macOSの検証hostで実行してください。' }
try {
    $log = & $compiler @arguments 2>&1
    $compilerExit = $LASTEXITCODE
} finally {
    if ($IsWindows) { $env:INCLUDE = $previousInclude; $env:LIB = $previousLib }
}
$log | Set-Content -LiteralPath (Join-Path $outputRoot 'build.log') -Encoding utf8
$proof = [ordered]@{
    source = $sourcePath; sourceSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    patchlevelSha256 = (Get-FileHash -LiteralPath (Join-Path $sourceRoot 'patchlevel.h') -Algorithm SHA256).Hash
    compiler = $compiler; compilerSha256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
    hostArchitecture = $architecture; arguments = $arguments; compilerExitCode = $compilerExit
    decoder = $decoder; adapter = 'none; original ncompress 5.1 has upstream MSVC/binary stdio support'
}
if ($compilerExit -eq 0) { $proof.decoderSha256 = (Get-FileHash -LiteralPath $decoder -Algorithm SHA256).Hash }
$proof | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputRoot 'build-proof.json') -Encoding utf8
if ($compilerExit -ne 0) { throw "reference buildが失敗しました。build.logを確認してください: $outputRoot" }
$proof | ConvertTo-Json -Depth 5

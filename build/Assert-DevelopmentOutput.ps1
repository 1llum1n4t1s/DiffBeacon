param([string] $MSBuildPaths, [string] $MSBuildFiles, [string] $ProjectDirectory, [string] $ConfiguredRoot)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/DevelopmentOutput.ps1"
$root = Get-DiffBeaconWorkRoot
Assert-DiffBeaconEnvironment $root
if ($root) {
    if ($ConfiguredRoot -ne $root) { throw 'DiffBeaconWorkRoot はローカル正本設定から変更できません。' }
    foreach ($path in $MSBuildPaths.Split('|')) {
        if ($path) { $null = Assert-DiffBeaconPath $path $root $ProjectDirectory }
    }
    foreach ($path in $MSBuildFiles.Split('|')) {
        if ($path) { $null = Assert-DiffBeaconPath $path $root $ProjectDirectory -AllowFileLeaf }
    }
}

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorkDirectory,
    [Parameter(Mandatory)] [ValidateSet('dotnet', 'publish', 'z-reference', 'pwsh')] [string] $Tool,
    [Parameter(ValueFromRemainingArguments)] [string[]] $ToolArguments
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/DevelopmentOutput.ps1"
$root = Get-DiffBeaconWorkRoot
if (-not $root) { $root = Join-Path ([IO.Path]::GetTempPath()) 'Codex/DiffBeacon' }
$work = Assert-DiffBeaconPath $WorkDirectory $root
$temp = Assert-DiffBeaconPath (Join-Path $work 'temp') $root
$build = Assert-DiffBeaconPath (Join-Path $work 'build') $root
# MSBuildのloggerは初期targetより前に開くため、親process側で検査する。
if ($Tool -eq 'dotnet') {
    foreach ($argument in $ToolArguments) {
        if ($argument.TrimStart().StartsWith('@', [StringComparison]::Ordinal)) { throw '明示response file (@...) は起動前の出力検査を迂回するため使用できません。引数を直接指定してください。' }
        if ($argument -match '^[-/](bl|binarylogger)(:|$)') {
            $value = ($argument -split ':', 2 | Select-Object -Skip 1) -join ':'
            if (-not $value) { throw 'binary loggerにはwork directory配下の絶対pathを明示してください。' }
            $parameters = @($value -split ';')
            foreach ($parameter in @($parameters | Select-Object -Skip 1)) {
                if ($parameter -notmatch '^(?i)ProjectImports=(None|Embed|ZipFile)$') { throw 'binary loggerのpathは先頭の1件だけ明示してください。追加はProjectImports指定だけを受け付けます。' }
            }
            $logPath = $parameters[0] -replace '^(?i)LogFile=', ''
            $null = Assert-DiffBeaconPath $logPath $root -AllowFileLeaf
        }
        if ($argument -match '^[-/](fl|filelogger)\d*$') { throw '既定file loggerはrepoへ書くため禁止です。LogFileをwork directory配下へ明示してください。' }
        if ($argument -match '^[-/](flp|fileloggerparameters)\d*:(.*)$') {
            $logFiles = [regex]::Matches($Matches[2], '(?i)(?:^|;)\s*LogFile\s*=\s*([^;]*)')
            if ($logFiles.Count -ne 1 -or [string]::IsNullOrWhiteSpace($logFiles[0].Groups[1].Value)) { throw 'file loggerのLogFileはちょうど1件だけ明示してください。重複/不明な指定は使用できません。' }
            $null = Assert-DiffBeaconPath $logFiles[0].Groups[1].Value $root -AllowFileLeaf
        }
    }
    # runは-noAutoResponseをアプリへ転送する。SDKとproject/CWD祖先の自動rspを読む前に拒否する。
    if (@($ToolArguments).Count -gt 0 -and $ToolArguments[0] -eq 'run') {
        $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $starts = [Collections.Generic.List[string]]::new()
        $starts.Add((Get-Location).Path)
        for ($index = 1; $index -lt $ToolArguments.Count; $index++) {
            $value = $ToolArguments[$index]
            if ($value -eq '--') { break }
            if ($value -in @('--project', '-p', '--file')) {
                if ($index + 1 -lt $ToolArguments.Count) { $value = $ToolArguments[++$index] }
            } elseif ($value -match '^(?:--project|--file|-p)(?:=|:)(.+)$') { $value = $Matches[1] }
            elseif ($value.StartsWith('-')) { continue }
            if ($value -match '\.(csproj|fsproj|vbproj|sln|slnx|cs)$' -or (Test-Path -LiteralPath $value -PathType Container)) {
                $full = [IO.Path]::GetFullPath($value)
                $starts.Add($(if (Test-Path -LiteralPath $full -PathType Container) { $full } else { [IO.Path]::GetDirectoryName($full) }))
            }
        }
        foreach ($start in $starts) {
            for ($cursor = $start; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
                $null = $directories.Add($cursor)
                $globalJson = Join-Path $cursor 'global.json'
                if (Test-Path -LiteralPath $globalJson) {
                    $settings = Get-Content -LiteralPath $globalJson -Raw | ConvertFrom-Json
                    $sdkSettings = $settings.PSObject.Properties['sdk']
                    if ($sdkSettings -and $sdkSettings.Value -and $sdkSettings.Value.PSObject.Properties['paths']) { throw 'runのカスタムSDK探索pathsでは自動responseを安全に検査できません。通常SDKと直接引数を使用してください。' }
                }
            }
        }
        $hostExecutable = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
        $hostFile = [IO.FileInfo]::new($hostExecutable)
        if ($hostFile.LinkTarget) { $hostExecutable = $hostFile.ResolveLinkTarget($true).FullName }
        $sdkRoot = Join-Path ([IO.Path]::GetDirectoryName($hostExecutable)) 'sdk'
        if (-not (Test-Path -LiteralPath $sdkRoot -PathType Container)) { throw 'runのSDK directoryを安全に確認できません。' }
        foreach ($sdk in Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction Stop) { $null = $directories.Add($sdk.FullName) }
        foreach ($directory in $directories) {
            foreach ($name in @('MSBuild.rsp', 'Directory.Build.rsp')) {
                $candidate = Join-Path $directory $name
                if (Test-Path -LiteralPath $candidate) { throw "runの自動response fileは起動前の出力検査を迂回するため使用できません: $candidate" }
            }
        }
    }
}
# 検査完了後にだけ作成し、子process全体へ同じtempを継承する。
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$saved = @{}
foreach ($name in @('TEMP', 'TMP', 'TMPDIR', 'DIFFBEACON_WORK_DIRECTORY')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:TEMP = $temp; $env:TMP = $temp; $env:TMPDIR = $temp; $env:DIFFBEACON_WORK_DIRECTORY = $work
    switch ($Tool) {
        'dotnet' {
            $arguments = @($ToolArguments)
            if ($arguments.Count -gt 0 -and $arguments[0] -in @('build', 'restore', 'run', 'publish', 'msbuild', 'pack')) {
                $additional = @("-p:ArtifactsPath=$build")
                if ($arguments[0] -ne 'run') { $additional += '-noAutoResponse' }
                $separator = [Array]::IndexOf($arguments, '--')
                if ($separator -lt 0) { $arguments += $additional }
                else { $arguments = @($arguments[0..($separator - 1)]) + $additional + @($arguments[$separator..($arguments.Count - 1)]) }
            }
            & dotnet @arguments
        }
        'publish' { & pwsh -NoProfile -File "$PSScriptRoot/Publish.ps1" -OutputRoot (Join-Path $work 'publish') -BuildArtifactsPath $build @ToolArguments }
        'z-reference' { & pwsh -NoProfile -File "$PSScriptRoot/Build-ZReference.ps1" -OutputDirectory (Join-Path $work 'z-reference') @ToolArguments }
        'pwsh' { & pwsh @ToolArguments }
    }
    if ($LASTEXITCODE -ne 0) { throw "開発コマンドが失敗しました: $LASTEXITCODE" }
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}

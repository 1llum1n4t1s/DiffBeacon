param([string]$CaseName='',[string]$GoldenName='legacy-line-golden.json',[switch]$CorrectPairDummy,[string]$OutputDirectory='artifacts/verification/table-line-alignment/reproduced')
$ErrorActionPreference='Stop'
if([IO.Path]::GetFileName($GoldenName) -ne $GoldenName){throw '出力名はファイル名に限定'}
$repo=Split-Path $PSScriptRoot
$output=if([IO.Path]::IsPathRooted($OutputDirectory)){[IO.Path]::GetFullPath($OutputDirectory)}else{[IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))}
$allowed=[IO.Path]::GetFullPath((Join-Path $repo artifacts))+[IO.Path]::DirectorySeparatorChar
if(-not $output.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be under artifacts.'}
for($ancestor=$output;$ancestor -and $ancestor.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase);$ancestor=Split-Path $ancestor){if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Output may not contain links.'}}
if((Test-Path -LiteralPath (Join-Path $repo artifacts)) -and ((Get-Item -LiteralPath (Join-Path $repo artifacts)).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Artifacts may not be a link.'}
New-Item -ItemType Directory -Path $output -Force|Out-Null
& uv run --no-project python (Join-Path $PSScriptRoot Extract-LegacyLineReference.py) $output
if($LASTEXITCODE -ne 0){throw '元関数抽出失敗'}
$sources=@('Src/stringdiffs.cpp','Src/Common/UnicodeString.cpp','Externals/crystaledit/editlib/utils/icu.cpp','Externals/crystaledit/editlib/utils/string_util.cpp') | ForEach-Object {Join-Path $repo $_}
$allSources=$sources+@('Src/MergeDocDiffSync.cpp','Src/MergeDocLineDiffs.cpp','Src/MergeDoc.h','Src/DiffList.h','Src/DiffList.cpp','Src/DiffTextBuffer.cpp') | ForEach-Object {if([IO.Path]::IsPathRooted($_)){$_}else{Join-Path $repo $_}}
$hashes=@($allSources | ForEach-Object {@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash}})
$msvc='C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231'
$sdk='C:/Program Files (x86)/Windows Kits/10';$version='10.0.28000.0'
$compiler=Join-Path $msvc bin/Hostx64/x64/cl.exe
$env:INCLUDE=@((Join-Path $msvc include),(Join-Path $sdk "Include/$version/ucrt"),(Join-Path $sdk "Include/$version/shared"),(Join-Path $sdk "Include/$version/um")) -join ';'
$env:LIB=@((Join-Path $msvc lib/x64),(Join-Path $sdk "Lib/$version/ucrt/x64"),(Join-Path $sdk "Lib/$version/um/x64")) -join ';'
$env:PATH=(Split-Path $compiler)+';'+$env:PATH
$stem=if($CorrectPairDummy){'corrected'}else{'original'}
$exe=Join-Path $output ($stem+'-line-probe.exe')
$arguments=@('/nologo','/std:c++17','/EHsc','/Y-','/utf-8','/DUNICODE','/D_UNICODE','/W3',('/Fe'+$exe),('/Fo'+$output+[IO.Path]::DirectorySeparatorChar),('/I'+(Join-Path $PSScriptRoot LegacyLineReference)),('/I'+$output))
foreach($include in @('Src','Src/Common','Externals/crystaledit/editlib','Externals/crystaledit/editlib/utils','Externals/boost','Src/diffutils','Src/diffutils/src','Src/diffutils/lib')){$arguments+='/I'+(Join-Path $repo $include)}
$arguments+=Join-Path $PSScriptRoot Legacy-Line-Probe.cpp
if($CorrectPairDummy){$arguments+='/DCORRECT_PAIR_DUMMY'}
$arguments+=$sources;$arguments+=@('/link','Kernel32.lib','Shlwapi.lib')
$hashes | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output source-hashes.json) -Encoding utf8
@{sourceSha=(& git -C $repo rev-parse HEAD);purpose='元関数抽出+buffer adapter、旧GUI実測ではない';correctPairDummy=[bool]$CorrectPairDummy;compiler=$compiler;compilerVersion=(Get-Item $compiler).VersionInfo.FileVersion;os=[Environment]::OSVersion.VersionString;arguments=$arguments;zeroSide='空側の旧投影アクセスはskipしAdjustのone-sidedを実行'} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output ($stem+'-metadata.json')) -Encoding utf8
$buildLog=Join-Path $output ($stem+'-build.log');$runLog=Join-Path $output ($stem+'-run.log')
& $compiler @arguments *> $buildLog
if($LASTEXITCODE -ne 0){Get-Content $buildLog -Tail 22;throw '原本関数compile失敗'}
if($CaseName){& $exe $CaseName > (Join-Path $output $GoldenName) 2> $runLog}else{& $exe > (Join-Path $output $GoldenName) 2> $runLog}
if($LASTEXITCODE -ne 0){Get-Content $runLog -Tail 8;throw '原本関数実行失敗'}
foreach($source in $hashes){if((Get-FileHash -LiteralPath $source.path).Hash -ne $source.sha256){throw '原本変更'}}
$golden=Get-Content (Join-Path $output $GoldenName) -Raw | ConvertFrom-Json
@{cases=$golden.cases.Count;sourceUnchanged=$true;locale=$golden.locale} | ConvertTo-Json

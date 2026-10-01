param([string]$OutputDirectory='artifacts/verification/gnu-line/reproduced')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$output=if([IO.Path]::IsPathRooted($OutputDirectory)){[IO.Path]::GetFullPath($OutputDirectory)}else{[IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))}
$allowed=[IO.Path]::GetFullPath((Join-Path $repo artifacts/verification/gnu-line))+[IO.Path]::DirectorySeparatorChar
if(-not $output.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be under artifacts/verification/gnu-line.'}
for($ancestor=$output;$ancestor -and $ancestor.StartsWith([IO.Path]::GetFullPath($repo),[StringComparison]::OrdinalIgnoreCase);$ancestor=Split-Path $ancestor){if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Output may not contain links.'}}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$sourcePaths=@('Src/diffutils/src/analyze.c','Src/diffutils/src/io.c','Src/diffutils/src/util.c','Src/diffutils/src/diff.h','Src/diffutils/src/system.h','Src/diffutils/config.h','Src/diffutils/lib/cmpbuf.c','Src/diffutils/lib/cmpbuf.h','Src/COPYING','Src/CompareOptions.cpp','Src/DiffWrapper.cpp','Src/MergeDoc.cpp','Src/DiffTextBuffer.cpp','Externals/crystaledit/editlib/ccrystaltextbuffer.cpp')
$hashes=@($sourcePaths|ForEach-Object{@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()}})
$msvc='C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231'
$sdk='C:/Program Files (x86)/Windows Kits/10';$version='10.0.28000.0'
$compiler=Join-Path $msvc bin/Hostx64/x64/cl.exe
$env:INCLUDE=@((Join-Path $msvc include),(Join-Path $sdk "Include/$version/ucrt"),(Join-Path $sdk "Include/$version/shared"),(Join-Path $sdk "Include/$version/um")) -join ';'
$env:LIB=@((Join-Path $msvc lib/x64),(Join-Path $sdk "Lib/$version/ucrt/x64"),(Join-Path $sdk "Lib/$version/um/x64")) -join ';'
$env:PATH=(Split-Path $compiler)+';'+$env:PATH
$common=@('/nologo','/Y-','/utf-8','/DWIN32','/D_CRT_SECURE_NO_WARNINGS','/W3','/Od','/Zi')
foreach($include in @('Src/diffutils','Src/diffutils/src','Src/diffutils/lib')){$common+='/I'+(Join-Path $repo $include)}
$commands=@();$objects=@()
$units=@(@{path='build/LegacyGnuReference/OriginalTranslationUnit.c';language='/TC'},@{path='Src/diffutils/src/util.c';language='/TC'},@{path='Src/diffutils/lib/cmpbuf.c';language='/TC'},@{path='build/LegacyGnuReference/UnsupportedPaths.c';language='/TC'},@{path='build/Legacy-Gnu-Probe.cpp';language='/TP'})
$buildLog=Join-Path $output build.log
''|Set-Content -LiteralPath $buildLog -Encoding utf8
foreach($unit in $units){
    $object=Join-Path $output (([IO.Path]::GetFileNameWithoutExtension($unit.path))+'.obj')
    $arguments=$common+@('/c',$unit.language,('/Fo'+$object),('/Fd'+(Join-Path $output probe-compile.pdb)))
    if($unit.language -eq '/TP'){$arguments+=@('/std:c++17','/EHsc')}
    $arguments+=Join-Path $repo $unit.path
    $commands+=@{executable=$compiler;arguments=$arguments}
    & $compiler @arguments *>> $buildLog
    if($LASTEXITCODE -ne 0){Get-Content -LiteralPath $buildLog -Tail 30;throw 'GNU original compile failed.'}
    $objects+=$object
}
$exe=Join-Path $output legacy-gnu-probe.exe
$arguments=@('/nologo',('/Fe'+$exe))+$objects+@('/link','Kernel32.lib','/DEBUG',('/PDB:'+(Join-Path $output legacy-gnu-probe.pdb)))
$commands+=@{executable=$compiler;arguments=$arguments}
& $compiler @arguments *>> $buildLog
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath $buildLog -Tail 30;throw 'GNU original link failed.'}
$adapterPaths=@('build/LegacyGnuReference/OriginalTranslationUnit.c','build/LegacyGnuReference/UnsupportedPaths.c','build/LegacyGnuReference/README.md','build/Legacy-Gnu-Probe.cpp','build/Generate-LegacyGnuReference.ps1','build/Extract-LegacyGnuReference.py')
$adapterHashes=@($adapterPaths|ForEach-Object{@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $_)).Hash.ToLowerInvariant()}})
@{schemaVersion=1;sourceCommit=(& git -C $repo rev-parse HEAD);license='GNU original: GPL-2.0-or-later; see Src/COPYING and source headers';sourceHashes=$hashes;adapterHashes=$adapterHashes;compiler=$compiler;compilerVersion=(Get-Item -LiteralPath $compiler).VersionInfo.FileVersion;sdkVersion=$version;architecture='win-x64';os=[Environment]::OSVersion.VersionString;powershell=$PSVersionTable.PSVersion.ToString();compileCommands=$commands;compileEnvironment=@{INCLUDE=$env:INCLUDE;LIB=$env:LIB};scope='Unmodified analyze.c+io.c in one C TU; complete util.c+cmpbuf.c; regular-file open/stat and output-only observer; no old GUI execution';unsupportedStubs=@('moved_block_analysis: exits 90 if reached','print_context_header: exits 91 if reached');cleanupCandidates=@($objects)+@($exe,(Join-Path $output legacy-gnu-probe.pdb),(Join-Path $output legacy-gnu-probe.ilk),(Join-Path $output probe-compile.pdb));reproduction="pwsh -NoProfile -File build/Generate-LegacyGnuReference.ps1 -OutputDirectory $OutputDirectory"} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output metadata.json) -Encoding utf8
& uv run --no-project python (Join-Path $PSScriptRoot Extract-LegacyGnuReference.py) $exe $output
if($LASTEXITCODE -ne 0){throw 'GNU original execution/validation failed.'}
foreach($source in $hashes){if((Get-FileHash -LiteralPath (Join-Path $repo $source.path)).Hash.ToLowerInvariant() -ne $source.sha256){throw ('Original modified: '+$source.path)}}
@{sourceUnchanged=$true;sourceCount=$hashes.Count;goldenSha256=(Get-FileHash -LiteralPath (Join-Path $output legacy-gnu-golden.json)).Hash.ToLowerInvariant()}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output source-verification.json) -Encoding utf8
Get-Content -LiteralPath (Join-Path $output summary.json)

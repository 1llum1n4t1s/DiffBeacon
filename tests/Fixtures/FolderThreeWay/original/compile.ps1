param([switch]$Compile,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
if(-not $Compile){throw 'Parent native-start instruction required.'}
$outputPath=[IO.Path]::GetFullPath($Output)
$allowed=$PSScriptRoot+[IO.Path]::DirectorySeparatorChar
if(-not $outputPath.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Attempt output must be below harness.'}
if(Test-Path -LiteralPath $outputPath){throw 'Attempt must be fresh; old results are retained.'}
for($path=$outputPath;$path.StartsWith($PSScriptRoot,[StringComparison]::OrdinalIgnoreCase);$path=Split-Path $path){if((Test-Path $path)-and((Get-Item $path).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Links are forbidden.'}}
# Manifestは原文provenance自身と全adapter/slice/copyを固定。外部sourceも照合。
foreach($record in (Get-Content (Join-Path $PSScriptRoot 'sha256-manifest.json') -Raw|ConvertFrom-Json)){
 $path=Join-Path $PSScriptRoot $record.path
 if((Get-FileHash $path).Hash -ne $record.sha256 -or (Get-Item $path).Length -ne $record.bytes){throw "Harness manifest mismatch: $($record.path)"}
}
foreach($record in (Get-Content (Join-Path $PSScriptRoot 'provenance.json') -Raw|ConvertFrom-Json)){
 $source=Join-Path $repo $record.source
 if((Get-FileHash $source).Hash -ne $record.sourceSha256){throw "Original SHA changed: $($record.source)"}
 if($record.slice){$lines=[Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($source)) -split '(?<=\n)';$expected=[Text.UTF8Encoding]::new($false).GetBytes(($lines[($record.first-1)..($record.last-1)] -join ''));$actual=[IO.File]::ReadAllBytes((Join-Path $PSScriptRoot $record.slice));if($null -ne $record.byteStart){$sourceBytes=[IO.File]::ReadAllBytes($source);$expected=$sourceBytes[$record.byteStart..($record.byteStart+$record.byteLength-1)]};if([Convert]::ToBase64String($expected) -ne [Convert]::ToBase64String($actual)){throw 'Slice bytes differ from original'}}
 if($record.copy){if((Get-FileHash (Join-Path $PSScriptRoot $record.copy)).Hash -ne $record.sourceSha256){throw 'Copy differs from original'}}
}
New-Item -ItemType Directory -Path $outputPath|Out-Null
Copy-Item (Join-Path $PSScriptRoot 'sha256-manifest.json') (Join-Path $outputPath 'harness-manifest.json')
Copy-Item (Join-Path $PSScriptRoot 'provenance.json') (Join-Path $outputPath 'provenance.json')
$snapshot=Join-Path $outputPath 'harness-snapshot';New-Item -ItemType Directory $snapshot|Out-Null
foreach($item in (Get-Content (Join-Path $PSScriptRoot 'sha256-manifest.json') -Raw|ConvertFrom-Json)){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $item.path) -Destination (Join-Path $snapshot $item.path)}
Copy-Item (Join-Path $PSScriptRoot 'sha256-manifest.json') (Join-Path $snapshot 'sha256-manifest.json')
$msvc='C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231';$sdk='C:/Program Files (x86)/Windows Kits/10';$version='10.0.28000.0';$compiler=Join-Path $msvc 'bin/Hostx64/x64/cl.exe'
$env:INCLUDE=@((Join-Path $msvc include),(Join-Path $sdk "Include/$version/ucrt"),(Join-Path $sdk "Include/$version/shared"),(Join-Path $sdk "Include/$version/um")) -join ';'
$env:LIB=@((Join-Path $msvc lib/x64),(Join-Path $sdk "Lib/$version/ucrt/x64"),(Join-Path $sdk "Lib/$version/um/x64")) -join ';';$env:PATH=(Split-Path $compiler)+';'+$env:PATH
function Invoke-Recorded([string]$tag,[string[]]$arguments){
 $info=[Diagnostics.ProcessStartInfo]::new($compiler);$info.UseShellExecute=$false;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.WorkingDirectory=$outputPath
 foreach($a in $arguments){$info.ArgumentList.Add($a)}
 $p=[Diagnostics.Process]::new();$p.StartInfo=$info;$requested=[DateTime]::UtcNow;$null=$p.Start();$pidValue=$p.Id;$birth=$p.StartTime.ToUniversalTime();$stdout=$p.StandardOutput.BaseStream;$stderr=$p.StandardError.BaseStream
 $outPath=Join-Path $outputPath "$tag.stdout.bin";$errPath=Join-Path $outputPath "$tag.stderr.bin";$outFile=[IO.File]::Open($outPath,[IO.FileMode]::CreateNew);$errFile=[IO.File]::Open($errPath,[IO.FileMode]::CreateNew)
 $outTask=$stdout.CopyToAsync($outFile);$errTask=$stderr.CopyToAsync($errFile);$p.WaitForExit();$exit=$p.ExitCode;$outTask.GetAwaiter().GetResult();$errTask.GetAwaiter().GetResult();$outFile.Dispose();$errFile.Dispose();$p.Dispose()
 @{executable=$compiler;compilerSha256=(Get-FileHash $compiler).Hash;arguments=$arguments;pid=$pidValue;birthUtc=$birth;requestedUtc=$requested;actualExit=$exit;exitObserved=$true;waitCompleted=$true;disposed=$true;stdoutComplete=$true;stderrComplete=$true;stdoutBytes=(Get-Item $outPath).Length;stderrBytes=(Get-Item $errPath).Length;stdoutSha256=(Get-FileHash $outPath).Hash;stderrSha256=(Get-FileHash $errPath).Hash;compilerVersion=(Get-Item $compiler).VersionInfo.FileVersion;sdk=$version;INCLUDE=$env:INCLUDE;LIB=$env:LIB}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $outputPath "$tag.process.json")
 if($exit -ne 0){throw "$tag failed: $exit"}
}
$common=@('/nologo','/Y-','/utf-8','/DWIN32','/D_CRT_SECURE_NO_WARNINGS','/W3','/Od');foreach($inc in @('Src/diffutils','Src/diffutils/src','Src/diffutils/lib')){$common+='/I'+(Join-Path $repo $inc)}
$units=@(@('build/LegacyGnuReference/OriginalTranslationUnit.c','/TC'),@('Src/diffutils/src/util.c','/TC'),@('Src/diffutils/lib/cmpbuf.c','/TC'),@('build/LegacyGnuReference/UnsupportedPaths.c','/TC'),@((Join-Path $PSScriptRoot 'DiffList.cpp'),'/TP'),@((Join-Path $PSScriptRoot 'producer.cpp'),'/TP'));$objects=@();$index=0
foreach($unit in $units){$obj=Join-Path $outputPath "$index.obj";$src=if([IO.Path]::IsPathRooted($unit[0])){$unit[0]}else{Join-Path $repo $unit[0]};$args=$common+@('/c',$unit[1],"/Fo$obj");if($unit[1] -eq '/TP'){$args+=@('/std:c++17','/EHsc')};$args+=$src;Invoke-Recorded "compile-$index" $args;$objects+=$obj;$index++}
Invoke-Recorded 'link' (@('/nologo',('/Fe'+(Join-Path $outputPath 'producer.exe')))+$objects+@('/link','Kernel32.lib'))
@{complete=$true;executableSha256=(Get-FileHash (Join-Path $outputPath 'producer.exe')).Hash;manifestSha256=(Get-FileHash (Join-Path $outputPath 'harness-manifest.json')).Hash}|ConvertTo-Json|Set-Content (Join-Path $outputPath 'build-status.json')




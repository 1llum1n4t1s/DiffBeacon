for /f %%a in ('powershell -command "Get-Date -Format HH:mm:ss.fff"') do set start=%%a

set workdir=%~dp0BuildTmp\Src
if "%1" == "vs2017" (
  set vsversion=vs2017
)
if "%1" == "latest" (
  set vsversion=vs2017_32bit_latest_64bit
) else (
  set vsversion=vs2017_32bit_vs2022_64bit
)

pushd "%~dp0"

setlocal DisableDelayedExpansion
set "DIFFBEACON_CLEANUP_ROOT=%~dp0"
set "DIFFBEACON_CLEANUP_TARGET=%workdir%"
pwsh -NoProfile -Command "$ErrorActionPreference='Stop'; $base=(Get-Location).ProviderPath; $root=[IO.Path]::GetFullPath($env:DIFFBEACON_CLEANUP_ROOT,$base).TrimEnd('\','/'); $target=[IO.Path]::GetFullPath($env:DIFFBEACON_CLEANUP_TARGET,$base); if(-not $target.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup target outside root'}; if(Test-Path -LiteralPath $target){$targets=@($target); foreach($owned in $targets){for($cursor=$owned; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)){if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup link refused'}}; $pending=[Collections.Generic.Stack[string]]::new(); if((Get-Item -LiteralPath $owned -Force).PSIsContainer){$pending.Push($owned)}; while($pending.Count){foreach($child in Get-ChildItem -LiteralPath $pending.Pop() -Force){if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup child link refused'}; if($child.PSIsContainer){$pending.Push($child.FullName)}}}; Remove-Item -LiteralPath $owned -Recurse -Force -ErrorAction Stop}}"
  if errorlevel 1 (endlocal & exit /b 1)
  endlocal
mkdir %workdir% 2> NUL

git submodule update --init --recursive

git checkout-index -a -f --prefix=%workdir%\
for /F %%d in ('git submodule foreach --quiet --recursive "echo $displaypath"') do (
  pushd "%%d"
  setlocal DisableDelayedExpansion
  set "DIFFBEACON_CLEANUP_ROOT=%~dp0BuildTmp\Src"
  set "DIFFBEACON_CLEANUP_TARGET=%workdir%\%%d"
  pwsh -NoProfile -Command "$ErrorActionPreference='Stop'; $base=(Get-Location).ProviderPath; $root=[IO.Path]::GetFullPath($env:DIFFBEACON_CLEANUP_ROOT,$base).TrimEnd('\','/'); $target=[IO.Path]::GetFullPath($env:DIFFBEACON_CLEANUP_TARGET,$base); if(-not $target.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup target outside root'}; if(Test-Path -LiteralPath $target){$targets=@($target); foreach($owned in $targets){for($cursor=$owned; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)){if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup link refused'}}; $pending=[Collections.Generic.Stack[string]]::new(); if((Get-Item -LiteralPath $owned -Force).PSIsContainer){$pending.Push($owned)}; while($pending.Count){foreach($child in Get-ChildItem -LiteralPath $pending.Pop() -Force){if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup child link refused'}; if($child.PSIsContainer){$pending.Push($child.FullName)}}}; Remove-Item -LiteralPath $owned -Recurse -Force -ErrorAction Stop}}"
  if errorlevel 1 (endlocal & exit /b 1)
  endlocal
  mkdir "%workdir%\%%d"
  git checkout-index -a -f --prefix=%workdir%\%%d\
  popd
)

pushd %workdir%
call DownloadDeps.cmd
call BuildAll.%vsversion%.cmd

for /F %%f in (Build\Releases\files.txt) do (
  copy %%f ..\..\Build\Releases\
)
copy Build\Releases\files.txt ..\..\Build\Releases\
for /d %%d in (Build\Release\?.*.*) do (
  mkdir ..\..\Build\Releases\PDB\%%~nxd\Win32 2> NUL
  xcopy /y /s %%d ..\..\Build\Releases\PDB\%%~nxd\Win32\
)
for /d %%d in (Build\x64\Release\?.*.*) do (
  mkdir ..\..\Build\Releases\PDB\%%~nxd\x64 2> NUL
  xcopy /y /s %%d ..\..\Build\Releases\PDB\%%~nxd\x64\
)
for /d %%d in (Build\ARM64\Release\?.*.*) do (
  mkdir ..\..\Build\Releases\PDB\%%~nxd\ARM64 2> NUL
  xcopy /y /s %%d ..\..\Build\Releases\PDB\%%~nxd\ARM64\
)
popd

rem call UploadToVirusTotal.cmd

popd

for /f %%a in ('powershell -command "Get-Date -Format HH:mm:ss.fff"') do set end=%%a
for /f %%a in ('powershell -command "(New-TimeSpan -Start (Get-Date '%start%') -End (Get-Date '%end%')).TotalMilliseconds"') do set elapsed=%%a

echo %DATE% %start%,%end%,%elapsed%[ms] >> Build\Releases\buildtime.txt

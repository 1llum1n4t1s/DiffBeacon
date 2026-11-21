@echo off
pushd %~dp0
if "%MSYS_HOME%" == "" (
  for %%i in (C D E F G) do (
    if exist %%i:\msys64\ set MSYS_HOME=%%i:\msys64
  )
)

(
echo #!/bin/bash
echo cd $^(cygpath '%CD%'^)
echo po4a --verbose --wrap-po newlines --package-name WinMerge --package-version 2.16 --copyright-holder 'Thingamahoochie Software' --msgid-bugs-address https://github.com/WinMerge/winmerge/issues ./Manual.cfg
) > %MSYS_HOME%\tmp\__update_translations__.sh
%MSYS_HOME%\usr\bin\bash.exe -lc /tmp/__update_translations__.sh
setlocal DisableDelayedExpansion
set "DIFFBEACON_CLEANUP_ROOT=%MSYS_HOME%\tmp"
set "DIFFBEACON_CLEANUP_TARGET=%MSYS_HOME%\tmp\__update_translations__.sh"
pwsh -NoProfile -Command "$ErrorActionPreference='Stop'; $base=(Get-Location).ProviderPath; $root=[IO.Path]::GetFullPath($env:DIFFBEACON_CLEANUP_ROOT,$base).TrimEnd('\','/'); $target=[IO.Path]::GetFullPath($env:DIFFBEACON_CLEANUP_TARGET,$base); if(-not $target.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup target outside root'}; if(Test-Path -LiteralPath $target){$targets=@($target); foreach($owned in $targets){for($cursor=$owned; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)){if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup link refused'}}; $pending=[Collections.Generic.Stack[string]]::new(); if((Get-Item -LiteralPath $owned -Force).PSIsContainer){$pending.Push($owned)}; while($pending.Count){foreach($child in Get-ChildItem -LiteralPath $pending.Pop() -Force){if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup child link refused'}; if($child.PSIsContainer){$pending.Push($child.FullName)}}}; Remove-Item -LiteralPath $owned -Recurse -Force -ErrorAction Stop}}"
  if errorlevel 1 (endlocal & exit /b 1)
  endlocal
popd

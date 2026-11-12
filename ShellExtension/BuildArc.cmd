pushd "%~dp0"

setlocal
set PATH="%ProgramFiles%\7-zip";"%ProgramFiles(x86)%\7-zip";%PATH%
set DISTDIR=..\Build\Releases

mkdir ..\Build\ShellExtension 2> NUL
mkdir ..\Build\ShellExtension\x64 2> NUL
mkdir ..\Build\ShellExtension\ARM 2> NUL
mkdir ..\Build\ShellExtension\ARM64 2> NUL

copy /y "..\Build\x86\Release\ShellExtensionU.dll" ..\Build\ShellExtension\
copy /y "..\Build\x64\Release\ShellExtensionX64.dll" ..\Build\ShellExtension\
copy /y "..\Build\x64\Release\WinMergeContextMenu.dll" ..\Build\ShellExtension\x64
copy /y "..\Build\ARM\Release\ShellExtensionARM.dll" ..\Build\ShellExtension\
copy /y "..\Build\ARM64\Release\ShellExtensionARM64.dll" ..\Build\ShellExtension\
copy /y "..\Build\ARM64\Release\WinMergeContextMenu.dll" ..\Build\ShellExtension\ARM64
copy /y "..\Build\x64\Release\WinMergeContextMenuPackage.msix" ..\Build\ShellExtension\

powershell -NoLogo -NoProfile -Command "(Get-Item '..\Build\ShellExtension\ShellExtensionX64.dll').VersionInfo.FileVersion" > _tmp_.txt
set /P DLLVERSIONTMP=<_tmp_.txt
set DLLVERSION=%DLLVERSIONTMP: =%
setlocal DisableDelayedExpansion
set "DIFFBEACON_CLEANUP_ROOT=%~dp0"
set "DIFFBEACON_CLEANUP_TARGET=%~dp0_tmp_.txt"
pwsh -NoProfile -Command "$ErrorActionPreference='Stop'; $base=(Get-Location).ProviderPath; $root=[IO.Path]::GetFullPath($env:DIFFBEACON_CLEANUP_ROOT,$base).TrimEnd('\','/'); $target=[IO.Path]::GetFullPath($env:DIFFBEACON_CLEANUP_TARGET,$base); if(-not $target.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup target outside root'}; if(Test-Path -LiteralPath $target){$targets=@($target); foreach($owned in $targets){for($cursor=$owned; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)){if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup link refused'}}; $pending=[Collections.Generic.Stack[string]]::new(); if((Get-Item -LiteralPath $owned -Force).PSIsContainer){$pending.Push($owned)}; while($pending.Count){foreach($child in Get-ChildItem -LiteralPath $pending.Pop() -Force){if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup child link refused'}; if($child.PSIsContainer){$pending.Push($child.FullName)}}}; Remove-Item -LiteralPath $owned -Recurse -Force -ErrorAction Stop}}"
  if errorlevel 1 (endlocal & exit /b 1)
  endlocal

7z.exe a -tzip "%DISTDIR%\ShellExtension-%DLLVERSION%.zip" ..\Build\ShellExtension

popd
goto :eof


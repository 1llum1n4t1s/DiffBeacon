setlocal enabledelayedexpansion
if not defined exepath (
  set exepath=%~dp0\..\Build\x64\Release\WinMergeU.exe
)
set cdate=%date:~0,10%
set cdate=%cdate:/=-%
set ctime=%time::=%
set ctime=%ctime: =%
set outputdir=%~dp0\..\BuildTmp\ReportTest\%cdate%-%ctime%
echo %outputdir%
mkdir %outputdir%
for /L %%i in (0,1,5) do ( 
  set cnt=%%i
  set /a lrev=cnt
  set /a rrev=cnt+1
  echo "%exepath%" -minimize -noninteractive -noprefs -cfg Settings/DirViewExpandSubdirs=1 -cfg ReportFiles/ReportType=2 -cfg ReportFiles/IncludeFileCmpReport=1 -r -u -or "%outputdir%\%%i.html" %%1 %%2> %~dp0\tmp.cmd
  git difftool -d --extcmd %~dp0\tmp.cmd R2_10_0~!lrev!..R2_10_0~!rrev!
)
setlocal DisableDelayedExpansion
set "DIFFBEACON_CLEANUP_ROOT=%~dp0"
set "DIFFBEACON_CLEANUP_TARGET=%~dp0tmp.cmd"
pwsh -NoProfile -Command "$ErrorActionPreference='Stop'; $base=(Get-Location).ProviderPath; $root=[IO.Path]::GetFullPath($env:DIFFBEACON_CLEANUP_ROOT,$base).TrimEnd('\','/'); $target=[IO.Path]::GetFullPath($env:DIFFBEACON_CLEANUP_TARGET,$base); if(-not $target.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup target outside root'}; if(Test-Path -LiteralPath $target){$targets=@($target); foreach($owned in $targets){for($cursor=$owned; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)){if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup link refused'}}; $pending=[Collections.Generic.Stack[string]]::new(); if((Get-Item -LiteralPath $owned -Force).PSIsContainer){$pending.Push($owned)}; while($pending.Count){foreach($child in Get-ChildItem -LiteralPath $pending.Pop() -Force){if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup child link refused'}; if($child.PSIsContainer){$pending.Push($child.FullName)}}}; Remove-Item -LiteralPath $owned -Recurse -Force -ErrorAction Stop}}"
  if errorlevel 1 (endlocal & exit /b 1)
  endlocal

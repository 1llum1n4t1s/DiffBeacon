setlocal DisableDelayedExpansion
set "DIFFBEACON_CLEANUP_ROOT=%~dp0"
set "DIFFBEACON_CLEANUP_TARGET=%~dp0English.pot"
pwsh -NoProfile -Command "$ErrorActionPreference='Stop'; $base=(Get-Location).ProviderPath; $root=[IO.Path]::GetFullPath($env:DIFFBEACON_CLEANUP_ROOT,$base).TrimEnd('\','/'); $target=[IO.Path]::GetFullPath($env:DIFFBEACON_CLEANUP_TARGET,$base); if(-not $target.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup target outside root'}; if(Test-Path -LiteralPath $target){$targets=@($target); foreach($owned in $targets){for($cursor=$owned; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)){if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup link refused'}}; $pending=[Collections.Generic.Stack[string]]::new(); if((Get-Item -LiteralPath $owned -Force).PSIsContainer){$pending.Push($owned)}; while($pending.Count){foreach($child in Get-ChildItem -LiteralPath $pending.Pop() -Force){if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Cleanup child link refused'}; if($child.PSIsContainer){$pending.Push($child.FullName)}}}; Remove-Item -LiteralPath $owned -Recurse -Force -ErrorAction Stop}}"
  if errorlevel 1 (endlocal & exit /b 1)
  endlocal
cscript "%~dp0CreateMasterpotFile.js"
powershell -executionpolicy remotesigned -file "%~dp0UpdatePoFilesFromPotFile.ps1"
pause

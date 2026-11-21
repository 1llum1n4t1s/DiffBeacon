$vspath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
$instid = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property instanceId
Import-Module "$vspath\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell $instid -DevCmdArguments -arch=x64

cd $(Split-Path $MyInvocation.MyCommand.Path -Parent)

if (($env:SIGNBAT_PATH -eq "") -or !(Test-Path publisher.txt) -or (($args[0] -ne $null) -and ($args[0].ToLower() -eq "selfsignedpackage"))) {
  $publisher = "CN=winmergedev"
} else {
  $publisher = Get-Content publisher.txt
  $certList = dir Cert:\CurrentUser\My | where { $_.Subject -eq $publisher }
  if ($certList.Length -eq 0) {
    $publisher = "CN=winmergedev"
  }
}

Get-Content WinMergePkg\AppxManifest.xml.in | % { $_ -replace '\${Publisher}',$publisher } | Set-Content WinMergePkg\AppxManifest.xml
if (Test-Path WinMergeContextMenuPackage.msix) { & {
    param([string[]]$Paths, [string]$Root, [string]$Ledger, [switch]$ReturnReceipt, [switch]$AllowLeafLink, [switch]$IgnoreMissing)
    if ([string]::IsNullOrWhiteSpace($Root) -or -not [IO.Path]::IsPathFullyQualified($Root)) { throw 'Absolute cleanup root required' }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    if ($rootPath -eq [IO.Path]::GetPathRoot($Root).TrimEnd('\','/')) { throw 'Filesystem root cleanup prohibited' }
    if ($Paths.Count -eq 0) { throw 'Empty cleanup target set' }
    $rootItem = Get-Item -LiteralPath $rootPath -Force -ErrorAction Stop
    if (-not $rootItem.PSIsContainer) { throw 'Cleanup root must be a directory' }
    $validated = [Collections.Generic.List[string]]::new()
    $owned = @(foreach ($path in $Paths) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not [IO.Path]::IsPathFullyQualified($path)) { throw 'Absolute cleanup target required' }
        $full = [IO.Path]::GetFullPath($path).TrimEnd('\','/')
        if (-not $full.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Cleanup target outside root: $full" }
        foreach ($prior in $validated) {
            if ($full -eq $prior -or $full.StartsWith($prior + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $prior.StartsWith($full + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Duplicate or overlapping cleanup targets' }
        }
        $validated.Add($full)
        if (-not (Test-Path -LiteralPath $full) -and -not $IgnoreMissing) { throw "Cleanup target missing: $full" }
        for ($cursor = $full; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
            if (Test-Path -LiteralPath $cursor) {
                $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and -not ($AllowLeafLink -and $cursor -eq $full)) { throw "Cleanup link refused: $cursor" }
            }
        }
        if (Test-Path -LiteralPath $full) {
            $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
            if ($item.PSIsContainer -and -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($full)
                while ($pending.Count) {
                    foreach ($child in Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop) {
                        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Cleanup child link refused: $($child.FullName)" }
                        if ($child.PSIsContainer) { $pending.Push($child.FullName) }
                    }
                }
            }
        }
        $full
    })
    if ($Ledger -and @( $owned | Where-Object { $Ledger -eq $_ -or $Ledger.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }).Count) { throw 'Cleanup ledger is inside target' }
    $entries = @(foreach ($full in $owned) {
        if (Test-Path -LiteralPath $full) {
            $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Remove-Item -LiteralPath $full -Force -ErrorAction Stop }
            else { Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop }
        }
        if (Test-Path -LiteralPath $full) { throw "Cleanup target remains: $full" }
        [pscustomobject]@{ original = @{Path=$full}; sourceAbsent=$true }
    })
    if ($Ledger) {
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Ledger)) -Force | Out-Null
        [pscustomobject]@{state='complete'; items=$entries; protocol='direct-delete'} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Ledger -Encoding utf8
    }
    if ($ReturnReceipt) { foreach ($full in $owned) { [pscustomobject]@{Target=$full; State='complete'; LedgerPath=$Ledger} } }
} -Paths (Join-Path $PSScriptRoot "WinMergeContextMenuPackage.msix") -Root $PSScriptRoot }
MakeAppx.exe pack /d WinMergePkg /p WinMergeContextMenuPackage.msix /nv

if ($publisher -eq "CN=winmergedev") {
  $certList = dir Cert:\CurrentUser\My | where { $_.Subject -eq $publisher  }
  if ($certList.Length -eq 0)  {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName "Your friendly name goes here" -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
  } else {
    $cert = $certList[0]
  }

  $certPassword = ConvertTo-SecureString -String "test" -Force -AsPlainText
  Export-PfxCertificate -Cert $cert -FilePath selfsigncert.pfx -Password $certPassword
  Export-Certificate -Type CERT -Cert $cert -FilePath selfsigncert.cer

  SignTool.exe sign /fd SHA256 /a /f selfsigncert.pfx /p test /v WinMergeContextMenuPackage.msix
} else {
  & $env:SIGNBAT_PATH WinMergeContextMenuPackage.msix
}

foreach($i in @("", "X64\", "ARM64\")) {
  foreach($j in @("Debug", "Release")) {
    if (!(Test-Path ..\Build\$i$j)) { mkdir ..\Build\$i$j }
    copy WinMergeContextMenuPackage.msix ..\Build\$i$j
  }
}

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [Alias('Rid')]
    [ValidateSet('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier,
    [switch] $SkipVerification,
    # CI用。E2Eの限定selectorが独立readerと一緒に実行する同じ検査を、全体自己検証から省く（時間の二重消費を避ける）。
    # 既定では全検査を実行する。省いた事実はui-report.jsonのskippedE2ECoveredとpublish-manifest.jsonのselfTestScopeに残る。
    [switch] $SkipE2ECoveredSelfTest,
    [switch] $CheckOutputOnly,
    [switch] $OwnerAttestsQuiescentAndNoMixedWork,
    [string] $OutputRoot,
    [string] $BuildArtifactsPath,
    [ValidateRange(1, 3600)]
    [int] $VerificationTimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. "$PSScriptRoot/DevelopmentOutput.ps1"
$developmentRoot = Get-DiffBeaconWorkRoot
Assert-DiffBeaconEnvironment $developmentRoot
$isMacTarget = $RuntimeIdentifier.StartsWith('osx-')
if (($isMacTarget -and -not $IsMacOS) -or (-not $isMacTarget -and -not $IsWindows)) {
    throw 'Native AOT は対象と同じ OS 上で発行してください。Windows と macOS 間のクロスコンパイルには対応していません。'
}
$defaultWork = if ($developmentRoot) { Join-Path $developmentRoot ('publish-' + [guid]::NewGuid().ToString('N')) } else { Join-Path ([IO.Path]::GetTempPath()) ('Codex/DiffBeacon/publish-' + [guid]::NewGuid().ToString('N')) }
$publishRoot = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot, $repoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) } else { Join-Path $defaultWork 'publish' }
$buildArtifactsRoot = if ($BuildArtifactsPath) { [IO.Path]::GetFullPath($BuildArtifactsPath, $repoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) } else { Join-Path $defaultWork 'build' }
if ($developmentRoot) {
    $null = Assert-DiffBeaconPath $publishRoot $developmentRoot
    $null = Assert-DiffBeaconPath $buildArtifactsRoot $developmentRoot
}
# 作業出力は外部の作業別領域へ限定し、作成前に全祖先を検査する。
$allowedOutputRoots = @(
    (Join-Path ([IO.Path]::GetTempPath()) 'Codex/DiffBeacon'),
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Codex/TaskArtifacts/DiffBeacon')
)
if ($developmentRoot) { $allowedOutputRoots = @($developmentRoot) }
elseif ($env:DIFFBEACON_WORK_DIRECTORY) { $allowedOutputRoots += $env:DIFFBEACON_WORK_DIRECTORY }
if ($env:RUNNER_TEMP -and $env:GITHUB_ACTIONS -eq 'true') {
    $allowedOutputRoots += Join-Path $env:RUNNER_TEMP 'Codex/DiffBeacon'
}
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$allowedOutput = $false
foreach ($allowedRoot in $allowedOutputRoots) {
    $prefix = [IO.Path]::GetFullPath($allowedRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($publishRoot.StartsWith($prefix, $comparison)) { $allowedOutput = $true; break }
}
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$codexRoot = if ($env:CODEX_HOME) { [IO.Path]::GetFullPath($env:CODEX_HOME) } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex' }
$codexPrefix = $codexRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $allowedOutput -or $publishRoot.StartsWith($repoPrefix, $comparison) -or $publishRoot.Equals($repoRoot, $comparison) -or $publishRoot.StartsWith($codexPrefix, $comparison) -or $publishRoot.Equals($codexRoot, $comparison) -or $publishRoot -match '(?i)(^|[\\/])(artifacts|\.codex)([\\/]|$)') {
    throw '生成先はrepo／Codexユーザーディレクトリ外のCodex/DiffBeacon作業別領域を指定してください。artifactsという名前のパス要素は使用できません。'
}
if ($buildArtifactsRoot) {
    $buildArtifactsAllowed = $false
    foreach ($allowedRoot in $allowedOutputRoots) {
        $prefix = [IO.Path]::GetFullPath($allowedRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($buildArtifactsRoot.StartsWith($prefix, $comparison)) { $buildArtifactsAllowed = $true; break }
    }
    $publishPrefix = $publishRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $buildArtifactsPrefix = $buildArtifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $buildArtifactsAllowed -or $buildArtifactsRoot.StartsWith($repoPrefix, $comparison) -or $buildArtifactsRoot.Equals($repoRoot, $comparison) -or
        $buildArtifactsRoot.StartsWith($codexPrefix, $comparison) -or $buildArtifactsRoot.Equals($codexRoot, $comparison) -or
        $buildArtifactsRoot -match '(?i)(^|[\\/])(artifacts|\.codex)([\\/]|$)' -or
        $buildArtifactsRoot.Equals($publishRoot, $comparison) -or $buildArtifactsRoot.StartsWith($publishPrefix, $comparison) -or $publishRoot.StartsWith($buildArtifactsPrefix, $comparison)) {
        throw '中間出力先はrepo／Codexユーザーディレクトリと発行出力の外にある別の作業領域を指定してください。artifactsという名前のパス要素は使用できません。'
    }
    # 同じrunのbuild/objは再利用する。境界・発行先との分離・祖先の型/linkは前後で検査する。
    $currentPath = $buildArtifactsRoot
    while ($currentPath) {
        $existingAncestor = $null
        try { $existingAncestor = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] { }
        if ($existingAncestor) {
            if ($existingAncestor.PSProvider.Name -ne 'FileSystem' -or $existingAncestor -isnot [IO.DirectoryInfo]) {
                throw "中間出力先の祖先はファイルシステムのディレクトリである必要があります: $currentPath"
            }
            if ($existingAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "中間出力先にリンクは使用できません: $currentPath"
            }
        }
        $currentPath = [IO.Path]::GetDirectoryName($currentPath)
    }
}
$currentPath = $publishRoot
while ($currentPath) {
    $existingAncestor = $null
    try { $existingAncestor = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop }
    catch [Management.Automation.ItemNotFoundException] { }
    if ($existingAncestor) {
        if ($existingAncestor.PSProvider.Name -ne 'FileSystem' -or $existingAncestor -isnot [IO.DirectoryInfo]) {
            throw "生成先の祖先はファイルシステムのディレクトリである必要があります: $currentPath"
        }
        if ($existingAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "生成先にリンクは使用できません: $currentPath"
        }
    }
    $currentPath = [IO.Path]::GetDirectoryName($currentPath)
}
$outputPath = Join-Path $publishRoot $RuntimeIdentifier
$verificationPath = Join-Path $publishRoot "verification/$RuntimeIdentifier"
$projectPath = Join-Path $repoRoot 'Src/DiffBeacon.App/DiffBeacon.App.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw "アプリのプロジェクトが見つかりません: $projectPath" }

# 生成先を確認してから古い発行物を削除し、異なるビルドの混在を防ぐ。
foreach ($path in @($publishRoot, $outputPath, $verificationPath)) {
    $ancestor = $path
    while ($ancestor) {
        $existingAncestor = $null
        try { $existingAncestor = Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] { }
        if ($existingAncestor) {
            if ($existingAncestor.PSProvider.Name -ne 'FileSystem' -or $existingAncestor -isnot [IO.DirectoryInfo]) {
                throw "生成先の祖先はファイルシステムのディレクトリである必要があります: $ancestor"
            }
            if ($existingAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "生成先にリンクは使用できません: $ancestor"
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
}
if (-not [IO.Path]::GetFullPath($outputPath).StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "発行先が許可されたディレクトリの外です: $outputPath"
}
if (Test-Path -LiteralPath $outputPath) {
    if ($IsWindows) {
        if (-not $OwnerAttestsQuiescentAndNoMixedWork) { throw '旧発行物の利用中プロセスなし・他作業混在なしを確認し、-OwnerAttestsQuiescentAndNoMixedWork を指定してください。' }
        # PSから削除しない。呼出側が正規PrunletをCMD直接で実行するまで発行を止める。
        $cleanupRoot = Join-Path $publishRoot 'publish-cleanup'
        if ($cleanupRoot -match '[^A-Za-z0-9_:\\./-]') { throw '既存発行先は保持しました。CMD用paths-fileを生成できる空白/特殊文字なしの未使用OutputRootへ切り替えてください。' }
        if ($developmentRoot) { $null = Assert-DiffBeaconPath $cleanupRoot $developmentRoot }
        for ($cursor = $cleanupRoot; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
            if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "削除計画のリンク祖先を拒否: $cursor" }
        }
        New-Item -ItemType Directory -Path $cleanupRoot -Force | Out-Null
        $cleanupId = [guid]::NewGuid().ToString('N')
        $pathsPath = Join-Path $cleanupRoot "$RuntimeIdentifier-$cleanupId.paths.json"
        $resultPath = Join-Path $cleanupRoot "$RuntimeIdentifier-$cleanupId.delete.json"
        $planPath = Join-Path $cleanupRoot "$RuntimeIdentifier-$cleanupId.plan.json"
        foreach ($plannedPath in @($pathsPath, $resultPath, $planPath)) {
            if ($developmentRoot) { $null = Assert-DiffBeaconPath $plannedPath $developmentRoot -AllowFileLeaf }
            if (Test-Path -LiteralPath $plannedPath) { throw "既存の削除計画は上書きしません: $plannedPath" }
        }
        [IO.File]::WriteAllText($pathsPath, (ConvertTo-Json -InputObject @($outputPath)), [Text.UTF8Encoding]::new($false))
        $command = "C:\Users\IMT\.local\Prunlet\bin\prunlet.exe --config C:\Users\IMT\.local\Prunlet\config.json delete --recursive --paths-file $pathsPath > $resultPath"
        $plan = [ordered]@{ state='awaiting-prunlet-cmd-direct'; target=$outputPath; sourceRemoved=$false; pathsFile=$pathsPath; deleteResult=$resultPath; command=$command }
        $encoded = [Text.UTF8Encoding]::new($false).GetBytes(($plan | ConvertTo-Json -Depth 5))
        $stream = [IO.File]::Open($planPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $stream.Write($encoded, 0, $encoded.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        Write-Host "既存発行物を保持し、発行を停止しました。削除計画: $planPath"
        Write-Host "shell=cmd.exe, login=false で直接実行するコマンド: $command"
        throw '呼出側で正規CMD削除の結果と元パス不在を確認してから再実行するか、正本workroot配下の未使用OutputRootで再実行してください。自動削除/purgeは行いません。'
    } elseif (-not $CheckOutputOnly) { Remove-Item -LiteralPath $outputPath -Recurse -Force }
}
if ($CheckOutputOnly) {
    [ordered]@{ outputCheckOnly=$true; output=$outputPath; build=$buildArtifactsRoot; verification=$verificationPath; existingOutput=(Test-Path -LiteralPath $outputPath) } | ConvertTo-Json
    return
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
Push-Location $repoRoot
try {
    $sdkVersion = & dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'dotnet SDK の確認に失敗しました。' }
    $publishArguments = @('publish', $projectPath, '-c', 'Release', '-r', $RuntimeIdentifier, '-p:PublishAot=true', '--self-contained', 'true', '-o', $outputPath, '-noAutoResponse')
    if ($buildArtifactsRoot) { $publishArguments += @('--artifacts-path', $buildArtifactsRoot) }
    # 制限された実行環境で OS 環境変数が欠けても、実際のホスト OS を MSBuild へ伝える。
    if ($IsWindows -and -not $env:OS) { $publishArguments += '-p:OS=Windows_NT' }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "Native AOT 発行に失敗しました: $RuntimeIdentifier" }
    $executableName = if ($isMacTarget) { 'DiffBeacon' } else { 'DiffBeacon.exe' }
    $executablePath = Join-Path $outputPath $executableName
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { throw "ネイティブ実行ファイルが見つかりません: $executablePath" }

    if ($isMacTarget) {
        # ネイティブライブラリも実行ファイルの隣に配置する。
        $publishedFiles = @(Get-ChildItem -LiteralPath $outputPath)
        $bundlePath = Join-Path $outputPath 'DiffBeacon.app'
        $macOsPath = Join-Path $bundlePath 'Contents/MacOS'
        New-Item -ItemType Directory -Path $macOsPath -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $bundlePath 'Contents/Resources') -Force | Out-Null
        foreach ($file in $publishedFiles) { Copy-Item -LiteralPath $file.FullName -Destination $macOsPath -Recurse -Force }
        $bundleVersion = & dotnet msbuild $projectPath -getProperty:Version -nologo -noAutoResponse
        if ($LASTEXITCODE -ne 0) { throw 'バンドル用バージョンの取得に失敗しました。' }
        $escapedVersion = [Security.SecurityElement]::Escape(($bundleVersion | Out-String).Trim())
        $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>DiffBeacon</string>
  <key>CFBundleDisplayName</key><string>DiffBeacon</string>
  <key>CFBundleIdentifier</key><string>com.kagayoi.diffbeacon</string>
  <key>CFBundleExecutable</key><string>DiffBeacon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$escapedVersion</string>
  <key>CFBundleShortVersionString</key><string>$escapedVersion</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
"@
        Set-Content -LiteralPath (Join-Path $bundlePath 'Contents/Info.plist') -Value $plist -Encoding utf8NoBOM
        $executablePath = Join-Path $macOsPath $executableName
        & chmod +x $executablePath
        if ($LASTEXITCODE -ne 0) { throw 'バンドル実行権限の設定に失敗しました。' }
        & plutil -lint (Join-Path $bundlePath 'Contents/Info.plist')
        if ($LASTEXITCODE -ne 0) { throw 'Info.plist の検証に失敗しました。' }
        # upload-artifact による実行権限の欠落を避けるため tar も残す。
        & tar -czf (Join-Path $outputPath 'DiffBeacon.app.tar.gz') -C $outputPath 'DiffBeacon.app'
        if ($LASTEXITCODE -ne 0) { throw 'macOS バンドルのアーカイブに失敗しました。' }
    }

    $hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $targetArchitecture = $RuntimeIdentifier.Split('-')[1]
    $verificationStatus = 'skipped by request'
    $verificationSeconds = $null
    if (-not $SkipVerification) {
        if ($hostArchitecture -eq $targetArchitecture) {
            New-Item -ItemType Directory -Path $verificationPath -Force | Out-Null
            $env:DIFFBEACON_SELFTEST_SKIP_E2E_COVERED = if ($SkipE2ECoveredSelfTest) { '1' } else { $null }
            $startOptions = @{ FilePath = $executablePath; ArgumentList = @('--self-test', ('"' + $verificationPath + '"')); PassThru = $true }
            if ($IsWindows) { $startOptions.WindowStyle = 'Hidden' }
            Write-Host "ネイティブ自己検証: $RuntimeIdentifier（制限 $VerificationTimeoutSeconds 秒）"
            $verificationTimer = [Diagnostics.Stopwatch]::StartNew()
            $process = Start-Process @startOptions
            if (-not $process.WaitForExit($VerificationTimeoutSeconds * 1000)) {
                $process.Kill($true)
                throw "ネイティブ自己検証が制限時間 $VerificationTimeoutSeconds 秒を超えました。進捗は $verificationPath を確認してください。"
            }
            $verificationTimer.Stop()
            $verificationSeconds = $verificationTimer.Elapsed.TotalSeconds
            Write-Host "ネイティブ自己検証の実行時間: $verificationSeconds 秒"
            if ($process.ExitCode -ne 0) { throw "ネイティブ自己検証に失敗しました: $($process.ExitCode)" }
            $verificationStatus = 'passed'
        } else {
            $verificationStatus = "skipped: host $hostArchitecture differs from target $targetArchitecture"
            Write-Warning 'クロスアーキテクチャ発行では自己検証を省略します。対象アーキテクチャのホストで実行してください。'
        }
    }
    $files = @(Get-ChildItem -LiteralPath $outputPath -Recurse -File | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($outputPath, $_.FullName); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [ordered]@{
        runtimeIdentifier = $RuntimeIdentifier
        sdkVersion = ($sdkVersion | Out-String).Trim()
        nativeAot = $true
        selfTest = $verificationStatus
        selfTestScope = if ($SkipE2ECoveredSelfTest) { 'full without units covered by E2E selectors' } else { 'full' }
        verificationTimeoutSeconds = $VerificationTimeoutSeconds
        verificationSeconds = $verificationSeconds
        executable = [IO.Path]::GetRelativePath($repoRoot, $executablePath)
        files = $files
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'publish-manifest.json') -Encoding utf8NoBOM
    Write-Host "発行完了: $outputPath"
} finally { Pop-Location }

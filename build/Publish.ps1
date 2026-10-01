[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [Alias('Rid')]
    [ValidateSet('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier,
    [switch] $SkipVerification
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$isMacTarget = $RuntimeIdentifier.StartsWith('osx-')
if (($isMacTarget -and -not $IsMacOS) -or (-not $isMacTarget -and -not $IsWindows)) {
    throw 'Native AOT は対象と同じ OS 上で発行してください。Windows と macOS 間のクロスコンパイルには対応していません。'
}
$publishRoot = Join-Path $repoRoot 'artifacts/publish'
$outputPath = Join-Path $publishRoot $RuntimeIdentifier
$verificationPath = Join-Path $repoRoot "artifacts/verification/$RuntimeIdentifier"
$projectPath = Join-Path $repoRoot 'Src/DiffBeacon.App/DiffBeacon.App.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw "アプリのプロジェクトが見つかりません: $projectPath" }

# 生成先を確認してから古い発行物を削除し、異なるビルドの混在を防ぐ。
foreach ($path in @((Join-Path $repoRoot 'artifacts'), $publishRoot, $outputPath)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "生成先にリンクは使用できません: $path"
    }
}
if (-not [IO.Path]::GetFullPath($outputPath).StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "発行先が許可されたディレクトリの外です: $outputPath"
}
if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Recurse -Force }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
Push-Location $repoRoot
try {
    $sdkVersion = & dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'dotnet SDK の確認に失敗しました。' }
    $publishArguments = @('publish', $projectPath, '-c', 'Release', '-r', $RuntimeIdentifier, '-p:PublishAot=true', '--self-contained', 'true', '-o', $outputPath)
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
        $bundleVersion = & dotnet msbuild $projectPath -getProperty:Version -nologo
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
    if (-not $SkipVerification) {
        if ($hostArchitecture -eq $targetArchitecture) {
            New-Item -ItemType Directory -Path $verificationPath -Force | Out-Null
            $startOptions = @{ FilePath = $executablePath; ArgumentList = @('--self-test', ('"' + $verificationPath + '"')); PassThru = $true }
            if ($IsWindows) { $startOptions.WindowStyle = 'Hidden' }
            $process = Start-Process @startOptions
            if (-not $process.WaitForExit(180000)) {
                $process.Kill($true)
                throw 'ネイティブ自己検証が制限時間 180 秒を超えました。'
            }
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
        executable = [IO.Path]::GetRelativePath($repoRoot, $executablePath)
        files = $files
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'publish-manifest.json') -Encoding utf8NoBOM
    Write-Host "発行完了: $outputPath"
} finally { Pop-Location }

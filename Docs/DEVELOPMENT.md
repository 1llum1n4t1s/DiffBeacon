# 開発と検証

必要環境は .NET 10 SDK と PowerShell 7。正規のソースディレクトリ名は `Src` であり、macOS でもこの大文字小文字を維持する。通常のビルドは旧 C++ プロジェクトや submodule に依存しない。

```powershell
dotnet build DiffBeacon.slnx -c Release
dotnet run --project Src/DiffBeacon.App/DiffBeacon.App.csproj -c Release --no-build
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output artifacts/e2e/local
```

E2E はアプリを別プロセスで実行し、CLI の終了コード、比較結果、マージ、パッチのバイト列、再帰比較、異常入力を確認する。`artifacts/e2e/local/assertions.json` と入力・出力・標準出力・標準エラーログが再現用成果物になる。UI は次の headless 自己検証で確認する。

プロバイダーの検証にはXML・HTML・DOCX・PPTX・XLSX・TAR、ループバックHTTP、外部実行ファイルのプロトコル異常と応答上限も含む。ソリューション内の`DiffBeacon.FakeProvider`がE2E用実行ファイルを生成する。UI検証は通常のウィンドウとSkia描画を使い、4ペイン・画像・バイナリ・変換後テキスト保存拒否などの結果をPNGとJSONで保存する。

```powershell
dotnet Src/DiffBeacon.App/bin/Release/net10.0/DiffBeacon.dll --self-test artifacts/verification/managed
```

Native AOT の Windows 発行には Visual Studio の C++ ビルドツールと Windows SDK、macOS 発行には Xcode のネイティブツールが必要。発行は対象と同じ OS で実行する。

```powershell
# Windows 上
./build/Publish.ps1 -Rid win-x64
./build/Publish.ps1 -Rid win-arm64
# macOS 上
./build/Publish.ps1 -Rid osx-x64
./build/Publish.ps1 -Rid osx-arm64
```

発行物は `artifacts/publish/<RID>` に生成する。発行スクリプトはこの RID の既存生成物を削除してから再生成する。実行ファイルと同じホストアーキテクチャでは自己検証も実行し、結果を `artifacts/verification/<RID>` に残す。クロスアーキテクチャでは自己検証を省略した理由を manifest に記録する。`-SkipVerification` は発行だけを行う明示指定である。

macOS の成果物は `DiffBeacon.app` と、実行権限を保持する `DiffBeacon.app.tar.gz`。GitHub artifact をダウンロードした場合は tar を展開して起動する。署名・公証・配布はこのスクリプトの工程に含めない。

```powershell
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output artifacts/e2e/native --app artifacts/publish/win-x64/DiffBeacon.exe
```

`.github/workflows/main.yml` は Windows x64 / ARM64、macOS Intel / ARM64 上でビルド、Native AOT 発行、自己検証、E2E を行い、検証成果物と発行物を保存する。CodeQL は C# の手動ビルドを解析する。ローカル結果と GitHub 上の実行結果は区別し、CI の完了は実際の run を確認する。

初回の4構成検証は[GitHub Actions run 36759572632](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36759572632)で全ジョブ成功した。コードコミット・成功数・実測の範囲は[MIGRATION.md](MIGRATION.md#実行した検証)を参照する。ローカル取得した同runのJSON・ログ・PNGは`artifacts/github/36759572632`に保持する。成果物はGitへ登録しない。

マージ結果セッション・詳細フィルターの4構成検証は[GitHub Actions run 36770239518](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36770239518)で全ジョブ成功した。対応するJSON・ログ・PNGは`artifacts/github/36770239518`に保持する。

暗号化・solidアーカイブ比較と7z作成の4構成検証は[GitHub Actions run 36775611278](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36775611278)で全ジョブ成功した。対応するJSON・ログ・PNGと集計は`artifacts/github/36775611278`に保持する。Windows上で省略するUnix権限・Mac大小文字別名の検証は、両Mac runnerで成功した。

Windowsのシンボリックリンク検証は作成権限が必要。権限がないプロセスでは理由を記録してスキップし、権限のある同一マシンで再実行する。Macではファイル保存後のUnix実行権限も検証する。対象OS上のAOT発行にはMicrosoftの[Native AOTの前提条件](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)が適用される。macOS最低14.0は[Avaloniaの対応環境](https://docs.avaloniaui.net/docs/overview/supported-platforms)に合わせてInfo.plistへ記載する。

既知のローカル環境観測: SDK `10.0.401` の `dotnet --version` と MSBuild は起動する一方、`dotnet --info` は workload MSI の `InstallerBase` 初期化例外を表示した。原因は未確定で、グローバル設定やインストールを変更していない。この診断表示の失敗とプロジェクトのビルド成否は別々に確認する。

機能の対応状況と保留事項は [MIGRATION.md](MIGRATION.md) を参照する。

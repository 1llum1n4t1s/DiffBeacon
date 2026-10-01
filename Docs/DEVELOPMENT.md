# 開発と検証

## 検証成果物の容量管理

画像変換のGUI自己検証では固定goldenをgzipで同梱し、約2.46 MBのJSONを約71 KBへ圧縮する。`python build/Generate-ImageTransformUiFixture.py`で再生成し、展開後の全bytesと正本SHAを照合する。E2Eは同じ正本をそのまま読み、通常の画像処理にはgoldenを使わない。

ローカルで展開保持するのは最新のCLI／UI検証と、次の実装に必要な原本採取に限る。E2Eの`--output`には空き容量のあるドライブを指定し、次の実行前に前回の完了済み出力を整理する。ビルドの`bin/obj`は通常の増分ビルドで再利用し、毎回コピーしない。GitHubの4RID検証は維持し、必要な成果物を一度だけ取得・照合した後、古いrunを圧縮する。

Windowsでは[Compact-Evidence.ps1](../build/Compact-Evidence.ps1)とPython 3.11以降の標準ライブラリの[CompactEvidence.py](../build/CompactEvidence.py)で過去の検証ディレクトリをZIP64へ格納できる。ZIPは入力とは別の場所へ置く。各entryのSHA・サイズと入力の一覧・更新日時・属性が不変であることを確認し、ZIP全体のSHAも照合してから、同じPowerShellセッションで展開元を除去する。リンクと許可root外の操作は拒否する。失敗ログ・入力・出力・PNG・発行物も省略しない。

```powershell
./build/Compact-Evidence.ps1 -SourcePath "$PWD/artifacts/e2e/previous" `
  -ArchiveRoot 'E:/DiffBeacon-artifacts/retained/local' -PythonPath '<Python 3の実行ファイル>'
```

外部の証拠rootを整理するときだけ`-AdditionalEvidenceRoot 'E:/DiffBeacon-artifacts'`を指定する。処理に失敗した場合は元データと途中ZIPを保持する。ZIPが完成している場合は同じ入力と格納先に`-ExistingArchive '<途中ZIPの絶対パス>'`を加えると、全entryを再照合して再開できる。既存のZIP・報告JSONは上書きしない。

2026-10-02の保存先は`E:/DiffBeacon-artifacts/retained`。元パスとZIPの対応・SHA・照合結果は隣接JSONと`artifacts/retention/index.json`へ記録する。以前の文書に記載された過去の展開パスはこの索引から参照する。ZIPをその元ディレクトリへ展開すれば各ファイルを読み直せる。元の属性・mode・更新日時はZIP内の`.diffbeacon-retention-manifest.json`に保存されるが、展開ツールによる属性復元の対応は異なるため必要時にmanifestを参照する。ソース・固定fixture・Git履歴、共有キャッシュ、他プロジェクトはこの清掃の対象にしない。

画像領域の原本参照は `uv run --no-project python tests/Fixtures/ImageRegions/generate-reference.py --output artifacts/verification/image-regions-reference/reproduced` で再採取する。通常buildやE2EにはMSVCを追加せず固定goldenを使う。製品を別プロセスで照合する限定実行は `--image-regions-only`。原本抽出・ライセンス・SHAは[fixture説明](../tests/Fixtures/ImageRegions/README.md)、診断CLIの契約は[画像の説明](IMAGE-VIEWER.md#原本照合の入口)を参照する。

画像強調の再採取は `uv run --no-project python tests/Fixtures/ImageHighlight/generate-reference.py --output artifacts/verification/image-highlight-reference/reproduced`。固定72件は限定E2E `--image-highlight-only` と全体へ接続する。通常GUI・CLI・単体／包装HTMLの描画画素を照合し、UI自己検証は実Bitmap・選択・強調解除・三者ページ操作を保存する。[fixture](../tests/Fixtures/ImageHighlight/README.md)の出典・SHA・GPLを保持する。

静止画像コピー核の再採取は `uv run --no-project python tests/Fixtures/ImageCopy/generate-reference.py --output artifacts/verification/image-copy-reference/reproduced`。固定143ケース・935状態を実画像の限定 `--image-copy-only` と全体 E2Eへ照合する。[fixture](../tests/Fixtures/ImageCopy/README.md)のFreeImage未実測のadapter境界を維持し、[診断CLI](IMAGE-VIEWER.md#静止画像コピー核の診断)を使う。GUIの静止画編集はheadless自己検証で実controlの方向・領域／全領域・三者auto・共有履歴・readonly・取消／失敗時の状態保持、PNG別名保存と編集済みHTML、保存paneのパス更新とdirty包装拒否を確認する。元形式／多ページ編集保存と通常デスクトップの手動操作は未実測または未対応。今回のGUI接続ではmanaged buildの警告0とmanaged headless UI終了0を観測済みで、件数の確定とNative AOT／CIの検証はまだ行っていない。結果の確定値は[MIGRATION.md](MIGRATION.md)へ集約する。

画像HTMLは `--report-project INPUT_PROJECT OUTPUT_HTML [--entry N] [--left-frame N [--middle-frame N] --right-frame N] [--threshold X]` で生成する。番号・閾値は画像比較にだけ指定でき、保存した画像設定を既定として使う。画像設定を省略した既存プロジェクトは全同番号フレームと閾値0。明示したCLI指定が保存設定より優先する。限定E2Eは `--image-reports-only`、契約は [画像の説明](IMAGE-VIEWER.md) と [E2Eの手順](../tests/DiffBeacon.E2E/README.md) を参照する。

GNU既定算法の原本参照は `pwsh -NoProfile -File build/Generate-LegacyGnuReference.ps1` で採取する。[原本とadapterの境界](../build/LegacyGnuReference/README.md)を確認する。`--gnu-line-script INPUT_JSON [--max-work N]` は入力 `{ "left": [同値クラスID], "right": [同値クラスID], "classCount": ID上限 }` から変更scriptを返す開発用CLI。原本の入力変換と算法を分けて照合する限定E2Eは `--gnu-line-only`、通常文書比較の元bytesからの接続検証は `--gnu-text-only` を使う。表の復号キーへの接続は `--gnu-table-only` で原本143ケースのCLI/HTMLと共有予算を照合する。これらは全体E2Eにも含める。通常 `--compare` は `lineWorkUsed`・`lineFallback`・`lineFallbackReason` と全元行を返す。予算契約は [Core README](../Src/DiffBeacon.Core/README.md#テキスト比較と保存) を参照する。

必要環境は.NET 10 SDKとPowerShell 7。アーカイブE2Eの独立検証にはPython 3（CIは3.13）も使い、pipパッケージは不要。Pythonはアプリの実行・発行依存ではない。正規のソースディレクトリ名は`Src`であり、macOSでもこの大文字小文字を維持する。通常ビルドは旧C++プロジェクトやsubmoduleに依存しない。

表行対応の元関数fixtureを再採取する場合だけ、WindowsのMSVC・SDK・ICUを使う `pwsh -NoProfile -File build/Generate-LegacyLineReference.ps1` を実行する。出力は`artifacts/verification/table-line-alignment/reproduced`。元関数の抽出・buffer adapter・固定コンパイラパスと採取範囲は[fixtureの出典](../tests/Fixtures/LineAlignment/README.md)を参照する。これらはアプリの通常buildや実行依存に含めない。

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

発行物は `artifacts/publish/<RID>` に生成する。発行スクリプトはこの RID の既存生成物を削除してから再生成する。実行ファイルと同じホストアーキテクチャでは自己検証も実行し、結果を `artifacts/verification/<RID>` に残す。自己検証のプロセス制限は180秒で、超過時は子プロセスも終了して発行失敗にする。Intel MacのGitHub runnerでAPNG原画40件まで確認できた後、後続の表UI検証中に旧60秒制限へ到達したため延長した。全検証項目・合否条件は維持し、改定後の同構成の成功はCI実測まで未確認とする。クロスアーキテクチャでは自己検証を省略した理由を manifest に記録する。`-SkipVerification` は発行だけを行う明示指定である。

macOS の成果物は `DiffBeacon.app` と、実行権限を保持する `DiffBeacon.app.tar.gz`。GitHub artifact をダウンロードした場合は tar を展開して起動する。署名・公証・配布はこのスクリプトの工程に含めない。

```powershell
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output artifacts/e2e/native --app artifacts/publish/win-x64/DiffBeacon.exe
```

`.github/workflows/main.yml` は Windows x64 / ARM64、macOS Intel / ARM64 上でビルド、Native AOT 発行、自己検証、E2E を行い、検証成果物と発行物を保存する。CodeQL は C# の手動ビルドを解析する。ローカル結果と GitHub 上の実行結果は区別し、CI の完了は実際の run を確認する。

複数比較プロジェクト・読取り専用・表設定の4構成検証は[GitHub Actions run 36786270273](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36786270273)で全ジョブ成功した。コードコミット・成功数・実測の範囲は[MIGRATION.md](MIGRATION.md#実行した検証)を参照する。同runの入力・出力・JSON・ログ・PNGと集計を`artifacts/github/36786270273`に保持する。UIはheadless検証であり、ネイティブファイル選択などの手動実測を含まない。成果物はGitへ登録しない。

初回の4構成検証は[GitHub Actions run 36759572632](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36759572632)で全ジョブ成功した。ローカル取得した同runのJSON・ログ・PNGは`artifacts/github/36759572632`に保持する。

マージ結果セッション・詳細フィルターの4構成検証は[GitHub Actions run 36770239518](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36770239518)で全ジョブ成功した。対応するJSON・ログ・PNGは`artifacts/github/36770239518`に保持する。

暗号化・solidアーカイブ比較と7z作成の4構成検証は[GitHub Actions run 36775611278](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36775611278)で全ジョブ成功した。対応するJSON・ログ・PNGと集計は`artifacts/github/36775611278`に保持する。Windows上で省略するUnix権限・Mac大小文字別名の検証は、両Mac runnerで成功した。

ZIP派生・TAR系の作成／再梱包・全件展開の4構成検証は[GitHub Actions run 36781192763](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36781192763)で全ジョブ成功した。対応するJSON・ログ・PNGとPython独立検証記録は`artifacts/github/36781192763`に保持する。Mac版のNative AOT発行はWindows上でクロスコンパイルせず、同workflowのIntel／ARM64 Mac runnerで行う。実行完了後、ActionsのArtifactsから`DiffBeacon-osx-x64`または`DiffBeacon-osx-arm64`を取得できる。

Windowsのシンボリックリンク検証は作成権限が必要。権限がないプロセスでは理由を記録してスキップし、権限のある同一マシンで再実行する。Macではファイル保存後のUnix実行権限も検証する。対象OS上のAOT発行にはMicrosoftの[Native AOTの前提条件](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)が適用される。macOS最低14.0は[Avaloniaの対応環境](https://docs.avaloniaui.net/docs/overview/supported-platforms)に合わせてInfo.plistへ記載する。

既知のローカル環境観測: SDK `10.0.401` の `dotnet --version` と MSBuild は起動する一方、`dotnet --info` は workload MSI の `InstallerBase` 初期化例外を表示した。原因は未確定で、グローバル設定やインストールを変更していない。この診断表示の失敗とプロジェクトのビルド成否は別々に確認する。

比較文書・レポート・パッチ・プロジェクト包装の4構成検証は[GitHub Actions run 36790193652](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36790193652)で全ジョブ成功した。各Mac版の発行物は同runのArtifactsから取得できる。全構成の入力・出力・JSON・PNG・ログと集計は`artifacts/github/36790193652`、ローカル容量実測と清掃記録は`artifacts/e2e/packaging-capacity-probes`に保持する。

二者／三者のテキスト・表・JSONレポートの4構成検証は[GitHub Actions run 36793389828](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36793389828)、同コードの[CodeQL workflow](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36793389800)で成功した。各Mac発行物を同runから取得でき、検証成果物と集計は`artifacts/github/36793389828`に保持する。レポート限定CLI検証はE2Eへ`--reports-only`を渡す。単体レポートは`--report-project INPUT_PROJECT OUTPUT_HTML [--entry N]`で生成できる。

後続の表の行合わせ・セル編集・検索は[4構成のrun 36796867483](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36796867483)と[同SHAのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36796867472)で成功した。発行物は同run、実測範囲と集計は[移行一覧](MIGRATION.md#表の行合わせセル編集)、表のAPI・操作・上限は[TABLE-EDITOR.md](TABLE-EDITOR.md)を参照する。

表の同セル内検索・固定文字範囲・一件/全置換と、小さい画面での文字全体の表示は[4構成のrun 36805372990](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36805372990)と[同SHAのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36805373087)で成功した。Mac Intel/ARM64のNative AOT発行物を同runのArtifactsから取得できる。成功数・実測範囲は[移行一覧](MIGRATION.md#表のセル内検索置換)、各構成の成果物・集計は `artifacts/github/36805372990` に保持する。

機能の対応状況と保留事項は [MIGRATION.md](MIGRATION.md) を参照する。

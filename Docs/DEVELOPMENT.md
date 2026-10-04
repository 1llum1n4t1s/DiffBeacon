# 開発と検証

## 固定fixtureのGit属性

固定SHAを持つ原本・manifest・goldenは、最も近い`.gitattributes`で`-text`を指定し、継承した`eol`指定も解除する。親ディレクトリの属性はルートの指定より優先されるため、ルートに規則を書いただけでは保護を確認できない。TAR.Zは[専用属性](../tests/Fixtures/Archives/TarZ/.gitattributes)を使う。原文の改行・空白は変更せず、必要なwhitespace設定を属性側へ記載する。

stage後は実効属性とGit indexの原本bytesを確認する。次はPython標準ライブラリだけで、Git blobをバイナリのまま取得してSHA-256を照合する例。期待SHAの正本とも照合し、改行を正規化して一致扱いにしない。

```powershell
git check-attr --all -- tests/Fixtures/Archives/TarZ/manifest.json
git check-attr --cached --all -- tests/Fixtures/Archives/TarZ/manifest.json
$verifyFixtureGitBytes = @'
import hashlib, subprocess, sys
from pathlib import Path
working = hashlib.sha256(Path(sys.argv[1]).read_bytes()).hexdigest()
blob = subprocess.run(["git", "cat-file", "blob", sys.argv[2]], check=True, stdout=subprocess.PIPE).stdout
stored = hashlib.sha256(blob).hexdigest()
assert working == stored, (working, stored)
print(stored)
'@
python -c $verifyFixtureGitBytes 'tests/Fixtures/Archives/TarZ/manifest.json' ':tests/Fixtures/Archives/TarZ/manifest.json'
```

commit後も第2引数を`HEAD:tests/Fixtures/Archives/TarZ/manifest.json`へ替えて照合する。CIはそのcommitの固定SHAを検査し、ローカル作業中のbytesと区別する。

## 内包テキストの作業保存

実在する内包Textの通常保存・相対アセット・包装・復元はE2Eの `--archive-present-only` で限定確認する。[独立検証の契約](../tests/Fixtures/ArchiveWorkingText/README.md)に従い、Python標準ライブラリで原本ZIPの全CRC・保存bytes・HTML原文と改行・workspaceのSHA・包装全entryを照合する。通常の全体E2EとUI自己検証にも含み、限定成功だけを全体成功として扱わない。

`--self-test-archive-working-review <output>` は保存済みアセットの上書き拒否、JSON公開失敗後の保護、異なる暗号化枝のmasked再入力、三者の祖先同期、手動マージの履歴・dirty保持と古い入力の採用拒否、最終配置後の実captionを再現する。出力のJSON・PNG・原本・workspace・HTMLを保持する。保存ダイアログはpath picker注入であり、ネイティブダイアログの操作検証とは区別する。実行時の認証情報と出力保護registryは専用windowを閉じて解放する。

## 検証成果物の容量管理

画像変換のGUI自己検証では固定goldenをgzipで同梱し、約2.46 MBのJSONを約71 KBへ圧縮する。`python build/Generate-ImageTransformUiFixture.py`で再生成し、展開後の全bytesと正本SHAを照合する。E2Eは同じ正本をそのまま読み、通常の画像処理にはgoldenを使わない。

ローカルで展開保持するのは最新のCLI／UI検証と、次の実装に必要な原本採取に限る。E2Eの`--output`には空き容量のあるドライブを指定し、次の実行前に前回の完了済み出力を整理する。ビルドの`bin/obj`は通常の増分ビルドで再利用し、毎回コピーしない。GitHubの4RID検証は維持し、必要な成果物を一度だけ取得・照合した後、古いrunを圧縮する。

GitHubから取得したZIPはそのまま保持し、entryをストリームで読みながらSHA・サイズ・CRCを照合する。確認に使うJSONと代表PNGだけを抽出し、Native AOT発行物や全体E2Eを一括展開しない。実行に展開が必要な場合は対象RIDだけを使い、用途を終えた展開物を照合後に整理してから次のRIDへ進む。

Windowsでは[Compact-Evidence.ps1](../build/Compact-Evidence.ps1)とPython 3.11以降の標準ライブラリの[CompactEvidence.py](../build/CompactEvidence.py)で過去の検証ディレクトリをZIP64へ格納できる。ZIPは入力とは別の場所へ置く。各entryのSHA・サイズと入力の一覧・更新日時・属性が不変であることを確認し、ZIP全体のSHAも照合する。展開元の清掃はCodexの共通`Remove-CodexItem.ps1`へ対象・許可root・対象外の新規台帳を渡す。移動と消去は別結果として記録し、元パス・ごみ箱実体・管理情報の残存ゼロで完了とする。リンクと許可root外の操作は拒否し、ごみ箱全体は空にしない。失敗ログ・入力・出力・PNG・発行物も省略しない。

```powershell
pwsh -STA -NoProfile -File build/Compact-Evidence.ps1 -SourcePath "$PWD/artifacts/e2e/previous" `
  -ArchiveRoot "$PWD/artifacts/retained/local" -PythonPath '<Python 3の実行ファイル>' `
  -OwnerAttestsQuiescentAndNoMixedWork
```

`-OwnerAttestsQuiescentAndNoMixedWork`は、所有者が利用中プロセスと他作業の混在がないことを確認してから指定する。不可視プロセスとcwdを完全に確認するものではない。清掃は利用者の`.codex/scripts/Remove-CodexItem.ps1`に固定し、別ヘルパーへ変更する引数は提供しない。操作前に同じ`.codex/references/windows-delete-workflow.md`の検証状態・対応範囲を読む。手順書が停止中・対象未対応を示す場合、またはコードの稼働状態と手順書が一致しない場合は元データを保持し、`-WhatIf`による読取り検査だけを行う。停止フラグを解除せず、別実装を使わない。ごみ箱へ移せない場合、取消・拒否・対応項目を特定できない場合も対象の清掃を停止し、元データまたは移動済み項目とZIPを保持する。処理別JSONに残存状態を記録する。圧縮だけ行う場合は`CompactEvidence.py`を直接実行でき、展開元を削除しない。

2026-10-04の移植検証では、最初に手順書と停止フラグの不一致を確認して元データを保持した。その後、共通ヘルパー側の更新と11件のE2E・並行実行の成功、現在のコードSHAとの一致を確認したため、過去の取消修正E2E 21492 files／963433353 bytesを全entry照合済みZIP 57202172 bytesへ保存し、対象だけをごみ箱へ移した。今回項目の個別消去はE_ABORTで途中失敗し、元パスは不在だがごみ箱に10212 files／812355400 bytesと管理情報が残った。全原本を保持したZIPと台帳の対応は `artifacts/retention/index.json` に記録する。この清掃を完了扱いにせず、部分消去後の合計サイズを拒否する現行Resumeも再実行していない。既存のpolicy拒否対象を別APIで再試行しない。symbolic linkや子reparse pointを含む検証rootは現行ヘルパーの対象外であり、保持する。

内包GUI検証の最初の全体runから、reportsの226files／275643425bytesを全entry照合済みZIP 1390196bytesへ保存した。この対象だけの移動後、個別消去がE_ABORTで失敗し、元パスは不在、今回のごみ箱directoryと管理情報は残る。directory内のfileは0件だが残存ゼロを満たさず清掃未完了とする。残り4対象は移動していない。ZIP・台帳・残存receiptは `artifacts/retention/index.json` のpartialCleanupに対応を記録する。同じ対象の再試行や別APIへの切替を行わない。

外部の証拠rootを整理するときだけ`-AdditionalEvidenceRoot`を指定する。ZIPが完成している場合は同じ入力と格納先に`-ExistingArchive '<途中ZIPの絶対パス>'`を加えると、全entryを再照合できる。ただし削除の拒否対象を再実行する許可ではない。既存のZIP・報告JSONは上書きしない。2026-10-03以降のローカル出力はCドライブを使い、I/OエラーのあったEドライブは読取り専用とする。

2026-10-02の保存先は`E:/DiffBeacon-artifacts/retained`。元パスとZIPの対応・SHA・照合結果は隣接JSONと`artifacts/retention/index.json`へ記録する。以前の文書に記載された過去の展開パスはこの索引から参照する。ZIPをその元ディレクトリへ展開すれば各ファイルを読み直せる。元の属性・mode・更新日時はZIP内の`.diffbeacon-retention-manifest.json`に保存されるが、展開ツールによる属性復元の対応は異なるため必要時にmanifestを参照する。ソース・固定fixture・Git履歴、共有キャッシュ、他プロジェクトはこの清掃の対象にしない。

画像領域の原本参照は `uv run --no-project python tests/Fixtures/ImageRegions/generate-reference.py --output artifacts/verification/image-regions-reference/reproduced` で再採取する。通常buildやE2EにはMSVCを追加せず固定goldenを使う。製品を別プロセスで照合する限定実行は `--image-regions-only`。原本抽出・ライセンス・SHAは[fixture説明](../tests/Fixtures/ImageRegions/README.md)、診断CLIの契約は[画像の説明](IMAGE-VIEWER.md#原本照合の入口)を参照する。

画像強調の再採取は `uv run --no-project python tests/Fixtures/ImageHighlight/generate-reference.py --output artifacts/verification/image-highlight-reference/reproduced`。固定72件は限定E2E `--image-highlight-only` と全体へ接続する。通常GUI・CLI・単体／包装HTMLの描画画素を照合し、UI自己検証は実Bitmap・選択・強調解除・三者ページ操作を保存する。[fixture](../tests/Fixtures/ImageHighlight/README.md)の出典・SHA・GPLを保持する。

静止画像コピー核の再採取は `uv run --no-project python tests/Fixtures/ImageCopy/generate-reference.py --output artifacts/verification/image-copy-reference/reproduced`。固定143ケース・935状態を実画像の限定 `--image-copy-only` と全体 E2Eへ照合する。[fixture](../tests/Fixtures/ImageCopy/README.md)のFreeImage未実測のadapter境界を維持し、[診断CLI](IMAGE-VIEWER.md#静止画像コピー核の診断)を使う。GUIの静止画編集はheadless自己検証で実controlの方向・領域／全領域・三者auto・共有履歴・readonly・取消／失敗時の状態保持、PNG別名保存と編集済みHTML、保存paneのパス更新とdirty包装拒否を確認する。元形式／多ページ編集保存と通常デスクトップの手動操作は未実測または未対応。結果の確定値は[MIGRATION.md](MIGRATION.md)へ集約する。

矩形削除・貼り付けの限定E2Eは `--image-rectangles-only`。原本核の再採取はMSVC x64環境で `python tests/Fixtures/ImageRectangles/extract.py --output <空の採取先>` を使う。貼り付け前Resizeの呼出し順序を再採取する場合は `python tests/Fixtures/ImageResize/generate-reference.py --output <空の採取先>` を使う。両fixtureは原本二プロセスの全bytes一致を確認し、固定gzipへ照合する。[矩形](../tests/Fixtures/ImageRectangles/README.md)と[Resize](../tests/Fixtures/ImageResize/README.md)のadapter境界を確認する。通常buildにMSVCやFreeImageを追加しない。

実OSの画像クリップボードはGitHubのクリーンなWindows/macOS runnerで、Native AOT実行ファイルの `--clipboard-self-test <出力先> --write` と `--clipboard-self-test <同じ出力先> --read` を別プロセスで実行する。headless自己検証とは別のnative windowとOS clipboardを使い、writer終了後の全BGRA・透明度・標準bitmapとWindowsのbottom-up CF_DIBを確認する。JSON・生画素・PNG・Windows DIBを同じ検証成果物へ保持する。`GITHUB_ACTIONS`と`RUNNER_OS`を確認し、通常ローカルではplatform初期化前に拒否する。この実行経路の追加はOSでの成功を意味しない。確定結果はMIGRATION.mdへ記載する。

画像HTMLは `--report-project INPUT_PROJECT OUTPUT_HTML [--entry N] [--left-frame N [--middle-frame N] --right-frame N] [--threshold X]` で生成する。番号・閾値は画像比較にだけ指定でき、保存した画像設定を既定として使う。画像設定を省略した既存プロジェクトは全同番号フレームと閾値0。明示したCLI指定が保存設定より優先する。限定E2Eは `--image-reports-only`、契約は [画像の説明](IMAGE-VIEWER.md) と [E2Eの手順](../tests/DiffBeacon.E2E/README.md) を参照する。

GNU既定算法の原本参照は `pwsh -NoProfile -File build/Generate-LegacyGnuReference.ps1` で採取する。[原本とadapterの境界](../build/LegacyGnuReference/README.md)を確認する。`--gnu-line-script INPUT_JSON [--max-work N]` は入力 `{ "left": [同値クラスID], "right": [同値クラスID], "classCount": ID上限 }` から変更scriptを返す開発用CLI。原本の入力変換と算法を分けて照合する限定E2Eは `--gnu-line-only`、通常文書比較の元bytesからの接続検証は `--gnu-text-only` を使う。表の復号キーへの接続は `--gnu-table-only` で原本143ケースのCLI/HTMLと共有予算を照合する。これらは全体E2Eにも含める。通常 `--compare` は `lineWorkUsed`・`lineFallback`・`lineFallbackReason` と全元行を返す。予算契約は [Core README](../Src/DiffBeacon.Core/README.md#テキスト比較と保存) を参照する。

必要環境は.NET 10 SDKとPowerShell 7。アーカイブE2Eの独立検証にはPython 3（CIは3.13）も使い、pipパッケージは不要。Pythonはアプリの実行・発行依存ではない。正規のソースディレクトリ名は`Src`であり、macOSでもこの大文字小文字を維持する。通常ビルドは旧C++プロジェクトやsubmoduleに依存しない。

TAR.Zのwriter出力は検証専用の公式ncompressで独立に復号する。Windows／macOSの各hostで `pwsh -NoProfile -File build/Build-ZReference.ps1 -OutputDirectory artifacts/z-reference/local` を実行し、E2Eへ `--z-reference <生成したncompress.exeまたはncompressの絶対パス>` を渡す。Windowsは既存MSVC、macOSは既存clangを使い、固定した原本SHAとcompiler／decoderのSHA・引数・終了コードを生成先へ記録する。通常.NET build・製品起動・発行物にCやcompilerを追加しない。限定 `--tar-z-only`、第二decoderの指定、固定39原本の出典と再生成は[TAR.Z fixture](../tests/Fixtures/Archives/TarZ/README.md)を参照する。CIは全体E2Eにこの独立decoderを指定し、build proofもverification artifactへ保存する。

表行対応の元関数fixtureを再採取する場合だけ、WindowsのMSVC・SDK・ICUを使う `pwsh -NoProfile -File build/Generate-LegacyLineReference.ps1` を実行する。出力は`artifacts/verification/table-line-alignment/reproduced`。元関数の抽出・buffer adapter・固定コンパイラパスと採取範囲は[fixtureの出典](../tests/Fixtures/LineAlignment/README.md)を参照する。これらはアプリの通常buildや実行依存に含めない。

```powershell
dotnet build DiffBeacon.slnx -c Release
dotnet run --project Src/DiffBeacon.App/DiffBeacon.App.csproj -c Release --no-build
pwsh -NoProfile -File build/Build-ZReference.ps1 -OutputDirectory artifacts/z-reference/local
$decoderName = if ($IsWindows) { 'ncompress.exe' } else { 'ncompress' }
$zReference = (Resolve-Path -LiteralPath (Join-Path artifacts/z-reference/local $decoderName)).Path
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output artifacts/e2e/local --z-reference $zReference
```

E2E はアプリを別プロセスで実行し、CLI の終了コード、比較結果、マージ、パッチのバイト列、再帰比較、異常入力を確認する。`artifacts/e2e/local/assertions.json` と入力・出力・標準出力・標準エラーログが再現用成果物になる。UI は次の headless 自己検証で確認する。

プロバイダーの検証にはXML・HTML・DOCX・PPTX・XLSX・TAR、ループバックHTTP、外部実行ファイルのプロトコル異常と応答上限も含む。ソリューション内の`DiffBeacon.FakeProvider`がE2E用実行ファイルを生成する。UI検証は通常のウィンドウとSkia描画を使い、4ペイン・画像・バイナリ・変換後テキスト保存拒否などの結果をPNGとJSONで保存する。

```powershell
dotnet Src/DiffBeacon.App/bin/Release/net10.0/DiffBeacon.dll --self-test artifacts/verification/managed
```

内包アーカイブの子比較だけを調べる場合は、上の自己検証へ `--archive-sources-only` を追加する。限定結果の `scope` は `archive-sources-only`、全UIは `all` となる。実entry選択・子タブ候補・階層password再試行・Stop／refresh／tab close・readonly・全入力の出力保護・通常／最小viewport・workspace／HTMLをPNGとJSONへ残す。限定は全UIの代替にせず、コード変更後の全体E2Eと全UI、同OS Native AOTも実行する。Sourceとtyped workspace／包装のCLI限定実行・独立検証は [E2Eの手順](../tests/DiffBeacon.E2E/README.md) に集約する。

Native AOT の Windows 発行には Visual Studio の C++ ビルドツールと Windows SDK、macOS 発行には Xcode のネイティブツールが必要。発行は対象と同じ OS で実行する。

```powershell
# Windows 上
pwsh -STA -NoProfile -File build/Publish.ps1 -Rid win-x64 -OwnerAttestsQuiescentAndNoMixedWork
pwsh -STA -NoProfile -File build/Publish.ps1 -Rid win-arm64 -OwnerAttestsQuiescentAndNoMixedWork
# macOS 上
./build/Publish.ps1 -Rid osx-x64
./build/Publish.ps1 -Rid osx-arm64
```

ローカルのVisual Studio 2026構成では、SDKのC++コンポーネント検索が空となり、既存linkerの自動検出に失敗した。MSVC／Windows SDKが存在するこの環境では、開発シェルを読み込んでから発行できる。`IlcUseEnvironmentalTools`の実MSBuild値が`true`で、`link.exe`と`LIB`が既存ツールを指すことを確認する。

```powershell
Import-Module 'C:/Program Files/Microsoft Visual Studio/18/Community/Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
Enter-VsDevShell -VsInstallPath 'C:/Program Files/Microsoft Visual Studio/18/Community' -DevCmdArguments '-arch=x64 -host_arch=x64' -SkipAutomaticLocation
$env:IlcUseEnvironmentalTools = 'true'
./build/Publish.ps1 -Rid win-x64 -OwnerAttestsQuiescentAndNoMixedWork
```

発行物は `artifacts/publish/<RID>` に生成する。Windowsの旧発行物は、所有者の静止・混在なし確認を受け、Codexの指定共通ヘルパーによるごみ箱移動・今回項目の個別消去・元パス／実体／管理情報の残存確認を終えてから再生成する。既存発行物があるWindowsではSTA実行と上記switchが必要で、処理別JSONをartifacts/publish-cleanupへ残す。共通ヘルパーが停止中・未対応の場合は旧発行物を保持し、`-OutputRoot artifacts/native-runs/<新しい検証名>`で未使用の生成先を指定できる。その場合、発行物は指定rootのRID配下、自己検証は同rootのverification/RIDへ生成し、過去結果を混在させない。macOSは従来の同OS清掃経路を使う。実行ファイルと同じホストアーキテクチャでは自己検証も実行し、結果を `artifacts/verification/<RID>` に残す。自己検証全体の制限は既定600秒で、`-VerificationTimeoutSeconds`に1～3600秒を指定できる。超過時は子プロセスも終了して発行失敗にする。実行時間と制限をmanifestへ記録する。個々のUI操作の30秒制限と全検証項目・合否条件は維持する。自己検証は各段階の開始・終了、経過時間と成功／失敗数をconsoleと`self-test-progress.json`へ記録する。同じディレクトリの一時ファイルをflushして置換するため、強制終了中でも直前の完成した進捗を保持する。最終`ui-report.json`がないtimeoutを全UI成功として扱わない。画像ドラッグ対応SHAのMac ARM64 runnerは最後の表検索まで進んで旧180秒制限に達し、前回の同構成の成功も約178秒だったため、検証全体の枠を広げた。改定後の固定SHA 3b6f41bのCIではMac ARM64約195秒・x64約185秒で完走し、最終UI集計・表検索の完成進捗を照合した。全体E2E・実OSクリップボードも両構成で成功した。詳細は移行対応表の固定SHA検証記録を参照する。クロスアーキテクチャでは自己検証を省略した理由を manifest に記録する。`-SkipVerification` は発行だけを行う明示指定である。

macOS の成果物は `DiffBeacon.app` と、実行権限を保持する `DiffBeacon.app.tar.gz`。GitHub artifact をダウンロードした場合は tar を展開して起動する。署名・公証・配布はこのスクリプトの工程に含めない。

```powershell
pwsh -NoProfile -File build/Build-ZReference.ps1 -OutputDirectory artifacts/z-reference/local
$zReference = (Resolve-Path -LiteralPath artifacts/z-reference/local/ncompress.exe).Path
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output artifacts/e2e/native --app artifacts/publish/win-x64/DiffBeacon.exe --z-reference $zReference
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

検証成果物は直近の確認に必要なものだけ展開して保持する。完了済みの入力・出力・ログ・PNGは `build/Compact-Evidence.ps1` で ZIP 化し、全 entry の SHA-256・サイズと元ファイルの一致を確認してから展開元を除去する。過去の記載先が圧縮済みの場合は `artifacts/retention/index.json` から保存 ZIP と照合記録を確認する。GitHub の成果物は同じものを重複取得せず、ZIP 内で検査し、確認に必要な JSON と代表 PNG のみ展開する。生成物・検証 ZIP は Git に登録しない。

機能の対応状況と保留事項は [MIGRATION.md](MIGRATION.md) を参照する。

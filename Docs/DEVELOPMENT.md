# 開発と検証

開発コマンドは以下の起動経路から実行し、出力先を先に固定する。

## 開発出力の起動経路

このPCのローカル正本は Git 管理外の `Directory.Build.local.props`（`.git/info/exclude`登録）の `DiffBeaconWorkRoot` で、各ツールの既定の一時フォルダー（PowerShellの `[IO.Path]::GetTempPath()`）配下の `Codex/DiffBeacon` を基準にする。このcheckoutで設定が無い場合は暗黙のfallbackをせずエラーにする。CI/macOS/別利用者にはこのPCのローカル設定を適用しない。直接dotnetの作業root外のTEMP/TMP、正本外のMSBuild出力上書き、似たprefix、junctionは作成前に拒否する。照会専用の `-getProperty` は出力を作らないため利用できる。

```powershell
. ./build/DevelopmentOutput.ps1
$workRoot = Get-DiffBeaconWorkRoot
if (-not $workRoot) { $workRoot = Join-Path ([IO.Path]::GetTempPath()) 'Codex/DiffBeacon' }
$runRoot = Join-Path $workRoot ('verify-' + [guid]::NewGuid().ToString('N'))
function Invoke-DevDotnet { & ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments $args }
function Invoke-DevPowerShell { & ./build/Dev.ps1 -WorkDirectory $runRoot -Tool pwsh -ToolArguments $args }
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments @('build', 'DiffBeacon.slnx', '-c', 'Release')
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool z-reference
$decoderName = if ($IsWindows) { 'ncompress.exe' } else { 'ncompress' }
$zReference = Join-Path $runRoot "z-reference/$decoderName"
$app = Join-Path $runRoot 'build/bin/DiffBeacon.App/release/DiffBeacon.dll'
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments @('run', '--project', 'tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj', '-c', 'Release', '--no-build', '--', '--output', "$runRoot/e2e", '--app', $app, '--z-reference', $zReference)
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments @($app, '--self-test', "$runRoot/verification")
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool publish -ToolArguments @('-Rid', 'win-x64')
```

`-Tool pwsh -ToolArguments @('-NoProfile', '-File', '<bootstrap script>', ...)` は同じTEMP/TMPを継承する。bootstrap/readerにも結果・logを明示して同じrunへ指定する。bin/obj/AOT中間物は `<run>/build`、発行は `<run>/publish`、TEMP/TMP/TMPDIRは `<run>/temp`。 同じrunの中間物はbuild→publishや再発行で再利用する。既存RID発行先の停止・削除計画とは区別し、中間物の存在だけでは停止しない。境界外・発行先との重複・リンク祖先は引き続き拒否する。製品の通常保存先は制限しない。SDKホスト自身の共有cacheと既存NuGet package cacheは、このガードが動く前の処理とは区別する。MSBuildのbinary/file loggerも初期targetより先に開くため、loggerを使う起動は必ずwrapperを通す。wrapperは既定file loggerと正本外のlogger pathを起動前に拒否する。 明示response file（`@...`）と重複LogFile指定は展開せず起動前に拒否する。build／restore／publish／pack／msbuildには`-noAutoResponse`を付ける。runではこのswitchがアプリ引数になるため付けず、project・作業directoryの祖先とSDK内のMSBuild.rsp／Directory.Build.rspが存在したら停止する。runのカスタムSDK探索pathsも検査対象を確定できないため停止する。必要な引数は直接指定する。

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

## 独立三側TextのGUI入力選択

BasicとCritical、Archive、Cipher、Lifetime、Route、Saved Archivesの限定実行と別readerは [E2E手順](../tests/DiffBeacon.E2E/README.md) に従う。全体UIの `ui-report.json` をBasic／Route／Saved Archives readerへ渡すときは `--full-ui` を明示し、限定reportのscopeと混同しない。生成原本は [Route](../tests/Fixtures/IndependentTextInputSelectionRoutes/README.md) と [Saved Archives](../tests/Fixtures/IndependentTextInputSavedArchives/README.md) のmanifest・原文bytes／BOM／混在改行・SHAおよびZIP全entry／CRCを確認する。再生成は入力のない新directoryだけで行い、固定原本を上書きしない。

新runは冒頭の正本workrootに従い、このPCではD配下の新しいdirectoryへ作成する。Cへのfallbackは禁止する。次run前の圧縮保持と全entry照合は次節に従い、過去の実証を展開したまま積み上げない。
## バイナリ置換の限定検証

バイナリ置換はE2Eの `--binary-replace-only`、GUIの `--self-test <外部出力先> --binary-replace-only` で限定実行する。外部の新規runを使用し、固定51原本・実置換ダイアログ・共有Undo／Redo・保存点・タブABA・通常／内包保存とworkspace複製／包装を独立照合する。`verify_gui.py --gui <GUI出力先> [--receipt <新規JSON>]` と `verify_cli.py --work <E2Eのfixtures/run-id/binary-replace> --receipt <同work直下の新規JSON>` は用途が異なる。CLI readerはE2Eが生成した保存版・project-copy／package-projectと実終了metadataを必要とし、GUI単独の出力には使わない。正確なコマンドと保持する証拠は[置換fixture](../tests/Fixtures/BinaryReplace/README.md)を参照する。限定成功は全体UI／E2E・AOT・実OS保存ダイアログ／クリップボードの資格に含めない。

## 検証build／Native発行のNuGet lock隔離

SDK RID／ILCompilerの復元が保護対象の `packages.lock.json` に工具graphを追加する場合、検証用のbuild／publishだけ外部lockへ隔離する。依存更新・version変更の手順ではなく、リポジトリの5正本lockを維持するための方法である。Windows `native506-20261008` でbuild・win-x64 publishへの継承とApp／Core／Providersの実property probeを確認し、`artifacts/local/folder-threeway-next/text-next73/commit250/scope-before.json` の21保護fileは前後raw SHA不一致0件だった。

1. 冒頭の正本workroot配下の `<一意なrun名>` を絶対パスへ解決し、repoと利用者の `.codex`／解決済み `CODEX_HOME` の外で、既存データやリンク先が混在しない新directoryにする。全5project（App・Core・Providers・E2E・FakeProvider）の正本lockをbytesのままバックアップし、raw SHA・属性・復元先を記録する。`locks/<MSBuildProjectName>.packages.lock.json` に同じbytesをseedする。出力・ログもこのrunへ指定する。
2. run直下へ次の `ExternalLockFiles.props` を保存する。projectごとの名前はprops内で展開する。CLIに `-p:NuGetLockFilePath=...$(MSBuildProjectName)...` を直接渡す方法は名前が展開されずlockが衝突するため使わない。

```xml
<Project>
  <PropertyGroup>
    <NuGetLockFilePath>$(MSBuildThisFileDirectory)locks\$(MSBuildProjectName).packages.lock.json</NuGetLockFilePath>
  </PropertyGroup>
</Project>
```

3. 専用の新しいPowerShell processで、設定前に `CustomBeforeMicrosoftCommonTargets` のprocess環境値と各projectの `dotnet msbuild <project> -getProperty:NuGetLockFilePath,CustomBeforeMicrosoftCommonTargets -nologo` を記録する。既存の有効なcustom importがある場合は上書きせず停止する。その専用processだけで `$env:CustomBeforeMicrosoftCommonTargets = '<runの絶対パス>/ExternalLockFiles.props'` と設定し、同じprobeで全5projectのlockがrun内の別々のseedへ解決されることを確認する。
4. buildは通常tokenの専用processから `& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments @('build', 'DiffBeacon.slnx', '-c', 'Release')` を実行する。publishは後述のVisual Studio 2026開発シェル手順で既存DevShellと `IlcUseEnvironmentalTools=true`、実 `link.exe`／`LIB` を確認してから、`Invoke-DevPowerShell -STA -NoProfile -File build/Publish.ps1 -Rid win-x64 -OutputRoot '<runの絶対パス>/publish' -VerificationTimeoutSeconds 600` を子processで実行する。Native自己検証に管理者tokenが必要な環境では `Start-Process -Verb RunAs -WindowStyle Hidden` でSTAの専用publish processを起動し、実tokenを確認する。`native506` は通常tokenのbuildと、実管理者tokenのpublish／自己検証を分けて実行した。publish process内でも同じ外部propsをprocess環境へ設定し、probeで継承を確認する。ユーザー／machine環境設定やproject・props正本は変更せず、完了後は専用processを終了する。出力先は未使用のdirectoryにし、既存発行物の清掃を必要としない。
5. build／publishそれぞれの実終了・ログと前後raw SHAを読み、正本lock・保護file・sourceの保持を確認する。不一致があれば成功扱いせず停止し、変更された対象を退避してから記録済み原bytes／属性へ復元し、復元SHAを確認する。未検証のOS／RIDへ隔離方式や発行の成功資格を広げない。再現用props・`run-build.ps1`・`run-native-isolated.ps1`・probeと前後照合は利用者LocalAppData配下の `Codex/TaskArtifacts/DiffBeacon/native506-20261008` に保持する。

## 検証成果物の容量管理

画像変換のGUI自己検証では固定goldenをgzipで同梱し、約2.46 MBのJSONを約71 KBへ圧縮する。`python build/Generate-ImageTransformUiFixture.py`で再生成し、展開後の全bytesと正本SHAを照合する。E2Eは同じ正本をそのまま読み、通常の画像処理にはgoldenを使わない。

ローカルで展開保持するのは最新のCLI／UI検証と、次の実装に必要な原本採取に限る。E2Eの`--output`には空き容量のあるドライブを指定し、次の実行前に前回の完了済み出力を整理する。ビルドの`bin/obj`は通常の増分ビルドで再利用し、毎回コピーしない。GitHubの4RID検証は維持し、必要な成果物を一度だけ取得・照合した後、古いrunを圧縮する。

GitHubから取得したZIPはそのまま保持し、entryをストリームで読みながらSHA・サイズ・CRCを照合する。確認に使うJSONと代表PNGだけを抽出し、Native AOT発行物や全体E2Eを一括展開しない。実行に展開が必要な場合は対象RIDだけを使い、用途を終えた展開物を照合後に整理してから次のRIDへ進む。

GitHub artifactのZIPは空ディレクトリを保持しない。CIではアップロード直前に `build/Capture-EvidenceInventory.py` でE2E・UI・独立decoder・発行manifestの実体一覧を `$RUNNER_TEMP/Codex/DiffBeacon/ci-<run-id>-<attempt>-<RID>/evidence.json` へ記録し、同じverification成果物に同梱する。ファイルは全SHA／サイズ、ディレクトリは空かどうか、リンクは追跡せず参照先を記録する。schema 1の既存項目を維持し、全entryへ実体の `lstat().st_mtime_ns` を整数の `mtimeNs` として追加する。これはアップロード直前の原本観測であり、ZIPのDOS時刻や展開先の時刻から原本のexact nsを復元しない。受領時は同じrowのSHA／サイズと結び付けて扱い、`mtimeNs` のない過去runではexactな原本mtimeは未検証とする。失敗runの存在しないrootも明記する。受領時はZIP内の全ファイルを一覧と照合し、ZIPに残らない空ディレクトリの観測はこの一覧の記録と区別する。一覧を持たない過去runは、空ディレクトリの存在をrunner上の検証ログから確認した範囲に限定して報告する。

Windowsでは [Compact-Evidence.ps1](../build/Compact-Evidence.ps1) と Python 3.11以降の [CompactEvidence.py](../build/CompactEvidence.py) で圧縮・全entry SHA/サイズ照合を行う。`-ArchiveRoot`、index、report、圧縮log、Prunlet用paths JSON／結果logはすべて正本workroot配下へ限定し、このPCではD不在時やC指定で作成前に停止する。過去Cの完了runは `-AdditionalEvidenceRoot` で入力rootを明示すれば読み取れる。入力や製品の通常保存先をDへ制限する規則ではない。

`-CompressOnly` は圧縮・照合・Eのindex更新だけを行い、sourceを保持する。省略時もPSから削除は呼ばず、Eへpaths JSONと正規PrunletのCMD直接コマンドを準備する。実削除は 共通の `PRUNLET.md` に従い、インストール済みexe/configを `shell=cmd.exe, login=false` で直接実行する。PowerShell wrapperやRemove-Itemへ切り替えない。返されたdelete結果と元パス不在の確認までsourceRemovedを成功扱いしない。

通常ZIPの全entry照合だけでは、NTFS ADS／EAの保全は保証されない。fixtureは正規teardown前に元pathとstream／EA名・flags・全値を正本workrootのmanifest／payloadへ採取し、保存物の再読SHA／size照合を終えてから清掃する。EAがPrunlet backupやADS保全に含まれると仮定せず、必要な保全が完了するまで原本と唯一の復元copyを消さない。今回のEA保全はE配下の専用helperによる一回限りの採取であり、通常のCompactがEAに対応したわけではない。

自作fixtureで意図的に変更したReadOnlyやACLは、証拠採取・全entryの圧縮照合を済ませた後、fixtureの正規teardownで元の属性・ACLへ戻してから最初のPrunlet呼出しへ渡す。Prunletが拒否した対象の属性・ACLを無断で変えて再試行しない。今回のC対象だけに許可された属性／ACL修復を、将来の別対象へ拡大しない。

Prunletの復元コピーもC占有として数える。今回のoperationだけについてE本体ZIP・ADS補足・canonical記録の保全照合を終え、正規purge後に実際のC空き容量を確認する。元パス不在だけで容量回復や清掃完了としない。既存の別operationは任意にpurgeしない。保全indexは `<正本workroot>/<保全run>/index.json` に置き、repoへ新規生成しない。過去のrepo内indexは旧記録の読取りに限る。

```powershell
$previousRunRoot = '<保持対象の既存外部runの絶対パス>'
$retainedRoot = Join-Path $workRoot ('retained-' + [guid]::NewGuid().ToString('N'))
Invoke-DevPowerShell -NoProfile -File build/Compact-Evidence.ps1 -SourcePath "$previousRunRoot/e2e" `
  -AdditionalEvidenceRoot $previousRunRoot -ArchiveRoot "$retainedRoot" -CompressOnly -PythonPath '<Python 3の実行ファイル>' `
  -OwnerAttestsQuiescentAndNoMixedWork
```

`-OwnerAttestsQuiescentAndNoMixedWork`は、所有者が利用中プロセスと他作業の混在がないことを確認してから指定する。不可視プロセスとcwdを完全に確認するものではない。削除前に許可root・リンク・ZIP照合を確認し、失敗時は対象と復元ZIPを保持する。処理別JSONに削除完了と元パス不在を記録する。圧縮だけ行う場合は`CompactEvidence.py`を直接実行でき、展開元を削除しない。

2026-10-04の移植検証では、最初に手順書と停止フラグの不一致を確認して元データを保持した。その後、共通ヘルパー側の更新と11件のE2E・並行実行の成功、現在のコードSHAとの一致を確認したため、過去の取消修正E2E 21492 files／963433353 bytesを全entry照合済みZIP 57202172 bytesへ保存し、対象だけをごみ箱へ移した。今回項目の個別消去はE_ABORTで途中失敗し、元パスは不在だがごみ箱に10212 files／812355400 bytesと管理情報が残った。全原本を保持したZIPと台帳の対応は `<正本workroot>/<保全run>/index.json` に記録する。この清掃を完了扱いにせず、部分消去後の合計サイズを拒否する現行Resumeも再実行していない。既存のpolicy拒否対象を別APIで再試行しない。symbolic linkや子reparse pointを含む検証rootは現行ヘルパーの対象外であり、保持する。

内包GUI検証の最初の全体runから、reportsの226files／275643425bytesを全entry照合済みZIP 1390196bytesへ保存した。この対象だけの移動後、個別消去がE_ABORTで失敗し、元パスは不在、今回のごみ箱directoryと管理情報は残る。directory内のfileは0件だが残存ゼロを満たさず清掃未完了とする。残り4対象は移動していない。ZIP・台帳・残存receiptは `<正本workroot>/<保全run>/index.json` のpartialCleanupに対応を記録する。同じ対象の再試行や別APIへの切替を行わない。

外部の証拠rootを整理するときだけ`-AdditionalEvidenceRoot`を指定する。ZIPが完成している場合は同じ入力と格納先に`-ExistingArchive '<途中ZIPの絶対パス>'`を加えると、全entryを再照合できる。ただし削除の拒否対象を再実行する許可ではない。既存のZIP・報告JSONは上書きしない。現在のローカル出力は冒頭の正本workrootに従い、このPCではEを使用する。

2026-10-02の保存先は`E:/DiffBeacon-artifacts/retained`。元パスとZIPの対応・SHA・照合結果は隣接JSONと`<正本workroot>/<保全run>/index.json`へ記録する。以前の文書に記載された過去の展開パスはこの索引から参照する。ZIPをその元ディレクトリへ展開すれば各ファイルを読み直せる。元の属性・mode・更新日時はZIP内の`.diffbeacon-retention-manifest.json`に保存されるが、展開ツールによる属性復元の対応は異なるため必要時にmanifestを参照する。ソース・固定fixture・Git履歴、共有キャッシュ、他プロジェクトはこの清掃の対象にしない。

画像領域の原本参照は `uv run --no-project python tests/Fixtures/ImageRegions/generate-reference.py --output artifacts/verification/image-regions-reference/reproduced` で再採取する。通常buildやE2EにはMSVCを追加せず固定goldenを使う。製品を別プロセスで照合する限定実行は `--image-regions-only`。原本抽出・ライセンス・SHAは[fixture説明](../tests/Fixtures/ImageRegions/README.md)、診断CLIの契約は[画像の説明](IMAGE-VIEWER.md#原本照合の入口)を参照する。

画像強調の再採取は `uv run --no-project python tests/Fixtures/ImageHighlight/generate-reference.py --output $runRoot/verification/image-highlight-reference/reproduced`。固定72件は限定E2E `--image-highlight-only` と全体へ接続する。通常GUI・CLI・単体／包装HTMLの描画画素を照合し、UI自己検証は実Bitmap・選択・強調解除・三者ページ操作を保存する。[fixture](../tests/Fixtures/ImageHighlight/README.md)の出典・SHA・GPLを保持する。

静止画像コピー核の再採取は `uv run --no-project python tests/Fixtures/ImageCopy/generate-reference.py --output $runRoot/verification/image-copy-reference/reproduced`。固定143ケース・935状態を実画像の限定 `--image-copy-only` と全体 E2Eへ照合する。[fixture](../Fixtures/ImageCopy/README.md)のFreeImage未実測のadapter境界を維持し、[診断CLI](IMAGE-VIEWER.md#静止画像コピー核の診断)を使う。GUIの静止画編集は `Invoke-DevDotnet $app --self-test <出力先> --image-copy-only` で対象を限定でき、実controlの方向・領域／全領域・三者auto・共有履歴・readonly・取消／失敗時の状態保持、PNG／TIFF別名保存と編集済みHTML、保存paneのパス更新とdirty包装拒否を確認する。保存先には正本workroot配下の作業別runを使う。元形式／多ページ編集保存と通常デスクトップの手動操作は未実測または未対応。結果の確定値は[MIGRATION.md](MIGRATION.md)へ集約する。

矩形削除・貼り付けの限定E2Eは `--image-rectangles-only`。原本核の再採取はMSVC x64環境で `python tests/Fixtures/ImageRectangles/extract.py --output <空の採取先>` を使う。貼り付け前Resizeの呼出し順序を再採取する場合は `python tests/Fixtures/ImageResize/generate-reference.py --output <空の採取先>` を使う。両fixtureは原本二プロセスの全bytes一致を確認し、固定gzipへ照合する。[矩形](../tests/Fixtures/ImageRectangles/README.md)と[Resize](../tests/Fixtures/ImageResize/README.md)のadapter境界を確認する。通常buildにMSVCやFreeImageを追加しない。

実OSの画像クリップボードはGitHubのクリーンなWindows/macOS runnerで、Native AOT実行ファイルの `--clipboard-self-test <出力先> --write` と `--clipboard-self-test <同じ出力先> --read` を別プロセスで実行する。headless自己検証とは別のnative windowとOS clipboardを使い、writer終了後の全BGRA・透明度・標準bitmapとWindowsのbottom-up CF_DIBを確認する。JSON・生画素・PNG・Windows DIBを同じ検証成果物へ保持する。`GITHUB_ACTIONS`と`RUNNER_OS`を確認し、通常ローカルではplatform初期化前に拒否する。この実行経路の追加はOSでの成功を意味しない。確定結果はMIGRATION.mdへ記載する。

画像HTMLは `--report-project INPUT_PROJECT OUTPUT_HTML [--entry N] [--left-frame N [--middle-frame N] --right-frame N] [--threshold X]` で生成する。番号・閾値は画像比較にだけ指定でき、保存した画像設定を既定として使う。画像設定を省略した既存プロジェクトは全同番号フレームと閾値0。明示したCLI指定が保存設定より優先する。限定E2Eは `--image-reports-only`、契約は [画像の説明](IMAGE-VIEWER.md) と [E2Eの手順](../tests/DiffBeacon.E2E/README.md) を参照する。

GNU既定算法の原本参照は `pwsh -NoProfile -File build/Generate-LegacyGnuReference.ps1` で採取する。[原本とadapterの境界](../build/LegacyGnuReference/README.md)を確認する。`--gnu-line-script INPUT_JSON [--max-work N]` は入力 `{ "left": [同値クラスID], "right": [同値クラスID], "classCount": ID上限 }` から変更scriptを返す開発用CLI。原本の入力変換と算法を分けて照合する限定E2Eは `--gnu-line-only`、通常文書比較の元bytesからの接続検証は `--gnu-text-only` を使う。表の復号キーへの接続は `--gnu-table-only` で原本143ケースのCLI/HTMLと共有予算を照合する。これらは全体E2Eにも含める。通常 `--compare` は `lineWorkUsed`・`lineFallback`・`lineFallbackReason` と全元行を返す。予算契約は [Core README](../Src/DiffBeacon.Core/README.md#テキスト比較と保存) を参照する。

必要環境は.NET 11 SDKとPowerShell 7。アーカイブE2Eの独立検証にはPython 3（CIは3.13）も使い、pipパッケージは不要。Pythonはアプリの実行・発行依存ではない。正規のソースディレクトリ名は`Src`であり、macOSでもこの大文字小文字を維持する。通常ビルドは旧C++プロジェクトやsubmoduleに依存しない。

TAR.Zのwriter出力は検証専用の公式ncompressで独立に復号する。Windows／macOSの各hostで `& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool z-reference` を実行し、E2Eへ `--z-reference <生成したncompress.exeまたはncompressの絶対パス>` を渡す。Windowsは既存MSVC、macOSは既存clangを使い、固定した原本SHAとcompiler／decoderのSHA・引数・終了コードを生成先へ記録する。通常.NET build・製品起動・発行物にCやcompilerを追加しない。限定 `--tar-z-only`、第二decoderの指定、固定39原本の出典と再生成は[TAR.Z fixture](../tests/Fixtures/Archives/TarZ/README.md)を参照する。CIは全体E2Eにこの独立decoderを指定し、build proofもverification artifactへ保存する。

表行対応の元関数fixtureの再採取スクリプト `build/Generate-LegacyLineReference.ps1` は外部Tempへの出力対応が未実装で、repo内の旧生成先だけを許可するため、現行の作業出力規約では実行を停止する。通常経路の操作例には含めない。元関数の出典・抽出・buffer adapter・固定コンパイラパスと採取範囲は[fixtureの出典](../tests/Fixtures/LineAlignment/README.md)を参照する。採取用のMSVC・SDK・ICUと同スクリプトはアプリの通常buildや実行依存に含めない。

```powershell
$runRoot = Join-Path $workRoot ('verify-' + [guid]::NewGuid().ToString('N'))
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments @('build', 'DiffBeacon.slnx', '-c', 'Release')
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments @('run', '--project', 'Src/DiffBeacon.App/DiffBeacon.App.csproj', '-c', 'Release', '--no-build')
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool z-reference
$decoderName = if ($IsWindows) { 'ncompress.exe' } else { 'ncompress' }
$zReference = (Resolve-Path -LiteralPath (Join-Path "$runRoot/z-reference" $decoderName)).Path
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments @('run', '--project', 'tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj', '-c', 'Release', '--no-build', '--', '--output', "$runRoot/e2e", '--app', $app, '--z-reference', $zReference)
```

E2E はアプリを別プロセスで実行し、CLI の終了コード、比較結果、マージ、パッチのバイト列、再帰比較、異常入力を確認する。`"$runRoot/e2e"/assertions.json` と入力・出力・標準出力・標準エラーログが再現用成果物になる。UI は次の headless 自己検証で確認する。

フォルダーのモデルとコピーはそれぞれE2Eの`--folder-model-only`／`--folder-copy-only`で限定確認できる。コピーの独立readerは固定39 CLIケース、候補と実行結果を分けた型衝突plan、256MiB超のstreamコピー、全入力・出力のbytes／metadataとGUIの44操作を照合する。GUI限定起動は`& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments @($app, '--self-test', "$runRoot/verification/folder-copy", '--folder-copy-only')`。通常／最小850×550／多数タブのPNG・bounds JSONを確認する。MacのFIFO/socket/link/case aliasとWindowsの圧縮／Sparseは実プラットフォームの結果とskipを分ける。Macのstat ABI確認は同hostで`pwsh -NoProfile -File build/Build-FolderMacReference.ps1 -OutputDirectory $runRoot/folder-reference/local`を実行し、SDKの構造体・定数と原本／compiler／probeのSHA・終了を記録する。固定期待とadapter境界は[Folder fixture](../tests/Fixtures/FolderSync/README.md)を参照する。限定成功は全体E2E／UI／AOTの代替にしない。

プロバイダーの検証にはXML・HTML・DOCX・PPTX・XLSX・TAR、ループバックHTTP、外部実行ファイルのプロトコル異常と応答上限も含む。ソリューション内の`DiffBeacon.FakeProvider`がE2E用実行ファイルを生成する。UI検証は通常のウィンドウとSkia描画を使い、4ペイン・画像・バイナリ・変換後テキスト保存拒否などの結果をPNGとJSONで保存する。

```powershell
$runRoot = Join-Path $workRoot ('verify-' + [guid]::NewGuid().ToString('N'))
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool dotnet -ToolArguments @($app, '--self-test', "$runRoot/verification")
```

内包アーカイブの子比較だけを調べる場合は、上の自己検証へ `--archive-sources-only` を追加する。限定結果の `scope` は `archive-sources-only`、全UIは `all` となる。実entry選択・子タブ候補・階層password再試行・Stop／refresh／tab close・readonly・全入力の出力保護・通常／最小viewport・workspace／HTMLをPNGとJSONへ残す。限定は全UIの代替にせず、コード変更後の全体E2Eと全UI、同OS Native AOTも実行する。Sourceとtyped workspace／包装のCLI限定実行・独立検証は [E2Eの手順](../tests/DiffBeacon.E2E/README.md) に集約する。

Native AOT の Windows 発行には Visual Studio の C++ ビルドツールと Windows SDK、macOS 発行には Xcode のネイティブツールが必要。発行は対象と同じ OS で実行する。

```powershell
$runRoot = Join-Path $workRoot ('verify-' + [guid]::NewGuid().ToString('N'))
# Windows 上
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool publish -ToolArguments @('-Rid', 'win-x64')
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool publish -ToolArguments @('-Rid', 'win-arm64')
# macOS 上
./build/Publish.ps1 -Rid osx-x64 -OutputRoot "$runRoot/publish"
./build/Publish.ps1 -Rid osx-arm64 -OutputRoot "$runRoot/publish"
```

ローカルのVisual Studio 2026構成では、SDKのC++コンポーネント検索が空となり、既存linkerの自動検出に失敗した。MSVC／Windows SDKが存在するこの環境では、開発シェルを読み込んでから発行できる。`IlcUseEnvironmentalTools`の実MSBuild値が`true`で、`link.exe`と`LIB`が既存ツールを指すことを確認する。

```powershell
$runRoot = Join-Path $workRoot ('verify-' + [guid]::NewGuid().ToString('N'))
Import-Module 'C:/Program Files/Microsoft Visual Studio/18/Community/Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
Enter-VsDevShell -VsInstallPath 'C:/Program Files/Microsoft Visual Studio/18/Community' -DevCmdArguments '-arch=x64 -host_arch=x64' -SkipAutomaticLocation
$env:IlcUseEnvironmentalTools = 'true'
Invoke-DevPowerShell -NoProfile -File build/Publish.ps1 -Rid win-x64 -OwnerAttestsQuiescentAndNoMixedWork -OutputRoot "$runRoot/publish"
```

発行物は 指定した`$runRoot/publish/<RID>`（省略時もローカル正本設定があれば正本workroot配下の一意run。設定のないCI/macOS/別利用者は外部Temp配下） に生成する。Windowsで発行先のRID directoryが既にある場合、上記switchで所有者の静止・混在なしを確認しても自動削除せず、正本workroot配下のpublish-cleanupへpaths JSON・予定結果path・正規CMDコマンドを記録して発行を停止する。呼出側がインストール済みPrunletをshell=cmd.exe, login=falseで直接実行し、結果と元パス不在を確認してから同じ起動をやり直す。未使用OutputRootへ切り替えれば旧発行物を保持したまま進められる。未照合データの圧縮やpurgeは自動実行しない。新規runとCI/macOSの通常経路は維持する。出力設定だけの確認には-CheckOutputOnlyを指定でき、実buildは行わない。削除できない場合は旧発行物を保持し、`-OutputRoot <正本workroot>/<新しい検証名>/publish`で未使用の生成先を指定できる。その場合、発行物は指定rootのRID配下、自己検証は同rootのverification/RIDへ生成し、過去結果を混在させない。macOSは従来の同OS清掃経路を使う。実行ファイルと同じホストアーキテクチャでは自己検証も実行し、結果を `<OutputRoot>/verification/<RID>` に残す。自己検証全体の制限は既定600秒で、`-VerificationTimeoutSeconds`に1～3600秒を指定できる。GitHub CIはMacのみ1,200秒、Windowsは600秒を明示する。SHA2a5f01のMac Intelは10,919条件成功／0失敗の後、フォルダー検証開始時に600秒へ達したため、全体枠を拡張する。各操作の30秒制限と合否条件は変更せず、修正後のMac実測は別途確認する。超過時は子プロセスも終了して発行失敗にする。実行時間と制限をmanifestへ記録する。個々のUI操作の30秒制限と全検証項目・合否条件は維持する。自己検証は各段階の開始・終了、経過時間と成功／失敗数をconsoleと`self-test-progress.json`へ記録する。同じディレクトリの一時ファイルをflushして置換するため、強制終了中でも直前の完成した進捗を保持する。最終`ui-report.json`がないtimeoutを全UI成功として扱わない。画像ドラッグ対応SHAのMac ARM64 runnerは最後の表検索まで進んで旧180秒制限に達し、前回の同構成の成功も約178秒だったため、検証全体の枠を広げた。改定後の固定SHA 3b6f41bのCIではMac ARM64約195秒・x64約185秒で完走し、最終UI集計・表検索の完成進捗を照合した。全体E2E・実OSクリップボードも両構成で成功した。詳細は移行対応表の固定SHA検証記録を参照する。クロスアーキテクチャでは自己検証を省略した理由を manifest に記録する。`-SkipVerification` は発行だけを行う明示指定である。

macOS の成果物は `DiffBeacon.app` と、実行権限を保持する `DiffBeacon.app.tar.gz`。GitHub artifact をダウンロードした場合は tar を展開して起動する。署名・公証・配布はこのスクリプトの工程に含めない。

```powershell
$runRoot = Join-Path $workRoot ('verify-' + [guid]::NewGuid().ToString('N'))
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool publish -ToolArguments @('-Rid', 'win-x64') -BuildArtifactsPath "$runRoot/build-output"
& ./build/Dev.ps1 -WorkDirectory $runRoot -Tool z-reference
$zReference = (Resolve-Path -LiteralPath "$runRoot/z-reference/ncompress.exe").Path
Invoke-DevDotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output $runRoot/e2e/native --app $runRoot/publish/win-x64/DiffBeacon.exe --z-reference $zReference
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

検証成果物は直近の確認に必要なものだけ展開して保持する。完了済みの入力・出力・ログ・PNGは `build/Compact-Evidence.ps1` で ZIP 化し、全 entry の SHA-256・サイズと元ファイルの一致を確認してから展開元を除去する。過去の記載先が圧縮済みの場合は `<正本workroot>/<保全run>/index.json` から保存 ZIP と照合記録を確認する。GitHub の成果物は同じものを重複取得せず、ZIP 内で検査し、確認に必要な JSON と代表 PNG のみ展開する。生成物・検証 ZIP は Git に登録しない。

機能の対応状況と保留事項は [MIGRATION.md](MIGRATION.md) を参照する。

### Windowsフォルダーmetadataの限定観測

同じ限定実行は `FolderWindowsMetadataScenarios` から実主アプリの16コピーを起動します。固定SHAの `verify-metadata-copy.py` が普通／圧縮親、新規／既存DACL、4属性の入力を別processで生成し、コピー後の全DATA／ADS・EA・security・creation・mtime・identity・Close後の属性を別reader processで照合します。主アプリのJSON出力は独立観測へ照合する対象です。生成／検証readerのPID・birth・実終了・stdout／stderr、入力とreader SHAは `windows-metadata-main` 配下に残します。圧縮能力のない環境では圧縮8ケースを専用skipとし、通常8ケースは実行します。再現手順と資格の境界は[固定fixture](../tests/Fixtures/FolderSync/README.md)を参照してください。

Windows NTFSの `--folder-copy-only` は `folder-windows-metadata-observations.json` の5拒否GUIを、固定SHAの `tests/Fixtures/FolderSync/verify-metadata.py` に別processで照合します。`windows-metadata-independent-process.json` に実PID・birth・Wait完了・exitと入力/reader SHA、proofに5拒否＋2診断GUIの全literal・creation・98件のnative closeを保存します。本文/属性/mtimeが同じでcreationだけが確認後に変更された場合も公開0と入力保持を要求します。保持/query/native予算0は確認前の拒否を要求します。他OS/filesystemのskip、部品harness、全体E2E/UI、同OS主App AOT、4RID CIを別々に記録してください。owned stagingは取得前にcloseと未公開破棄の処理を予約し、後続の予算拒否や取消後の清掃を予約内で行います。全entryの属性に加えたSACL/任意owner/SMBの完全保持や、通常OS pointer/native dialogの実測へ資格を拡張しません。

## マージ結果と外部の検証出力

結果編集の回帰は通常の全体UI／E2Eに含める。部品のsource-linked比較だけでGUIの操作・保存・候補採用を合格にしない。通常DLLと同OS AOT、4RID CI、実OS IME／clipboardは区別する。詳細な状態・原文・予算の境界は [マージ結果](MERGE-SESSION.md) を参照する。

エージェントの作業出力はリポジトリ外の冒頭の正本workroot配下 `<一意task>`（このPCはE）を使う。作成前に絶対path・全ancestor・link先を確認し、repo／Codexユーザーディレクトリへ作業コピーを置かない。`Build-ZReference.ps1 -OutputDirectory <path>` はローカル正本設定がある場合はその領域だけを受け付け、固定source SHAとcompilerを検査する。decoderを `--z-reference` へ明示して全体E2Eを実行する。

共有compilerが大きなメモリを保持している場合は、利用中clientがないことを確認して `dotnet build-server shutdown --vbcscompiler` を使う。重複compileを避け、必要な一回のbuildを `-p:UseSharedCompilation=false` で行える。共有cacheや他作業のファイルを清掃しない。

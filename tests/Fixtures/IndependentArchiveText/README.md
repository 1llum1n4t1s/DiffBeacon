# Independent 実在Archive Text

由来は synthetic。2026-10-07 の `artifacts/local/folder-threeway-next/text-next73/next-archive133` で、失敗条件を先に保存して Python literal から生成した入力・期待bytesを昇格した。WinMergeの原本goldenでも製品出力から採取した期待値でもない。生成したroot ZIPのcommentは由来を示す `synthetic-next-archive133` を維持する。

新規fixtureとreaderは [LICENSE](LICENSE) のMIT。固定対象の全ファイル・サイズ・SHA-256は [fixed-sha256.json](fixed-sha256.json)、ZIPの全27entry／全3root／二段階層／metadata／CRC／SHAは [expected-manifest.json](expected-manifest.json)。元の失敗条件は [failure-spec.md](failure-spec.md)。コピーした生成器・reader・期待JSON・入力・相対assetの原bytesを保持する。

`verify.py` はstdlibだけで入力ZIP全entry、literal encoding/BOM/混在改行、全6方向コピー期待bytes、3側assetを検証する。`verify-products.py` はstdlibだけで別プロセスのCLI出力とheadless成果物を検証する。期待本文はreader自身のliteral、期待encoding/BOMは側ごとの定数であり、製品の宣言値からgoldenを作らない。

```powershell
python -B -X utf8 tests/Fixtures/IndependentArchiveText/verify.py
dotnet run --project tests/DiffBeacon.E2E/DiffBeacon.E2E.csproj -c Release --no-build -- --output <新しいrun> --independent-archive-text-only
dotnet Src/DiffBeacon.App/bin/Release/net10.0/DiffBeacon.dll --self-test-independent-archive-text <新しいUIrun>
python -B -X utf8 tests/Fixtures/IndependentArchiveText/verify-products.py --work <E2Erun/fixtures/run-ID/independent-archive-text> --receipt <独立照合JSON>
```

生成を再現するときは、未使用の一時directoryに `generate.py` と `verify.py` をコピーし、そのdirectory内のgenerate→verifyを実行する。既存固定fixtureを直接上書きしない。入力ZIP・期待bytes・workspace JSONのSHAを固定台帳と照合し、由来とライセンスを維持する。

E2E限定は全体E2Eへ同じscenarioを接続する。v6 original/savedのclone・単体HTML・包装・全ZIP/CRC・展開再読込み、Archive/Physical/Untitled混在、全6方向の実hunk-copyと保存bytes、三側working保存・全三側外部保存・元root保持を確認する。static command oracleは基本18命令＋外部保存18命令＋境界拒否6命令の42命令を固定し、名前・引数・終了コード・JSON範囲・rawの不足／余剰／重複を拒否する。各CommandResultのPID／OS生成時刻／実終了／raw stdout stderrを保存し、Python readerのPID／生成時刻／UTC／実終了／waitと全stream回収を別receiptに残す。Macの固定startup gate原bytesは変更しない。

UIはrun内に固定入力をコピーし、利用者AppDataやクリップボードを変更しない。三者本文、中央save、normal／850×550／多数tabのbounds JSON・PNG、実Control Undo/Redo、readonly true/null（省略時と同じ状態）・取消・後発編集・兄弟revisionを保存する。ネイティブ保存dialog、実OSのMetaジェスチャー／pointer、Missing/Absent/providerの対応追加は対象外。

`independent-archive-boundaries/cases.json` は独立した37実case＋3setup。readerは196のsourceからcase集合と理由を固定し、境界用literal本文／文字コード／BOM、保存snapshot、全inventoryのサイズ・SHA、dirty／revision／generation、実Undo/Redoの実行範囲、元入力／既存出力を照合する。親closeの正常保存だけ中央保存点の更新を許容し、拒否caseは全状態とraw bytesの保持を要求する。

Windows managedのReleaseは `whole200/build-20261007T1534081404032` で実終了0・警告0／エラー0。限定 `limited208` はE2E87成功／0失敗・42命令、GUI163成功／0失敗、B588 stdlib readerは2,295条件を照合した。全体E2Eは161,942成功／0失敗／5skip・7,112命令（実PID15808・実終了0）、全体headless UIは11,532成功／0失敗、新Archive GUI163成功・217 root PNG。旧D99固定readerの160条件と、B588の関数を使うGUI専用helperの4,146条件・全217PNG全画素の独立照合を受理済み。証拠は `artifacts/local/folder-threeway-next/text-next73` の `limited208-parent-accept209.json`、`accept215/acceptance-e2e200.json`・`facts.json`、`whole-ui-parent-accept217.json`、`accept212/archive-whole-gui-final-retry2-receipt.json`。

残りは [coverage.md](coverage.md) に明記する。この追加機能の4RID Native AOT、通常desktop・実OS pointer・ネイティブ保存dialogは未実測。Physical／Untitled基準版cb666の4RID合格を追加機能へ転用しない。元のfailure/spec・入力・golden・readerは保持する。

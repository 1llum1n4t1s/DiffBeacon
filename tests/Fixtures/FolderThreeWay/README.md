# 三者フォルダーの固定原本と独立照合

実装前に整理した失敗条件と期待結果:

- 原本・transport・source/slice の SHA 不一致は検証失敗。製品から期待値を再生成しない。
- 不在と実在する空ファイルの混同、存在 mask・全体分類・3 pair のいずれかの不一致は失敗。
- コピー先以外の側の bytes 変更、確認取消・読取り専用・確認中変更での公開は失敗。
- 第三 root 内への公開と content 予算超過を拒否し、入力を保持する。
- 第三 root の通常末尾区切り1個を付けても、確認前に拒否し、左の `new\n`・中央の `protected\n`・右の `right\n` をすべて保持する。
- Windowsでは `\\?\C:\...\right\third\\` のextended namespaceと末尾区切り2個も同じ拒否・全bytes保持を要求する。macOSへWindows namespaceの契約を転用しない。
- GUIの第三root保護観測JSONが欠落／重複する、確認を開く、公開する、3側の固定bytesが変化する場合は失敗。
- GUI の最終集計欠落、実終了の非ゼロ、16ケース欠落、PNG の破損／寸法不一致は失敗。
- 起動 timeout・部分stdout・異常終了は製品の期待終了として扱わない。

`original/expected.ndjson` は原文 FullQuickCompare・DiffWrapper・DiffList の実行による63ケースの固定stdoutです。SHA-256 は `C9B4165F49D74704828734E47D3AB7073233B301A733E96D0739302DE92978AB`、入力 `transport.ndjson` は `A251B307151D073AA6F36B5A7434F9EBFDC99386D20744C20A240DF4C742FEE7`。presence mask 1..7 と empty/same/片側変更3方向/all-change/末尾EOL/反復/離れた変更を組み合わせています。

原本 source/slice・GPL COPYING・adapter・provenance・観測あり／なし同値記録・採取 status と独立reader結果を `original/` に保持します。採取時の全プロセスとinputは `artifacts/local/folder-threeway-next/original-full34/capture-attempt-002`、別観測なし採取は `capture-attempt-003-plain` に保持されています。固定fixtureは前者のgoldenをbyteコピーし、全採取プロセスログを固定fixtureへ重複展開していません。後者との同値は `observer-equivalence.json` に記録されています。

再採取は `original/compile.ps1 -Compile -Output <original内の新attempt>`、`original/capture.py --run --executable <新attempt/producer.exe> --output <original内の新capture>`、`original/verify.py --output <新capture>` で行います。PowerShell 7、Python、既存MSVCとrepo内の旧sourceを必要とします。通常.NET build/E2EはC/C++を呼びません。採取の実stdout以外からgoldenを更新しないでください。

恒久E2Eは `FolderThreeWayScenarios.RunAsync`。実CLI `--directory LEFT RIGHT --middle MIDDLE`、6方向×all/diffの `--folder-sync`、第三root保護、content予算拒否を別プロセスで実行します。GUIは `--self-test OUTPUT --folder-threeway-only` を別起動し、`verify.py FIXTURE WORK` が固定原本、実stdout、全入力／出力bytes、16 GUIコピー、PNGの全chunk CRC・zlib・寸法を独立照合します。成果物には `commands.json`、stdout/stderr、前後snapshot、PNG、`independent-process.json` と独立照合結果を保持します。

末尾区切り保護は通常末尾1個を両OS、Windows extended・DOS device末尾2個をWindowsだけで実行します。Windowsでは既存のローカル管理共有を読める場合だけlocalhost UNC別名も検証し、共有やOS設定は変更しません。CLIはmacOS28件、Windows38件＋短い共有1件＋長い共有7件、GUIはmacOS83条件、Windows99条件＋共有4条件です。共有が読めない場合は環境JSONとCLIのskipを記録します。CLIとGUIそれぞれの環境JSONを、独立readerが共有の存在・実体一致へ照合し、実施したケースの件数を別々に検査します。既存16コピーのJSONを維持し、別の `folder-threeway-protection-observations.json` の1／4／5件を独立して数えます。readerはraw中央入力のnamespaceと末尾数、確認0・公開0、実3側の全bytesと前後snapshotを照合します。

Windowsの追加動的fixtureは標準rootが270文字以上、各componentが80文字以下の独立したtreeです。DOS drive別名（`\\.\C:\...`）で二者の左右read、三者の全3側read、二者の両方向copy、長い第三rootの末尾2区切り保護を実CLIで検証します。既存localhost管理共有をextended UNCで読める場合だけ、DOS UNC別名（`\\.\UNC\localhost\C$\...`）でもread5件・copy2件を追加します。共有の存在・実体一致とRAW引数、実PID・起動／終了時刻、差分readのexit1、copyのexit0／公開1、保護拒否のexit2／公開0、全3側bytesと非変更側metadataを独立readerで照合します。固定原本63、golden、provenanceとそのSHAは変更しません。`long-alias-environment.json`は長い共有の判定を短い共有とは別に記録し、両者の可用性に応じてCLI件数を厳密に数えます。

対象は既定GNU・変換済みUTF-8の葉63ケースです。親helper12061だけでscanner/Errorを検証したとは扱いません。任意filter/plugins/moved/encoding/binary・全入力互換・Native AOT/4RID・実OSのpointer/dialogはこのfixtureの資格範囲外です。GUI取消はheadless注入経路です。

Windowsでは比較していない別タブに長いDOS形式のrootを読取り専用で指定するGUI回帰も4条件追加しています。`folder-threeway-long-readonly.json`と全3側の実bytes、RAW入力の保持・確認1・公開0を独立readerで照合します。既存の全タブ保護は確認後の公開直前に働きます。

採取元を列挙した `original/provenance.json` の17ファイルもルート `.gitattributes` で `-text -eol` に固定する。旧C++／GNU原文のCRLFと、adapter CのLFを混在した原bytesのまま保持する。OSのcheckout改行変換を適用しない。原文・slice・copy・goldenのSHAは再生成で変更せず、Git blobとWindows／Macのcheckout bytesの双方をprovenanceへ照合する。初回CIのWindowsではadapter2件、Mac checkoutでは旧原文15件が変換されたため、原文採取元にも保護を追加した。

# .NET 10 / Avalonia 移行の到達点

## macOS E2E の起動記録

SHA `32b8cce957bf12a21fb8aec7706d1590aeb43550` の [4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37226617858) は Windows 両構成と Mac ARM64 が成功し、Mac Intel は E2E の `Process.StartTime` 取得で例外となった。Mac Intel の Native AOT 発行と UI10,919成功／0失敗は完了したが、E2Eは161,456成功／1失敗／2skipで、対象アプリの実終了コードを回収できていない。圧縮原本と失敗 assertion は `artifacts/local/folder-sync-next/github-current-head-inspection-01` に保持する。

検証用 launcher の待機中に nonce／実PIDを照合して実OSの開始時刻を取得し、同PIDのexecでアプリを起動する経路へ変更した。部分出力と完了フラグを同一の確定スナップショットへ揃え、回収失敗をnativeの成功に置き換えない。製品コード・Windowsの起動処理は変更しない。独立したソース確認は通過したが、この変更を含む実Macの実行、取消・pipe解放の実時間と4RID CIは未検証である。操作・記録の契約は [E2Eの手順](../tests/DiffBeacon.E2E/README.md) を参照する。

## バイナリの任意byte選択・clipboardと直接入力（managed／全4RID Native AOT実測）

絶対byte位置のinclusive選択を4096ページ跨ぎで保持し、実Copy／Cut／Paste／FastPaste／選択dialog、直接Hexニブル・ASCII入力、Delete／Backspaceへ接続した。repeat間skipは元suffixを跨ぎ、選択Pasteは常に置換Insert、Overwriteの最後のskipも容量検査する。共有64MiB／256操作と既存Redoを含めた候補を事前準備し、CutはOS公開／flush成功後と、readonly・全入力・project・provider・mode・owner・operation・取消・revision・選択世代の再照合後だけ確定する。未適用Hexを自動適用しない。二者rightのProject側2／Core側1の対応と、通常／内包全側の保存点・workspace／包装を維持する。

固定Frhed原codec634件（encoder263／decoder371）と独立IEEE4件を実アプリ別processで全byte照合した。原版の浮動値return未定義動作・scanf失敗・overflow・型不一致は実行しない。製品は有限IEEE値を安全に生成し、50文字以上の数値prefixを閉鎖の有無によらず拒否する。tokenは最大54byte先読みと2passに限定し、1MiB未閉鎖反復のGUI／CLI結果を独立readerへ照合した。Windows OEMは有界1byte CharToOemBuffAをliteralにだけ適用し、token生成bytesとescapeを保持する。実ACP／OEMCPは932／932、全256API返値は成功でbyte保持だった。macOSは明示1252／437の互換方針であり、Windows OS API実測と区別する。

最終build14は警告0／エラー0、限定E2E7は1,308成功／0失敗／0skip・651命令、clipboard GUIは151成功／0失敗／PNG5枚、既存range GUIは110成功／0失敗。全通常／内包保存bytes、原本ZIP全entry／CRC、相対asset、workspace／包装・展開再読込みをPython標準readerで独立照合した。850×550・45tab以上のHex先頭／ASCII本文と中央SaveAsを別scroll位置で実PNG・座標へ照合した。新ASCII欄で旧Range検証の最下端scrollがHex先頭38pxを隠す失敗を検出し、最低100pxと中央全欄可視を維持してHex先頭へ検証座標を是正した。同じbuild14の実管理者tokenによる全体UI03は10,919成功／0失敗・root描画PNG183枚／生成入力を含むrecursive PNG1,502枚となり、実process終端0・creation／PID・前後runtime SHA一致を確認した。ClipboardとRangeの全保存byte・原本ZIP／CRC・workspace／包装を独立readerで再照合した。成果物は `artifacts/local/binary-clipboard-selection/handoff.json`。

同じ最終ソースのmanagedとWindows x64 Native AOTは、全体E2Eが各161,447成功／0失敗／3skip・6,843命令、全体UIが各10,919成功／0失敗・描画PNG552枚となった。各13,686のindex付きstdout／stderrとNative発行物13fileのSHA／sizeを照合し、実管理者token・process終端0・実行前後runtime一致を確認した。採取した1,933 source／fixtureのうちNative restoreでbyteが変わった2 lockはHEADの依存graphと一致し、他1,931fileはbyte保持。固定codec／OEM／Clipboard／Range readerで全byte・原本ZIP／CRC・workspace／包装展開再読込みを再照合した。親がNativeの850×550多数tabのHex先頭／ASCII／SaveAsとRangeの5 PNGを目視し、同名managed PNGとのSHA一致も確認した。証拠は `artifacts/local/binary-clipboard-parent/full-managed01-independent.json`、`native01-independent.json`、`clipboard-readers-managed01-independent.json`、`range-readers-managed01-independent.json`、`binary-readers-native01-independent.json`、`native-managed01-parent-visual-receipt.json`。Windowsの3skipはUnix mode、Mac実APFS、大文字小文字衝突の環境条件であり、失敗と区別する。

所有rawは識別headerと宣言長でHGLOBAL余白を除き、未知外部rawは実formatの全bytesを明示選択する。最大raw16MiB＋header／text112MiB（Unicode224MiB）のplatform transferを両列挙順で実読込みし、Windows合成OEM有無・終端・allocator余白を共通有界予算464MiB＋332byteへサイズ注入した。CF_LOCALEは付随metadataの余白を予約しただけで実format注入していない。最大実HGLOBAL／GlobalSize・合成format列挙は未実測。利用者OSclipboard／AppDataは変更しない。clipboard sourceSha256はfixture内容のSHAであり製品source SHAではない。旧検索・挿入位置の再整列・旧Hex全操作の同等性・Binary HTML／patchは残工程で、全WinMerge移植の完了とは扱わない。

固定SHA `e03aca4249763d7a5821ce35c514486da04538d7` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37222885296)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37222885318)は成功した。4構成合計E2E645,808成功／0失敗／10skip、27,380命令・54,760のindex付きstdout／stderr、UI43,676成功／0失敗・PNG2,208枚を照合した。公式8ZIPのAPI digest・全entry SHA／CRC／size、発行物・3ライセンス、元codec／OEM・Clipboard／Range／CopyAll／三者Binary／TARの固定readerを確認した。cleanrunnerの別writer／readerでは画像に加え、全256値＋NUL／ASCII／FFの259byte rawとtext・formatを実OS clipboardへ照合した。HGLOBAL header／GlobalSize paddingの観測は含まない。Mac両構成で各14の通常／最小・多数tabのPNGを親が目視した。ZIP内の絶対／相対assetパスを混同した検証adapterの失敗ログを保持し、元readerを変更せず修正後に各RIDの9検証を再照合した。証拠は `artifacts/local/binary-clipboard-parent/ci/all-four-receipt.json` と `parent-final-revalidated.json`。全体は展開せず730,055,821byteの公式ZIPで保持する。通常desktop・実OS pointer・ネイティブ保存dialogと後続の未コミットFolder変更は、このCIの実測に含まない。

## バイナリ全体コピー（managed／Windows x64 Native AOT全体実測）

二者両方向と三者全6方向の「全体コピー」を追加した。旧Frhed `copy_all_from` に合わせ、先頭からsource全byteを上書きし、destination長はmax(source,destination)、短いsourceでは末尾保持、空／同一bytesでは履歴を増やさない。未適用Hexは有効／不正とも明示適用前に拒否する。実確認dialogのキャンセル／続行と待機中の破棄・再比較・owner交代・編集世代・readonly変更を検査し、古い確認で変更しない。

Releaseビルドは警告0／エラー0、限定E2Eは12成功／0失敗／0skip、限定headless GUIは66成功／0失敗、同じApp SHAの全体UIは10,658成功／0失敗。全8方向の保存byteとUndo／Redo・保存点、16MiB入力／共有64MiB／256操作の拒否、内包Binary作業保存・workspace／包装のassetと展開再読込みを実アプリで確認した。固定期待byte・原本ZIP entry／CRC・全assetはPython標準readerで独立照合した。成果物は `artifacts/local/binary-copy-all/handoff.json` と限定runのPNG／JSON／stdout／stderr。ネイティブ保存dialog、実OS pointer、Native AOT／全4RIDはこの限定実測に含まない（後続の全体実測は次段落）。通常保存経路と内包原本への非書戻しの境界は維持する。

後続の最終版でmanagedとWindows x64 Native AOTの全体E2Eは各160,129成功／0失敗／3skip、全体UIは各10,658成功／0失敗。各6,187 commandの12,374 stdout／stderr、544 PNG、Native発行物13fileを独立照合した。261 sourceのうち2 lockはNative restore後にHEAD依存graphへ一致し、残り259fileのbyte保持を確認した。証拠は `artifacts/local/binary-copy-all-parent/full-managed01-independent.json` と `native01-independent.json`。実際の最小850×550・45タブで中央SaveAsと3Hexへの到達を確認した。全体コピーの固定SHA `413a67e789d88d3fa149a71af5134d780479e18e` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37205395791)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37205395786)は成功。4構成合計E2E640,534成功／0失敗／10skip、24,754コマンド・49,508stream、UI42,632成功／0失敗、PNG2,176枚を独立照合した。公式8ZIPのdigest・全entry SHA／CRC／size、固定Binary／CopyAll／TAR reader、発行物、別writer／readerのOS画像clipboard、Macの選択PNGを確認した。証拠は `artifacts/local/binary-copy-all-parent/ci/all-four-receipt.json`。このCIは後続の範囲編集を含まず、通常デスクトップ・ネイティブ保存dialogは未検証。

## 中央も編集・保存する三者バイナリ（ローカル／全4RID検証済）

三者編集・候補採用保護のcommit `a87f4e8ae3e914f78ef1ef56f9e1be6947cc39e0` は [GitHub run 37201634956](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37201634956) の全4RIDとCodeQLで成功した。Windows各160,118成功／0失敗／3skip、Mac各160,127成功／0失敗／2skip、UI各10,592成功／0失敗。公式API digestと8 ZIP全entryを照合し、固定GUI reader・発行物・別writer／readerのOSクリップボード記録・runner採取inventoryを確認した。Mac両構成の通常／最小850×550 PNGを目視し、前回の重なりは再現しなかった。証拠は `artifacts/local/binary-threeway-parent/ci/all-four-receipt.json`。このCIは後続の全体コピー変更を含まない。headless描画と通常デスクトップ・ネイティブ保存dialogは区別する。

通常ファイルと内包Sourceの左・中央・右を独立編集し、全6方向の差分範囲／全体コピー、共有Undo／Redo、側別保存点、中央の通常保存／外部SaveAsへ接続した。workspace version 5の中央相対assetとレポートなし包装も復元できる。各入力16MiB、履歴合計64MiB／256操作を維持し、コピー範囲は対象pairだけで検査する。中央は編集する第三入力であり、祖先に対する競合分類は追加しない。未適用Hexを比較候補の採用で失わないよう、世代と全editor textを同期照合する。

Archive／Image／Folder／Tableの完成候補も読込み前のBinary `StateStamp`・操作・取消・入力・mode・providerで採用を検査する。拒否時は旧panel、中央Hexの未適用入力、適用済bytes、共有履歴と文書・保存点を保持し、候補を解放する。Tableはlocal文書snapshotで初回比較を完了してから同期採用する。修正前のArchive実操作で編集保持・共有状態・候補解放の3失敗を再現し、採用拒否後の状態表示も現在の操作だけへ反映する。全体GUIの多数45タブ・900×600で見出し9行が本文を押し出す2失敗を再現し、見出し欄を縦スクロールへ制限した。選択中タブの縮小後到達も1失敗を再現して単一postの位置更新で修正した。修正後の限定GUIは111成功／0失敗、同じ最終版の全体GUIは10,592成功／0失敗となった。多数45タブを保持した実際の最小850×550でも全3Hexの高さ・差分一覧の非重なり・中央SaveAsへのスクロール・選択見出しへの到達を確認した。三者Binary／既存Binary／内包projectの限定E2Eは22／57／278成功・失敗0・skip0で、物理中央入力もPython標準readerで全bytesを照合した。全runnerは実管理者・終端0・実行前後binary SHA一致を確認した。証拠は `artifacts/local/binary-adoption-repair/handoff.json` と同runのPNG／JSON。通常物理BasePathと内包左右を組み合わせた三者BinaryのDTOは保存・包装でき、旧schema拒否検証を全中央bytesの独立読込みと展開再読込みへ移行した。

同じ最終build13で三者限定E2E22成功／0失敗／0skip・GUI66成功／0失敗、既存二者Binary57成功／0失敗・GUI65成功／0失敗、内包Text67成功／0失敗・GUI138成功／0失敗となった。原本3 ZIPの全entry／CRC／bytes、全6方向保存、中央asset、CLI複製・包装・展開再読込みをPython標準readerで独立照合した。実管理者token、各終端0、App／driverの前後SHA一致と5 lockのHEAD graph同値を確認した。証拠は `artifacts/local/binary-threeway/completion-receipt.json`。多tab・900×600・長い出力pathでHexと差分一覧の非重なり、scroll後の中央SaveAs到達を実PNGとBoundsで確認した。前回CIで観測したMacフォントの重なりは後続CIで再確認した（本節冒頭）。最終修正後のmanagedとWindows x64 Native AOTは、それぞれ全体E2E160,118成功／0失敗／3skip・全体UI10,592成功／0失敗となった。各6,182 commandの12,364 stdout／stderrと542 PNG、発行物13fileを独立照合した。実管理者・終端0・実行前後のbinary一致を確認し、257 sourceのうちNative restoreで2 lockの改行だけが変わり、全lockのHEAD依存graph同値と残り255fileのbyte保持を確認した。証拠は `artifacts/local/binary-threeway-parent/full-managed02-independent.json` と `native02-independent.json`。後続の全4RID GitHub検証は本節冒頭に記載した。任意位置の挿入削除・旧Hex全操作・Binaryレポートの共通メタデータ経路は未完了。旧GenerateReportは本文を生成しないstubのため、byte本文HTML／patchを旧実装済み機能として扱わない。

## 内包バイナリの作業編集・保存（ローカル全体検証済）

内包Binaryは継承readonlyに従って同じ側の16進編集・差分範囲コピー・共有Undo／Redoができ、通常保存で原本アーカイブから独立した作業版を確定する。親一覧・preview・子再比較にも保存版を使う。外部SaveAsでは成功した側だけを通常ファイルへ切り替え、保存中の後発編集はdirtyを維持する。workspace version 5の相対 `.assets/<SHA>.bin` とレポートなし包装へ保存し、Textのversion 4互換を維持する。各Binary16MiB、共通作業store128MiB／256文書、共有編集履歴64MiB／256操作を上限とする。元アーカイブへの自動書戻しは行わない。

比較設定を適用すると編集可能な内包Binaryがreadonlyへ戻る不具合を実操作で再現（47成功／2失敗）し、継承指定の判定へ統一した。修正後のGUI49成功・0失敗／PNG2、限定E2E57成功・0失敗・0skip／27命令が成功し、全54ログ・原本ZIP・保存byte・workspace・包装とPNGをPython標準readerで独立照合した。実管理者token、プロセス終了コード0、App／driverの前後SHA一致を確認した。証拠は `artifacts/local/archive-binary-working/settings-binary-independent.json`。独立レビューで差分選択からHex位置への移動が失われた回帰を発見し、実GUIで再現後に修正した。後方差分への移動・選択維持・実コピーボタン・未適用Hexの保持を含むGUI53件が成功し、固定6byteと保存時の期待byteも独立readerに直接定義した。証拠は `artifacts/local/archive-binary-working/navigation-regression-independent.json`。初回全体E2Eは160,075成功／2失敗／3skipで、親のコマンドに必要な独立Zデコーダー指定がなかった。全12,338ログを照合し、このrunは未採用として保持した。指定を是正した次runでは全体E2E160,090成功／0失敗／3skip、6,169命令の全12,338ログbytesを独立照合した。一方、全体UIは9,883成功／1失敗で、Binaryから通常Textへの切替後に旧Binaryの保存経路へ到達する回帰を検出した。このrunの全体UIは未採用。通常Text／provider本文の採用時に旧viewを解放し、読込み失敗・取消・採用直前の旧Binary編集では前のownerを保持するよう修正した。実GUIで修正前55成功／5失敗、修正後65成功／0失敗、限定E2E57成功／0失敗を確認した。通常保存・外部保存のText全byteとprovider原本はPython標準readerで固定期待値へ照合した。証拠は `artifacts/local/archive-binary-working/full-managed02-e2e-independent.json`。修正後の通常版とWindows x64 Native AOTは各全体E2E160,090成功／0失敗／3skip、6,169命令／12,338全ログ、headless UI10,325成功／0失敗／PNG525となった。189ソースファイル（表記重複を含む369照合行）のbyte保持、全依存lockのHEAD正規化一致、Native発行物13ファイルと3ライセンス、実管理者token・終端0・実行前後SHA一致を独立照合した。証拠は `artifacts/local/archive-binary-working/full-managed03-independent.json` と `native-independent.json`。AOT初回はMSVC環境不足で失敗し、既存DevShellとリンカーを設定した新しい出力先で成功した。固定SHA `0359432641b1eca873c701dfb06ec1b631dc1aa0` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37192314085)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37192314112)は成功した。8 ZIP・105,890 entryの公式digest／CRC／SHA／サイズ、全24,682命令の49,364 stdout／stderr、UI全41,300成功・2,100 PNG、Binary作業版と多層TARの全ファイル出力を独立照合した。Windows各E2E160,090成功／0失敗／3skip、Mac各160,099成功／0失敗／2skipで、全構成UI10,325成功／0失敗。各CPUのPE／Mach-O、各3ライセンスとMac bundle、別writer／readerによる実OSクリップボードも確認した。原本ZIPは全展開せず698,909,484 bytesで保持する。証拠は `artifacts/local/archive-binary-working/ci/all-four-receipt.json`。これは宣言した自動検証範囲の受領であり、全旧機能・全製品レイアウトの完了ではない。artifact ZIPが保持しない展開後の空directoryはrunnerでの固定reader成功ログだけを確認でき、取得ZIPで存在を再照合できない。次CIにはupload前の実体一覧を同梱する。Mac ARM64の実描画PNGで多tab時のHex／差分一覧の重なりを確認し、後続の三者Binary変更で修正中。Binary HTML／patch、三者Binary、任意位置の挿入削除と旧Hex全操作の同等性は未完了で、ネイティブ保存ダイアログ・実OS pointerは今回のheadless検証に含めない。

## 多層圧縮TARの読取り（ローカル全体検証済）

TARに複数のgzip／bzip2／Zを重ねた入力と、tgz／tbz／tbz2／tazに外側の圧縮を追加した入力を読めるようにした。終端をTARとして検証し、全層の終端・CRC、共有深度・復号量・作業量の制限を維持する。通常一覧・entry取得・展開・ZIP再梱包、標準プロバイダーと明示Sourceの経路を接続した。多層形式への書込みは未対応。

2026-10-04のRelease buildは警告0・エラー0。通常版の限定E2Eは1,363成功・0失敗・0skip、484回の実アプリ実行と968ログの全byteを照合し、実行前後のApp／Providers／driver SHAが一致した。固定正常22件・拒否22件とSource用ZIP4件を使用し、Python標準readerが全entry・時刻・内容・展開・再梱包を独立照合した。readerは正常22件のtyped-rootと正常Source2件を必須にし、記録の欠落・重複を拒否する。入力SHA確認のwork64上限と、SHA確認後のアーカイブ処理で拒否されるwork1000上限を別々に確認した。証拠は `artifacts/local/archive-tar-wrappers/e2e-03-independent.json`。この変更を含む通常版とWindows x64 Native AOTの全体E2E160,090成功／0失敗／3skip、全体headless UI10,325成功／0失敗を独立照合した（上記Binary checkpointの証拠）。後続の正常22原本GUIは155成功／0失敗となり、読取り開始時・完成候補採用直前・Refresh採用直前の実中止と、確定表示の保持・再操作・古い候補のDisposeを確認した。全層の独立復号とGUI候補の全63項目・mtime・元bytesを照合し、限定E2E1,366成功／0失敗／0skip・485命令の970全ログ、実管理者・終端0・App／driver前後SHA一致とPNGも独立照合した。証拠は `artifacts/local/binary-threeway-parent/tar-e2e01-independent.json`。decoder処理中のGUI取消タイミングをこの境界検証から推測しない。新GUI検証を含む全体／4RIDは後続で、全機能移植の完了とは扱わない。

## 実在する内包テキストの作業保存（先行checkpointの検証記録）

実在する内包Textは継承した読取り専用指定に従い、通常保存で原本アーカイブとは別の作業本文を更新する。親一覧・preview・子の再比較も保存済み本文を使う。外部SaveAsの成功時だけ保存した側を通常ファイルへ切り替える。元アーカイブへの自動書戻しは行わない。後続のBinary作業編集・保存の到達点は冒頭の節を参照する。

workspace version 4はJSONと相対参照の `.assets/<SHA>.text` を使い、4MiBのJSON上限を保ったまま大きな本文を保存する。各本文64MiB、共有作業128MiB・256文書を上限とし、原本rootのSHAと全階層の整合性を再検証する。包装では原本と作業本文を別々に同梱する。passwordは保存しないが、明示保存した作業本文assetは暗号化しない。公開／読込みしたassetはwindow終了まで上書きを拒否し、異なる暗号化枝の再入力と一時credentialを経路ごとに分離する。共有保存は三者の祖先も同期し、手動マージの本文・履歴・dirtyを保って古い入力の採用を拒否する。

限定E2Eは内包Text67成功・0失敗（32命令）、archive project274成功・0失敗（110命令）、workspace127成功・0失敗・1skip（43命令、非管理者のlink作成特権不足）。独立readerで原本ZIPの全CRC・SHA、作業本文の全bytes、三者HTML、異なる二つの暗号化枝、相対assetと包装を照合した。初回全体E2Eは158664成功・8失敗・3skipで、新たに有効なversion 4を旧テストが未知としていた8件の期待値をversion 5へ更新した。失敗成果物を保持し、修正後の通常版とWindows x64 Native AOTは各158672成功・0失敗・3skip、5658命令と11316 stdout/stderrの全bytesを照合した。両版の全UIは10259成功・0失敗、各523PNGを独立復号し、原本・作業asset・三者HTML・暗号化二枝・包装・CLI再読込みと適用patchも照合した。ソース362件のbytesとdriver2件は不変で、Native発行12file・3licenseのSHA／サイズも一致した。SHA `58c55469b80f589fc4bd0cca4c57fd5de5e00d15` のCI `37182667198`はWindows両構成とMac ARM64が成功したが、Mac x64のE2Eは158604成功・4失敗・2skipで未採用。Mac x64のUI10259条件は成功している。公式digestと全entryのCRC／SHA／サイズを照合した失敗ZIPから、限定GUI2命令が30秒で強制終了し、未生成のUIレポートを読む独立reader2件も失敗したことを確認した。自己検証の子プロセス予算を見直し、同OSの再実行で完了を確認する。失敗根拠は `artifacts/local/archive-present-text-working/ci-osx-x64-failure-analysis.json`。通常desktop・ネイティブ保存ダイアログは未検証。独立レビューのasset保護・暗号化枝・祖先同期と最終captionを修正し、追加の不正正規表現によるマージ再開始失敗も実行再現後に修正した。最終限定GUIは138成功・0失敗で、再開始失敗時の本文・履歴・dirty・stale保持、window終了時の解放を含む。11PNGと原本・本文・相対assetを独立照合した。証拠は `artifacts/local/archive-present-text-working`。これは全機能移植の完了ではない。

## 不在側の未命名文書と外部保存

不在Text側の元のreadonly指定を、実在アーカイブの固定readonlyとは別に保存・多段継承する。同じ比較側で直接編集と選択差分コピーを行い、初回保存は外部SaveAsへ進む。原子的公開と再読込みの成功後だけ保存した側を通常ファイルへ切り替え、保存中に増えた編集はdirtyのまま保持する。親タブをすべて閉じた後も原本を保護し、取消・古い完了・公開後の採用失敗では本文と不在証拠を保持する。未保存の不在本文をworkspace／包装へ黙って落とさず拒否し、単体HTMLへ未保存表示と本文を反映する。継承情報のない既存projectは保守的にreadonlyとして復元する。実在entryの編集と作業文書への通常保存は上の後続checkpointを参照する。旧通常Saveは展開した実ファイルを更新し、元アーカイブへの自動書戻しは行わない（`Src/MergeDoc.cpp:1096`、`Src/7zCommon.h:27`）。

GUI限定検証は94成功・0失敗。保存側に実在するcontainer階層がある場合のSaveAs後再比較を実行再現し、採用成功時だけ保存側のpassword cacheを新しい階層へ更新した。対向側と原本を保持する。元root保護の脱落を実アプリで再現し、外部保存後・親閉鎖後・パス消去後の再指定を拒否する回帰検証を追加した。公開前の新入力タブとreadonly変更も既存出力を保持する。独立したPython標準readerで、二段ZIPの全entry・CRC・全byte、取消／古い完了の既存出力、保存・公開後の採用失敗・実在0byteの本文、未保存／包装HTMLの全文と改行、包装の全fileと相対参照を照合した。実アプリE2Eは240成功・0失敗で、同じGUI経路・包装展開・CLI HTML再読込み・独立した本文への包装patch適用まで確認した。管理者実行の通常版とWindows x64 Native AOTの全体E2Eは各158606成功・0失敗・3skip、5626命令と11252ログの全byteが一致した。両方の全UIは10215成功・0失敗、各520PNGを独立復号した。ソース355fileのうち353fileは不変で、2つのcompiler lockだけが既存GitのAOT graphへ復帰し、製品依存関係と検証driverは不変。Native発行12file・3licenseのSHAとサイズも一致した。新しい文脈の独立出荷レビューで見つかった保存後再比較の不具合は実行再現・修正・再検証し、修正確認で追加指摘はなかった。SHA `eb0aeb050e670f7c2ed8aa3cdfb22dce8ceb0e5b` の4RID CI `37175679220` とCodeQL `37175679228`は成功した。Windows各158606成功・0失敗・3skip／5626命令、Mac各158615成功・0失敗・2skip／5629命令。各UI10215成功・0失敗／520PNG、原本・HTML・包装全file・適用patch、発行物・ライセンス・Mac bundleと別processの実OS clipboardを独立照合した。8ZIP・683015978bytes・99138entryの全CRC／SHA／サイズと公式digestが一致し、全面展開量は0。証拠は `artifacts/github/37175679220/terminal-summary.json`。この結果は不在側の編集・外部保存を含む同SHAの検証であり、後続の実在entry作業版の実装を含めない。証拠は `artifacts/local/archive-unnamed-editing`。ネイティブ保存ダイアログの測定とは区別する。

## 片側不在の内包入力（4RID検証済み）

実在する親rootと不在の格納名を分けるversion 3のtyped入力を追加した。片側だけのText／Binary／Archiveを開き、不在containerを空一覧として二段先のleafまで進める。実在する0byte fileと不在を保存・HTML・包装patchで区別し、全親containerのCRC・EOF・SHAと不在の起点を再検証する。不在側を実在する親の再梱包・全件展開へ渡さない。元アーカイブへの書戻しは未対応のまま固定readonlyを維持する。

追加した実アプリE2Eは203成功・0失敗（81命令）、独立readerは8比較の原本・本文・全包装fileを照合し、6patchの適用結果を独立本文の全byteと照合した。GUI限定検証は62成功・0失敗で二段の不在階層・readonly・原本保持・出力保護を確認した。不在captionを本文見出しにも表示し、実controlとPNGで確認した。全UI検証は10,183成功・0失敗、518PNGと元container・HTMLの独立照合も成功した。証拠は `artifacts/local/archive-missing-input`。管理者実行の通常版とWindows x64 Native AOTは、全体E2Eが各158,569成功・0失敗・3skip、5,611命令と11,222ログの全byte照合に成功した。Nativeの全UIも10,183成功・0失敗、518PNGと原本・HTMLを独立照合し、12発行file・3ライセンスとソース351fileを照合した。2つのcompiler lockだけが既存GitのAOT graphへ戻り、製品依存関係と検証driverは不変。最初の非管理者実行はリンク保護など12skipのため採用せず、原本を全22,580file照合した約58MBのZIPへ保持した。ごみ箱の完全消去は中断し、約818MBと管理情報が残るため清掃完了とは扱わない。SHA `545d3a674a73ed3ac3d41f3648b4aa24b191858f` の4RID CI `37167958727`とCodeQL `37167958698`は成功した。Windows各158569成功・0失敗・3skip／5611命令、Mac各158578成功・0失敗・2skip／5614命令。各UI10183成功・0失敗／518PNG、8比較と6適用patch、全発行物・3license・Mac bundleと別processの実OS clipboardを再照合した。8ZIP・677587837bytes・98646entryの全CRC／SHA／サイズと公式digestが一致し、展開量は0。証拠は `artifacts/github/37167958727/terminal-summary.json`。このSHAは未命名側の編集・SaveAs追加前のcheckpointであり、後続変更の検証を含めない。通常desktop・ネイティブ保存ダイアログ、新しい文脈の独立出荷レビューは未完了。全機能移植の完了ではない。

## 内包アーカイブの子比較・保存・包装

左右に実在するentryを子タブのText／Binary／Archiveとして開く。自動判定、別名書出し、三者・通常文書との混在Text比較、version 2のtyped workspace、leaf本文の単体HTML、root snapshotを含む包装・leafパッチを接続した。物理rootと格納名・leaf・確定SHAを分け、固定readonlyと全タブの入力・filter・workspace出力保護を維持する。復元後は「比較」の明示操作を待ち、各階層のpasswordをmasked欄で再入力する。passwordと復号本文は保存しない。候補は準備後に親・世代・token・activeタブを照合して追加し、取消・別タブ・refresh・親closeで古い完了を破棄する。

通常版のRelease buildは警告0・エラー0、全体E2Eは158367成功・0失敗・3skip、5530命令・11060 stdout/stderr fileを照合した。全UIは10174成功・0失敗で、Sourceの実操作53件を含み、517PNGのCRC・寸法・復号長・filterを独立照合した。通常／最小windowの一覧・previewは291／93px、scroll後の比較button到達も確認した。全container CRC・leaf／全byte書出し・二者／三者HTMLの原文と改行、暗号化workspaceへのpassword／復号本文の不在をPythonで再照合した。プロジェクトは110命令、独立した7比較・3外部leafパッチ適用と、包装全file・展開・相対参照再読込みを確認した。ソース348件・managed App payload551件・driverの検証前後一致を保持する。

Windows x64 Native AOTも全体E2E 158367成功・0失敗・3skip、5530命令、UI 10174成功・0失敗、517PNGの独立照合が成功した。payload12件／3licenseのSHA・サイズ、source348件の照合、既存compiler graphへ復帰した2lockfile以外の346件のbyte不変とdriver2件の不変を確認した。通常デスクトップと実OS clipboardはこのローカル検証に含めない。

このGUI／typed workspace／包装を含むcommit `417e574d2cf81d5c87acd2cf49e7e9a5f28af6c8` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37160106400)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37160106402)が成功した。Windows両構成はE2E 158367成功・0失敗・3skip、5530命令、Mac両構成は158376成功・0失敗・2skip、5533命令。全4構成でUI 10174成功・0失敗、各517PNGを独立復号し、Sourceの全byte／CRC・本文と改行・7包装・3外部leafパッチを再照合した。実OS clipboardは各構成の別writer／reader processで一致した。8ZIP／676298066bytes／97338entryの全CRC・SHA・サイズとGitHub digest、Native payload・3license・Mac bundleの13内部fileと実行属性も確認した。証拠は `artifacts/github/37160106400/terminal-summary.json`。全4RIDは展開保持していない。通常interactive desktop・ネイティブダイアログと、新しい文脈の独立出荷レビューは未完了である。

初回全体の旧workspace検証が新たに有効になったformatVersion 2を未知としていたため、未知versionを3へ修正した。全UIでown archiveの上書き拒否が共通guardに先取りされ例外型が変わったため、own rootsのIOException検査を先にし全入力保護を後段へ維持した。失敗入力・ログ・JSONと、その修正後の全体／UI再実行を分けて保持する。独立PNG照合の初回がtop levelだけ148枚を列挙していたため、既存契約と同じ再帰・fixture除外で517枚を確認した。

深いcontainerの再梱包／全件展開、片側だけのentry、画像／provider子比較、Source Binary／ArchiveのHTML・report／patch包装、暗号化SourceのCLI report／包装、通常desktop・ネイティブダイアログと新しい文脈の独立出荷レビューは未完了。証拠はartifacts/local/nested-archive-source/gui、最新Nativeはartifacts/native-runs/archive-project-gui-20261004。これは全機能移植の完了ではない。

## 内包アーカイブの明示取得

Providersのimmutable `ArchiveSource` と、実アプリCLIの `--archive-source-list`／`--archive-source-entry` を追加した。物理rootと内包chainを別に保持し、全外側containerの後続entry・CRC・footerまで検証した後だけ次段へ進む。深度・復号量・名前・件数・TAR header・作業量を共有し、rootのSHA変更、descriptorへの上書き、readonlyとlinkを拒否する。各内包保持は256 MiB以下、通常streaming TAR全体の上限は縮めない。以下はservice／CLI checkpoint時点の記録である。GUI子タブ・workspace・包装・HTML接続は上の最新ローカル検証を参照し、深いcontainerの再梱包／展開は引き続き未対応。

Release警告0・エラー0、Windows通常版とx64 Native AOTのSource限定E2Eはそれぞれ管理者processで323成功・0失敗・0skip、127 App commands。通常版の全体E2Eは158094成功・0失敗・3skip、5420 App commands。headless UIは両版10121成功・0失敗で、各512 PNGのCRC・寸法・復号長・filterを独立照合した。Native発行物12件のSHA・サイズと3ライセンス、検証前後の339 sourceを照合し、Native restoreによる2 lockfileの既存compiler graph復帰と製品依存の不変も確認した。

Python標準libraryが18固定入力と11正常階層の全内容・CRC、16取得file、280MiB TARのraw SHAと各140MiB entryを独立照合した。固定入力一式は約327KBで、巨大raw TARを保存していない。別の実アプリ経路で、root／内包のsolid 7z・RAR5、暗号化7z／ZIP／RAR5、未知名TAR.ZとSHA付きdescriptorの再取得を両版各22命令で確認した。最初の非管理者実行は310成功後にlink作成特権不足でrunnerが中断したため失敗として保持し、管理者再実行でlink入力／出力保護も検証した。通常デスクトップや実OSクリップボードは今回のローカル検証に含めない。証拠は `artifacts/local/nested-archive-source`。

このservice／CLIを含むcommit `d8e9669481d3b872988eeef19e69e5558134d4c0` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37147393452)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37147393447)は成功した。Windows両構成の全体E2Eは158094成功・0失敗・3skip、5420命令、macOS両構成は158103成功・0失敗・2skip、5423命令。全4構成のUIは10121成功・0失敗、各512PNGを独立復号し、実OSクリップボードも別writer／reader processで一致を確認した。8ZIP・671689114byte・95670entryの全CRC／SHA／サイズとGitHub digest、固定原本、Source独立proof、Native payload／license、Mac bundleの内部全file・実行属性を照合した。全4RIDを展開保持せず、ZIPと小さい採用receiptを `artifacts/github/37147393452` に保持する。Source CLIの実取消、新しい文脈の独立出荷レビュー、通常デスクトップ・ネイティブダイアログの検証は未完了であり、新しいGUI／プロジェクト変更はこのservice／CLIのCIに含まない。

## ZIP・7z・RARの明示的な多重圧縮

初回の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37135868237)はWindows／MacのARM64が成功し、x64両構成はアーカイブの中止自己検証で失敗した。UIキューへ中止を予約した小さな入力が中止前に完了していたため、実際の中止ボタンを比較操作作成後・読込み開始直前の観測点から押す方式へ変更し、各取消の失敗名に境界を明記する。元の表示保持・modal・採用直前・refresh取消の検証は維持する。失敗ZIP2個の全entry CRC／SHA／サイズを照合し、`artifacts/github/37135868237`へ保持した。[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37135868265)は成功。修正後の成功runと初回の失敗を区別する。

中止検証の修正後はRelease警告0・エラー0、通常版UI10,121成功・0失敗、全体E2E157,722成功・0失敗・11skip。5,274 App呼出しと10,548 stdout／stderrを照合した。Windows x64 Native AOTの発行とUI10,121成功・0失敗、発行13ファイルのSHA／サイズも確認した。発行後の補助検証はCore／ProvidersのlockへNative用ILCompiler情報が追加され終了1となったが、追跡済みGitのNative graphと一致し、compiler情報だけを除いた通常版graphは事前SHAへ正確に戻ることを確認した。製品依存を変えないこの差分だけを厳密に検査する形へ補助検証を修正し、311 sourceのbytes不変・2 lockの既知restore差分を照合して終了0。元の終了1は保持する。証拠は`artifacts/local/archive-wrapper-cancel-fix`。UI観測点だけの変更に対しローカルNative全体E2Eは繰り返さず、修正後の4RID CIで全体E2Eを実行する。

ZIP/JAR/EAR/WAR/XPI・7z・RARを`.gz`・`.bz2`・`.Z`で包んだ明示名を、GUI Auto・標準archive provider・CLIの一覧／比較／エントリ保存／再梱包／全件抽出へ接続した。各層を終端まで検証し、深度・中間内容・累積復号量・実読込み作業量を共有する。暗号化入力の初期失敗は任意の左右パスワードで再試行でき、取消・失敗・古い完了で確定表示を保持する。ZIPの宣言CRCとサイズは展開前に固定し、不一致時は既存出力を置き換えない。

Release buildは警告0・エラー0、通常版の限定E2Eは1,118成功・0失敗・0skip、全体E2Eは157,722成功・0失敗・11skip、headless UIは10,121成功・0失敗。34正常鎖の全層と再梱包／抽出bytes、中央CRC／サイズ不正の拒否を独立Python／公式Zで確認した。UIの8状態で一覧・プレビュー・表示元を保持し、7z／RARの全11nodeの型・サイズ・UTC時刻を固定原本へ照合した。証拠は`artifacts/local/archive-wrapper-integration/managed-acceptance.json`。Windows x64 Native AOTもUI10,121成功・0失敗、全体E2E157,772成功・0失敗・3skipで終了し、5,293 App commandsと10,586 streams、発行12payload＋manifest、313 sourceの不変を確認した。Nativeの証拠は`artifacts/local/archive-wrapper-integration/native-full-acceptance.json`。新規reviewerはスレッド上限で起動できず、新しい文脈の独立レビューは未完了。内包entryの子タブ比較、裸の圧縮ファイル、TARの複数wrapper統一、wrapper出力作成は後続工程で、全移植完了とは扱わない。

中止検証修正SHA `10ea3e7dad8363689f5c33ffc379f6565c1523c8` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37139908624)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37139908632)は成功。全構成UI10,121成功・0失敗、Windows各157,772 E2E成功・0失敗・3skip／5,293命令、Mac各157,781成功・0失敗・2skip／5,296命令。全stdout／stderrとJSONが一致し、timeout／起動失敗0。各構成512 PNGを独立復号し、5取消境界、8表示保持状態、11 embedded metadata、34正常wrapperと34再梱包／抽出出力、2 central metadata拒否を確認した。発行manifest全payload SHA／サイズ、Mac bundle内部全fileと実行属性、別writer／readerの実OS clipboardも照合した。8 ZIP・94,138entryのCRC／SHA／サイズとGitHub digestが一致し、670,829,040 bytesの圧縮状態で保持する。全体E2E・発行物は展開していない。証拠は `artifacts/github/37139908624/terminal-summary.json`。このCIへ後続の未commit Source実装は含まない。

差分色の不透明度を0～1（既定0.7）でGUI・通常CLI・プロジェクト設定・単体／包装HTMLへ接続した。重ね合わせの不透明度とは別の値として保持し、原画・差分分類・共有履歴を変更しない。描画候補が未採用・取消・失敗の場合は保存値を変えず、alphaだけの変更では矩形選択・浮動貼り付けを保持する。固定原本12ケース146状態と、未選択86状態の通常CLI・HTML・包装・展開再読込みを使う。ローカル通常版・Windows x64 Native AOT版のheadless UIは各9279成功・0失敗。Native発行12ファイルの全SHA・サイズと原本146状態の全BGRAも独立照合済み。証拠は `E:/DiffBeacon-artifacts/local/image-alpha`。Windows x64 Native全体E2Eは149863成功・0失敗・3skipで終了し、18524ファイルの全SHA・サイズ照合後に約873 MiBを約43 MiBのZIPへ圧縮保持した。通常版全体E2Eも149818成功・0失敗・10skipで終了し、新しい文脈の独立レビューに未解決Critical／High／Mediumはない。この変更の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37081748402)はWindows両構成が成功し、Mac両構成は最小ウィンドウで差分色不透明度スライダーがツールバーの表示範囲からはみ出すため自己検証が失敗した。Windows両構成の全体E2Eは各149863成功・0失敗・3skip（実ログ確認）、Mac両構成は自己検証6300成功・2失敗で全体E2E未実行。[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37081748401)は成功した。Macの失敗証拠は各ZIPの全706entryを照合して保持し、前の矩形編集コミットのCI成功と区別する。

最小ウィンドウの操作欄はテーマの操作部品を実測して高さ下限を決め、低い画像パネルでは縦余白を28 DIP詰める。通常の6条件（通常／最小サイズと実テーマ／64／80px Slider）を保持し、Windowsの明示的な追加寸法実験では約32px少ない可用高さを与える。修正前の画像viewport 0pxを、修正後は27px×3面へ改善し、スライダー・モード・PNG保存の全到達判定に成功した。最新通常DLLの全UIは追加実験込み9299成功・0失敗（終了0）、全体E2Eは149818成功・0失敗・10skip（終了0）、Release buildは警告0・エラー0。Windows x64 Native AOTでは通常6条件を含むUI9295成功・0失敗、原本146状態316面の全BGRAと発行物12ファイルの全SHA・サイズが一致した。発行とmanifest完成後の検証wrapperは最終ログ保存のI/Oエラーで終了1となったため、実処理の成功と区別して記録した。証拠は`E:/DiffBeacon-artifacts/local/alpha-toolbar/managed-ui-compact-proof.json`、`native/attempt4-native-ui-proof.json`とPNG。通常6条件だけの件数と区別する。修正SHA `f578d3c7240f1a27a3032cc9ae9b360ffcb2b85e` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37087078586)では全構成のheadless UIが9295成功・0失敗となり、原本146状態316面の全BGRAと各36 PNGを独立照合した。全体E2EはWindows両構成が各149863成功・0失敗・3skip、Mac ARM64が149872成功・0失敗・2skip、Mac x64が149870成功・2失敗・2skip。Mac x64の2失敗は全ページ画像HTMLで累積描画上限を拒否する1コマンドの30秒タイムアウトに由来し、入力・既存HTMLは保持された。このrunは失敗、[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37087078589)は成功。成功3構成の発行manifest全12ファイルとOSクリップボードの別writer／readerも照合済みで、失敗Mac x64の発行物アップロード・OSクリップボードは未実行。7 ZIPの全79376entryを照合し、証拠は `artifacts/github/37087078586/terminal-summary.json` に保持する。全ページHTMLは予算成立後に描画する経路へ修正した。Windows通常DLLの同入力で拒否7,580ms→274ms、挿入削除限定E2E12,542成功・0失敗・0skip、headless UI9,369成功・0失敗、ビルド警告0・エラー0。単体／包装の正常多ページ・原画と整列全BGRAを独立PNG復号で確認し、正常・上限拒否・OS強制終了・実CancellationToken取消で一時データが残らないことを観測した。証拠は `artifacts/local/image-report-preflight/terminal-summary.json`。新しい文脈の独立コードレビューに成立する未解決指摘はない。修正後の通常版全体E2Eは150,137成功・0失敗・10skip、終了0。Windows x64 Native AOT全体E2Eも150,182成功・0失敗・3skip、終了0となり、同じ全ページHTML拒否は53msで終了した。後処理IO故障注入と修正後の4RIDは未確認。

画像の位置ずらしを比較・コピー核、GUI矢印操作、通常`--image`、プロジェクト保存・復元、GUI／プロジェクト／包装HTMLへ接続した。原本DLLの12ケース43状態を3回採取して全bytes一致を確認し、正規化・支持矩形・左上拡張・位置を戻さないUndo・原画PNGを固定E2EとGUI自己検証で照合する。位置調整とワイプは下記の6モードに接続した。[位置ずらしの契約](IMAGE-VIEWER.md#静止画像コピー核の診断)を参照する。

位置ずらし対応のSHA `d0644bcbf04ff12ba2dfd44ef2a4af275679b956` は [4構成のGitHub Actions](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36941324269) と [CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36941324655) が成功した。Windows両構成の全体E2Eは76780成功・0失敗・3skip、Mac両構成は76789成功・0失敗・2skip、headless UIは全構成3093成功・0失敗。原本の12ケース43状態と93画面、GUI HTML4画像、既存回転反転の14ケース124状態346画面を各構成の成果物へ独立照合した。8成果物はZIPのまま保持してentryを照合し、抽出はmanifestと代表PNGだけ、Native発行物・全体E2Eの一括展開は0 bytes。証拠は `artifacts/verification/image-copy-cli/ci-36941324269-summary.json` と `E:/DiffBeacon-artifacts/github/36941324269`。通常デスクトップ操作とMac署名・公証はこの検証に含めない。

この変更は新しい比較アプリの実装と Native AOT 発行経路の追加であり、旧 WinMerge の全機能との互換性を完了したものではない。旧 C++ / MFC の `Src/Merge.rc` と各実装は残っているが、新しい `DiffBeacon.slnx` の通常ビルド経路には含まれない。旧機能の存在と、新しいアプリで実装・検証された機能を区別する。

画像の行・列整列、逆座標と構造コピーをGUI・通常CLI・プロジェクト設定・単体／包装HTMLへ接続した。原本58ケース304状態の全ghost画素・座標対応・コピー・Undo／Redo・位置と方向・原画PNGを照合し、強調の別原本12ケース146状態で通常／選択削除色、透明実画素、alpha0/.3/.7/1を検証する。最新通常DLLの限定E2Eは9110成功・0失敗・0skip、headless UIは7513成功・0失敗、Release buildは警告0・エラー0。GUIの固定alpha .7は原本12ケース62状態を実Bitmap全BGRAへ照合し、最小ウィンドウのモード選択とPNG保存も確認した。通常デスクトップと、GUIのalpha0/.3/1選択はこの検証に含まない。証拠は `E:/DiffBeacon-artifacts/local/image-insertions/integration-managed-4` と `integration-ui-3`。全体E2Eは通常DLL145951成功・0失敗・10skip、Windows x64 Native AOT145996成功・0失敗・3skip。Native AOTの発行は警告0・エラー0、headless UIは7513成功・0失敗で通常DLLと画像観測SHAが一致した。全体E2Eの入力・出力・ログは全ファイルSHA-256照合済みZIPで保持し、展開分を除去する。変更後のGitHub初回検証（commit `5194e6e9c`、run `36952571103`）はWindows x64成功、CodeQL成功、Mac両RID失敗。Mac ARM64はAOTコンパイル後の自己検証で入力属性の期待値が不正なため失敗した。対象PNGのSHAは原本と一致する。検証のNormal／ReadOnly併用を修正し、設定後のOS属性と読取り専用フラグを確認する。GUI検証の修正後（commit `40648ef6a`、run `36954000118`）はWindows両RIDとCodeQLが成功し、Mac両RIDは失敗した。Mac ARM64の自己検証7539成功・0失敗を確認したが、E2Eに同じ属性期待値の不備が残り2件失敗した。E2Eも設定後の実属性を保存する方法へ修正し、次の変更SHAで4RIDを再検証する。

矩形画素操作の診断CLIへ半開矩形の削除とBGRA貼り付けを接続した。無改変原本32ケースのうち現行入力・安全契約に適合する20ケース、共有履歴・Undo／Redo・16方向組合せ・原画PNG・全比較gridと領域・入力保持・schema／履歴／作業量上限を実アプリで検証し、限定E2Eは708成功・0失敗・0skip。除外12ケースは証拠JSONへ明示する。この段階の検証は診断CLIと画素APIが対象。後続のGUI接続とクリップボードの実測は次段落に記載する。Windows x64 Native AOTの全体E2Eは146705成功・0失敗・3skip、通常版とNative版のheadless UIは各7539成功・0失敗。Native発行12ファイルのSHA・サイズを照合した。通常版の全体E2Eは146660成功・0失敗・10skip。変更後の4RID検証は未実施。限定証拠は `E:/DiffBeacon-artifacts/local/image-rectangles/e2e`、全体の入力・出力・ログは全15876ファイルのSHA照合済みZIP（912321302 bytesから43342084 bytesへ圧縮）と容量索引に保持する。

矩形編集のGUIへ選択・Copy／Cut／Delete・選択範囲の移動とCtrl複製、浮動貼り付け、右辺／下辺／右下角のResizeを接続した。最新通常DLLのheadless自己検証は8351成功・0失敗（追加812項目）。原本Resize96ケースと矩形操作20ケース、全BGRA・共有履歴・PNG保存再読込みを確認した。独立レビューの5件（Resize待ち中Escape、選択範囲移動、フォーカス先、画像原点、非同期マウス解除）を修正し、実Key／Pointerと採用前の待機ゲートで検証した。代表PNGも確認済み。証拠は `E:/DiffBeacon-artifacts/local/image-rectangles/gui/managed-2`。同出力先は失敗候補をZIP照合後に再利用したため、最新の `ui-report.json` と今回生成した観測／PNGを証拠とし、残存入力すべてを最新実行の生成物とは扱わない。headlessのクリップボード検証は製品のAvalonia／native API境界を注入し、実OS操作と区別する。commit bcda5ede591e7b619c43118f9ac10d2f58d42862 のGitHub Main run 36961800413とCodeQL run 36961800407は成功。Windows両RIDはE2E各146705成功・0失敗・3skip、Mac両RIDは各146714成功・0失敗・2skip、全RIDのUIは8351成功・0失敗。各OSの別writer／reader processで固定24 BGRA bytesとPNGを照合し、Windowsの正の高さ・bottom-up CF_DIBも独立確認した。実OSの成功項目はWindows各16、Mac各12、失敗0。8 ZIPを一度だけ取得し全68766 entryのCRC・SHA・サイズ、Windows各12／Mac各26発行ファイルとMac tar各13 entryを照合済み。証拠は E:/DiffBeacon-artifacts/github/36961800413/summary.json と8 ZIPに保持し、必要展開は7229188 bytes、発行物の展開は0 bytes。通常デスクトップの保存ダイアログ・旧FreeImageとの実相互運用は未実測。最終版の通常全体E2Eは146660成功・0失敗・10skip、Windows x64 Native全体E2Eは146705成功・0失敗・3skip。Native発行は警告0・エラー0、UIは8351成功・0失敗、発行12ファイルのサイズ／SHAと通常版との画像観測6ファイルのSHA一致を確認した。全体E2Eの入力・出力・ログは31712ファイルを全entry照合済みZIPへ圧縮して展開元を除去した。実測集計は `E:/DiffBeacon-artifacts/local/image-rectangles/gui/final-local-verification.json` と `native-ui-proof.json`、格納先は容量索引に保持する。

独立レビューで発見した全ページの描画予算漏れは、整列後canvasに対する累積256Mの処理前検査へ修正した。整列前240M／整列後360Mの10ページTIFFで旧ビルドの誤受理を再現し、修正後の終了2・空stdout・既存HTMLと入力保持、選択1ページの成功を確認した。また、幅不一致を先にfalseとする原本の判定順を保持し、横方向の三者整列の誤拒否を修正した。原本fixtureは変更していない。検証済みの過去出力は容量索引のZIPへ格納する。

テキストと表の行比較のDiff算法は依頼によりGNUベースの一種類に統一する。原本との対応検証と処理上限を備えた現行実装を採用し、旧WinMergeの算法選択ドロップダウンと別算法の移植は完了条件から外す。画像行の挿入削除検出にはWinIMerge既定Myers一種類を移植する。行内のWordDiffや比較フィルターは引き続き対応範囲に含む。

画像行Myers核は原本の分類順・signed char量子化・32bit hash・discard・同点規則を移植した。[固定原本](../tests/Fixtures/ImageLines/README.md)14,797ケースの全scriptと行hashを実アプリの`--image-line-script`へ照合し、予算修正後の通常DLL・Windows x64 Native AOTの限定E2Eは各59215成功・0失敗・0skip。Release buildとAOT発行は警告0・エラー0。入力へ期待scriptを渡さず、寸法・負の閾値・原本で整数量子化が未定義になる閾値・後半ケースの不正入力、予算境界と残量不足を検査した。独立レビューで見つかった処理完了後の累積予算検査を、比較開始前の残量引渡しと各処理中の検査へ修正し、部分stdoutと入力改変がないことを再検証した。修正前の通常DLL全体は135938成功・0失敗・10skip、修正後の管理者実行AOT全体は135994成功・0失敗・3skip、AOT headless UIは3093成功・0失敗。両版の行比較観測SHAは一致し、発行12ファイルのSHA・サイズも一致した。証拠は `E:/DiffBeacon-artifacts/local/image-insertions/line-progress.json` と容量索引のZIP。この記録は行比較核の段階であり、後から追加した整列診断・座標対応の全状態の実行検証、画像GUI・構造コピーへの接続、変更後の4RID検証は未完了。

フォルダー確認付きコピーの先行版`source-freeze-06`は、Windows通常版の全体E2E161,626成功／0失敗／4skip、全体headless UI11,074成功／0失敗となった。18の独立readerと実processの終端を確認し、30,888成果物のSHA・サイズを再照合した。通常／最小850×550・45タブのPNGで一覧とコピー操作への到達も確認した。証拠は`artifacts/local/folder-sync-next/whole-verification/managed06-parent-revalidated.json`と`managed06/handoff.json`。後続のルート重複・特殊コピー先・利用中filter・ADS格納元とextended表記の保護修正は、この先行版の実測に含まない。後続の`source-freeze-10`では保護修正を含むWindows通常版の全体E2E161,640成功／0失敗／4skip、全体headless UI11,083成功／0失敗。18独立reader、6,989実processの終端、32,461成果物の主stream SHA・サイズを確認した。証拠は`artifacts/local/folder-sync-next/review-fixes/managed10/final-verification.json`と`files-final.json`。これらは次の全file ADS経路を追加する前の実測であり、追加経路の全体実測は次段落に記載する。追加後の`source-freeze-13`は限定E2E104成功／0失敗／1skip、6CLI・11GUIの全streamを独立readerで照合した。中止表示と共有descriptor／UTF16名の境界を追加した`source-freeze-16`はRelease警告0／エラー0、限定headless UI203成功／0失敗。旧44GUI・7reviewと追加4中止・11stream・4予算GUIの全bytes／SHA／rootを含むmetadataを独立照合した。中止表示の修正前3失敗と検証側の期待不良1件は`managed14`へ保持し、確認待ち前の中止を避ける検証順序も修正した。追加後の全体・同OS AOT・4RIDはこの限定結果に含めない。証拠は`artifacts/local/folder-sync-next/windows-stream-implementation/managed13/parent-limited-acceptance.json`、`managed16/gui-reader-result.json`と`standalone-stream-gui-reader.json`。

全file ADS・中止表示・共有予算を含む最終版では、Windows通常版とWindows x64 Native AOTの全体E2Eが各161,653成功／0失敗／4skip・6,967命令、全体headless UIが各11,123成功／0失敗となった。各20の独立readerで全stream・metadata・保存bytesと固定期待を照合し、実process終端0と製品runtimeの前後一致を確認した。Mac起動記録を修正したmanaged19ハーネスは、コンパイル済みDLL／PDBと全ソースを照合して同じWindows x64 Native製品への全体E2Eに使用した。通常／最小850×550／多数tabのコピー操作をPNGで確認した。証拠は`artifacts/local/folder-sync-next/windows-stream-implementation/managed17/final-verification.json`と`native19/final-verification.json`。実Macの起動・取消・pipe解放、最新4RID、通常desktop・実OS pointer・ネイティブdialogと完全なShell metadataは未検証／未対応であり、このWindows実測に含めない。

SHA `2a5f01ae9fb3cd9264da4bd25d2680bedbe76057` のCI `37260364221`はWindows x64／ARM64が成功した。Mac ARM64はNative発行後の全UI11,094成功／2失敗で、最小／多数タブのフォルダー一覧が90pxとなり必要な100pxを満たさなかった。Mac Intelは10,919条件成功／0失敗の後、フォルダー検証開始時に全体600秒の制限へ到達し、最終UI集計とE2Eは未完成だった。同じ製品コードの後続SHA `5a37db2ba2e8b8690660c9ee8df8138fd3854f8b` のCI `37260736983`はWindows両構成成功、Mac両構成は全UI11,094成功／2失敗で終了した。この後続Intel実行はtimeoutではない。フォルダー表示時だけ共通設定欄を縮め、スクロールで入力・比較・除外・フィルターへ届く検証を追加した。Windows限定UIは218成功／0失敗で、追加15条件と最小一覧132pxを確認した。Mac CIの全体枠は1,200秒へ変更し、個々の操作制限と合否条件を維持する。

修正後のWindows通常版とWindows x64 Native AOTの全体E2Eは各161,653成功／0失敗／4skip・6,967命令、全体headless UIは各11,138成功／0失敗となった。通常版UIの取得済みruntimeとソースは今回のコンパイル対象に一致し、Native UIも実行して確認した。全体E2Eの独立readerは通常版9本・Native9本、Native UIは11本すべて実終了0。採用したFolder readerは既存44ケース・7保護ケース・4中止ケースの全bytes／metadataを維持し、旧3PNG期待を6枚へ更新してPNGのCRC・zlib scanline・寸法と15操作の座標を照合する。親は通常／最小の三者画像viewportとPNG保存ボタン、最小／45タブのフォルダー一覧とスクロール後の設定欄を実PNGで確認した。Nativeの実publishは終了0だったが、発行後guardはSDK暗黙ILCompiler追加によるCore／Providersの2lock byte変更を拒否して終了1となった。元bytesの再構築・既存依存graph・発行物全SHA／sizeと他1,971fileの保持を照合し、発行結果を別の親receiptへ採用した。各全体実行の前後1,973 source／fixtureと569 runtime fileは不変。全体E2E内の旧reader拒否runは実終了-1で打切った失敗記録として保持し、成功結果に含めない。直近原本は`E:\DiffBeacon-artifacts\local\folder-layout-validation26`、発行物は`artifacts/local/folder-sync-next/folder-layout-native25`、旧Mac原本ZIP／PNG／JSONは`github-latest-handoff20`へ保持する。修正後の4RID・実Mac起動と取消／pipe回収、通常desktop・実OS pointer・ネイティブdialogと完全なShell metadataは残工程。

SHA `92c2b1a6ee15807b97616f7899e216ebcf7f5e3c` の[CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37339611969)はWindows両構成が成功した。Windows x64の公式2ZIPのdigest・31,313全entryのCRC／SHA／size、E2E161,653成功／0失敗／4skip・6,967命令の全stdout／stderr、全UI11,138成功／0失敗、Native発行物・3ライセンス、実OS画像／バイナリclipboardの別writer／readerを照合した。親はフォルダー通常／最小／45タブの6PNGを目視し、一覧と4コピー操作・scroll後のfilter／除外への到達を確認した。証拠は`E:/DiffBeacon-artifacts/local/folder-layout-validation26/github-layout27/win-x64/rid-receipt.json`と`artifacts/local/folder-sync-next/validation26-control/layout27-ci-win-x64-parent-visual-receipt.json`。Mac両構成はそれぞれNative発行・全UI11,111成功／0失敗の後、E2E161,777成功／1失敗／8skipとなった。各7,020命令のStartupGate・実終端・出力回収に異常はなく、失敗は特殊input helperが`plain`文字列をtrueと扱って片側linkの反対側に通常fileを誤生成したことによる。固定のLeftOnly／RightOnlyと全原本保持の期待を維持して引数解析を修正した。同OSの修正後E2Eと最新全4RIDの受理は再検証完了まで未確認。Mac原本は同保存先の`osx-arm64/cause-receipt.json`、`osx-x64/cause-receipt.json`と公式ZIPへ圧縮保持する。修正後のWindows限定E2Eは104成功／0失敗／1skip・全62命令の実終端を確認し、独立readerは39CLI・44GUI・7review・4中止と全本文／metadata、6PNG・15toolbar条件を受理した。Release buildは警告0／エラー0。証拠は`artifacts/local/folder-sync-next/mac-special-input-fix28`とE:の同名限定run。

Windowsのcreation・owner/group/DACL・EA・file identityを取得する新部品は、固定した部品ソースの通常版とWindows x64 Native AOTで6ケース・各12独立readerの実終了0と全DATA／ADS／意味のあるmetadataを照合した。主コピー経路への接続、metadata setterと公開、取得中のpath差替え対策と完全なShell metadata保持は未完了で、この部品実測を製品の転送対応として扱わない。証拠は`artifacts/local/folder-sync-next/windows-metadata-product-capture27/native-run05-handoff.json`と原本SHA付きのsource cohort。

| 旧機能・処理分岐 | 新しい実装の範囲 | 判定・残作業 |
| --- | --- | --- |
| テキストの二者比較・三者マージ | 差分行・行内強調、差分移動、両方向コピー、編集、保存、競合表示、CLI、左・祖先・右・結果の4ペイン、差分単位の順序付き採用・Undo/Redo・未解決状態・行の由来 | [マージ結果セッション](MERGE-SESSION.md)を実装。旧エディターの構文強調、矩形選択、同期点、移動行、全ショートカット、由来の行番号マージン表示・セッション永続化は未完了 |
| フォルダー比較 | 側別kind／filtered／走査状態と階層、内容／SHA／日時、除外表示、Ctrl/Shift複数選択、確認付き左右「すべて／差分」コピー、取消・部分結果と再比較、共通CLI | Windows全file ADS転送・全stream鮮度・共有予算・中止表示を含む通常版とWindows x64 Native AOTの全体検証を前段の記録で確認。通常／最小／45タブの操作到達は確認済み。折り畳み、三者、移動・削除、EFS／ACLなど旧Shell操作の完全な同等性と最新Mac／ARMの実測は未完了。[操作と対応範囲](FOLDER-COPY.md) |
| 表形式 | 原文区間付き解析、raw WordDiff共通文字量・best-pair・三者01/12/20行合わせ、セル編集・Undo/Redo・同セル内前後検索・固定文字範囲・一件/全置換、区切り文字・引用符・引用内改行指定、GUI/CLI/HTMLの共通モデル | [表の操作](TABLE-EDITOR.md)。旧callerのraw CSV入力変換・全WordDiff設定とフィルター座標、raw buffer横断検索・PCRE/Rx互換・矩形文字編集・ヘッダー設定・列条件・同期点・全パーサー分岐は未完了 |
| Hex / バイナリ | バイト比較、ページ単位の16進編集、差分範囲／全体コピー、任意位置の挿入・上書き・削除、直接ニブル／ASCII入力・任意byte選択、Copy／Cut／Paste／FastPaste dialog、repeat／skip・Insert／Overwrite・AsText・endian・ANSI／OEM、共有Undo／Redo、通常／別名保存、内包作業版の保存・復元・レポートなし包装 | 表示・編集は各16 MiB、共有履歴64 MiB／256操作上限。同じオフセットで比較。挿入位置の再整列・旧検索・全旧Hex操作の同等性、Binary HTML／patchは未完了。新clipboardの実OS／Native／全4RIDは後続検証と区別する |
| 画像 | 二者・三者表示、原本領域強調と差分／競合移動、重ね合わせ、倍率・閾値、ピクセル差分、GIF/WebP/APNGのフレーム選択・前後移動・同期移動、TIFF／BigTIFFの主ページ比較、ページ・表示設定のプロジェクト保存、全フレーム／選択組のCLI比較とPNG埋込みHTML、包装レポート、GUI／開発用CLIの静止画領域コピー・共有Undo／Redo・PNG別名保存、回転・反転と手動位置ずらし、行列の挿入削除の表示／コピー／設定保存、編集済み原画のHTML、GUI矩形選択／移動／複製・Copy／Cut／Delete・浮動貼り付け・3辺Resize、差分色の不透明度と設定保存 | [画像の契約](IMAGE-VIEWER.md)。入力・画素・枚数・復号量とHTMLの上限を維持。TIFFの未検証構成と残りの旧画像形式、ベクター、OCR、元形式／多ページ保存、旧全体設定の一括移行は未完了。XOR／Alpha／ANIM・点滅・共通設定と通常CLI／HTML／包装の接続は実装し、最終通常版の全体E2E・UIが成功した。変更後のNative／4RID検証はSHA dc20aaの全構成で成功した（TAR.Z節の実測を参照） |
| Web / XML / HTML / Office | XML正規化、HTML静的本文、HTTP応答のソース・本文、DOCX/PPTX/XLSX本文 | 標準プロバイダーを明示選択。ブラウザーのDOM・JavaScript・画面・リソースツリー、Officeの書式・旧形式・PDF/OCRは未完了。詳細はプロバイダーREADME |
| Archive / プラグイン | 7z/RAR/ZIP/TAR/TAR.GZ/TAR.BZ2/TAR.ZとZIP派生/7z/RARの明示的gz/bz2/Z鎖の内容比較、初期再試行、暗号化ヘッダー/内容・solid読込み、プレビュー・エントリ保存・全件抽出、非暗号化7z/ZIP派生/TAR系作成・再梱包、保存済み比較文書・HTML/patch/projectの包装、Sourceの多段Text／Binary／Archive子比較・片側不在入力・未命名Textの外部保存・実在Textの作業保存・相対asset workspace・leaf HTML／patch包装、実行ファイル用JSON契約 | 旧submoduleの通常ビルド依存は解除。TAR.Zはmanagedで読書きし、通常アプリに外部圧縮ツールは不要。全形式の詳細レポート・一時ZIPのクリップボード包装、深いcontainerの再梱包／全件展開・内包Binaryの編集保存・画像／provider子比較・裸compressed・TAR複数wrapper・wrapper作成、CAB/LZH/ISO等の全旧読込み形式、属性・全日時保存、旧ActiveX/DLL ABIは未完了。7zのCRC省略と値0の区別は現行ライブラリの公開APIでは未確認。変換結果を元ファイルへテキスト保存しない |
| シェル統合 | 通常のデスクトップ起動、CLI、macOS `.app` 生成 | Explorer / Finder 拡張、旧コンテキストメニュー、インストーラー登録は未実装 |
| 多言語 | 新 UI は日本語を中心に実装 | 旧翻訳カタログとローカライズ切替、RTL、全ダイアログの同等性は未完了 |
| 詳細フィルター | 大文字小文字、4種の空白処理、空行、行正規表現、ASCII数字・CStyle/CSharp/Python/XMLコメントの除外、順序付き置換、旧`.flt` include/exclude、名前・拡張子・サイズ・日時の条件式 | 比較前処理は原文を保持し、GUI・CLI・フォルダーへ適用。同梱12 `.flt` の読込みを確認。内容検索、左右別属性、関数・算術、PCRE固有構文、全旧構文のコメント処理、複数行置換、全表示フィルターは未完了 |
| プロジェクト / レポート | source-generated JSONの全タブ保存・復元、複数組`.WinMerge` XML読込み、HTML / JSONレポート、相対JSON参照とGUI起動時プロジェクト読込み、テキスト・表・JSONの二者／三者HTML、画像の二者／三者全／選択HTML | 順序・選択位置、説明・各readonly・再帰・フォルダー方式・除外・表設定を保持し適用。HTMLは祖先アンカー整列・行内差分・表セル・JSON正規化・画像画素を単体GUI／CLI／包装へ適用。旧filterは対応範囲以外を明示拒否。未対応オプション・プラグイン名は保持・警告し実行しない。旧middleの第三比較ペインと祖先マージの厳密な対応、画像の全旧形式とWeb描画レポート、全旧オプション、結果本文・採用状態の永続化は未完了 |
| Native AOT Windows x64 / ARM64 | 対応 RID と同 OS 発行スクリプト、CI マトリクス | 両アーキテクチャのGitHub runnerで発行・UI自己検証・CLI E2E成功。実測結果は下表 |
| Native AOT macOS x64 / ARM64 | 対応 RID、`.app` / tar、CI マトリクス | Intel / Apple SiliconのGitHub runnerで発行・UI自己検証・CLI E2E成功。署名・公証・公開は実施しない |

WinMerge XML の要素と window-type の対応は `Src/ProjectFile.cpp`、`Src/MergeCmdLineInfo.h` を参照した。全`paths`を順に読み、`left` / `middle` / `right`、説明・readonly、`subfolders`、表設定、`ignore-case`、`ignore-blank-lines`、`ignore-numbers`、`ignore-comment-diff`（互換別名`ignore-comments`）、`white-spaces`を適用する。`white-spaces`の旧モード1は空白量の変更を無視、2は全空白を無視として区別する。コメント構文は左・祖先・右の最初の対応拡張子から選択し、未知の形式では除去しない。旧`filter`はファイルフィルターであり、行正規表現へ置き換えない。旧`compare-method`は0を内容、2をSHA-256、4を日時とサイズへ対応し、それ以外は未適用として元値を保持する。HTTP/HTTPS URL・外国OSの絶対パスは保持し、環境変数を展開して相対パスをプロジェクト所在基準で解決する。

プロジェクト保存は原子的に置換し、読取り専用属性・リンク経由を拒否する。プロジェクトのreadonly指定は編集・差分コピー・フォルダーコピー・全保存経路で入力を保護し、読取り専用フォルダーの子への出力も拒否する。バイナリ別名保存とアーカイブのエントリ保存・再梱包にも祖先・反対側を含む保護を接続する。旧JSONの省略項目は既定値を復元し、明示null・不正enumを拒否する。置換ルールは専用converterで省略時のMatchCase/UseRegex/Enabled=trueを維持する。

コピー処理はルート外のパス、`.` / `..`、既存の symlink / junction を拒否し、ファイルを一時ファイルへ書いた後に置き換える。途中のキャンセルでは、既に完了したファイルや作成したディレクトリが残る。検査とファイル操作の間に別プロセスがパスを差し替える敵対的な状況まで防ぐ OS ハンドル単位の保護は未実装。

全機能移行完了の判定には、上の未完了項目の実装と、Windows / macOS の対象アーキテクチャ上で再現できる検証結果が必要。CI 定義の追加だけで、ビルド・起動・配布の成功を確認したことにはならない。

アーカイブの旧互換範囲は実装根拠で区別する。`Src/7zCommon.cpp:391`の作成UIは7z・ZIP派生形式・TAR/TAR.Z/TAR.GZ/TAR.BZ2/TGZ/TBZ2を提供し、RAR/LZH/CAB作成はコメントアウトされている。`Src/ArchiveDlg.cpp:28`は選択文書・レポート・パッチ・プロジェクトを包装し、`ArchiveSupport/Merge7z/Merge7zCommon.cpp:243`は全件抽出、`Src/7zCommon.cpp:480`は多段アーカイブを再判定する。読込み形式は`ArchiveSupport/Merge7z/Merge7zCommon.cpp:721`以降の登録を参照する。暗号化出力・分割出力は調査した旧作成経路に指定がなく、既存機能の移植漏れとは断定しない。分割読込み・MSI・リンク保存の厳密な旧動作は未確定であり、追加実測が必要。

## TAR.Zの読書き

GUIの自動判定・比較・プレビュー・エントリ保存・全件展開・作成・再梱包、通常アーカイブCLI、標準`archive`・`tar`・`tar-metadata`プロバイダーと比較プロジェクトの包装でTAR.Zを扱う。`tar.Z`／`taz`は同じ圧縮TARとして認識し、外部7z DLL・compress実行ファイルを通常アプリから呼び出さない。標準TARプロバイダーはリンクを追わず型・名前・リンク先を比較し、アーカイブの保存／展開サービスはリンクと特殊entryを拒否する。

読込みは9～16bitのblock／nonblock、出力は16bit block形式。固定39原本には幅の変更・辞書満杯・CLEAR、現行block24件、歴史的nonblock10～16bitの14件と独立literal nonblock9を含む。歴史的v4.1のmaxbits9原本2件にはmodern decoderと幅境界が異なる旧自己互換があり、この2件を正常goldenとして使わない。出典・全SHA・CC0入力・public-domain原文・再生成は[TAR.Z fixture](../tests/Fixtures/Archives/TarZ/README.md)へ集約する。ZにはCRC・宣言長・明示EOFがなく、内側TARの構造・終端と作業上限を検査しても、すべての意味的改変や末尾padding欠損を検出できるわけではない。

限定E2Eと全体E2Eは固定原本の全entryを照合し、writer出力を別buildの公式ncompressとPython標準tarfileで独立に復号・照合する。ローカルではfull7zでも全TAR bytesを照合する。通常.NET build・発行アプリに検証用C/compilerを追加しない。最終Release buildは警告・エラー0、通常版とWindows x64 Native AOTのheadless UIは各10081成功・0失敗。最終Native AOT全体E2Eは156652成功・0失敗・3skip、4880命令のstdout／stderrと実終了コードが一致し、起動失敗・timeoutは0。検証中の259入力と実行ファイルのSHAを照合した。標準TARプロバイダーの復号を背景で実行し、復号中と結果採用直前の実中止ボタンで前回本文と入力を保持する。

SHA `dc20aaade17913cf0d4445386c6386f7523c52e1` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37126824667)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37126824669)は成功した。全構成UI10081成功・0失敗。Windows各156652 E2E成功・0失敗・3skip／4880命令、Mac各156661成功・0失敗・2skip／4883命令で、全実exit・stdout／stderrが一致し、起動失敗・timeoutは0。53固定原本のraw bytes、39原本の全entry、12writer出力の公式decoder復号、GUIの復号中／採用直前の取消、Wipeの最新要求2→8と選択3paneの全BGRA、重ね合わせ・不透明度・レイアウト、全発行物と別writer／readerによる実OSクリップボードを独立照合した。8 ZIP・89522entryのCRC・SHA・サイズとGitHub digestを確認し、全体E2E・発行物は展開せず654472724bytesのZIPとして保持する。証拠は`artifacts/github/37126824667/terminal-summary.json`と`artifacts/local/archive-tar-z-integration/corrected-ci-receipt.json`。通常デスクトップの操作とネイティブ保存ダイアログは未検証であり、全旧アーカイブ形式の移植は未完了。

先行SHA `ba7193aacc34190ad20c5cfd4c6e9033d82366d9` の[GitHub検証](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37125361234)では、親のArchives属性がCRLF原本をLFへ変換し、固定manifestのSHA検査が失敗した。修正ではTAR.Z専用の近い属性へ移して継承した`eol`を解除し、期待SHAと原本bytesを維持した。上記の修正後CIで、実Git blob・runner上の固定原本と全実行経路の一致を確認した。

## 原文区間を保持する WordDiff

旧 `stringdiffs.cpp` の単語 O(NP)・区間生成・文字絞込みを managed Core へ移植し、テキスト比較の変更ブロック全体へ接続した。旧ブロック呼出しの連結と行offsetを参照し、正の区間を各行本文へ投影する。離れた変更を GUI・CLI・単体／包装 HTML で共有する。全ブロックで詳細比較予算を共有し、上限時は変更行全体を強調して件数を表示する。

原本4 translation unitを変更せずMSVCで実行した6,048ケースと全UTF-16文字分類を採取した。CRT C / Windows分類を固定して全OSで使用する。実測で発見したタイトルケース・上付き数字の差を修正した。これは元ソース実行との比較であり、旧GUI配布バイナリの全設定・全字素規則との一致ではない。NULファイルは既存TextDocument契約により拒否する。詳細な出典と失敗条件は [WordDiff E2E](../tests/Fixtures/WordDiffs/README.md) を参照する。

表の変更ブロックはraw WordDiffの共通文字量・best-pair・4096本文上限・三者20写像へ接続した。全ブロック共有予算と不変条件による安全なzipを追加し、CLI三者指定と単語/EOL/予算設定を接続した。三者の統合区間でも復号一致アンカーを保持し、引用表記の違いだけで無変更側の行が追加・削除になる回帰を修正した。後続のGNU行算法接続は下記に記載する。旧callerの全設定、フィルター後の座標、全Unicode/パーサー分岐とプロジェクトの追加設定は引き続き未完了。元関数goldenの出典は[表行対応fixture](../tests/Fixtures/LineAlignment/README.md)。

再開後の通常DLL全E2Eは5,778成功・0失敗・8スキップ、表行対応の限定E2Eは1,948成功・0失敗、headless UIは230成功・0失敗。Windows x64 Native AOTは5,809 E2E成功・0失敗・3スキップ、230 UI成功。SDK10.0.401、buildとAOT発行のCS/IL/MSB警告0。独立レビューの成立した三者一致回帰を修正し、実CLI/HTMLとGUIで照合した。入力・対応JSON・PNG・ログは `artifacts/verification/table-line-alignment` に保持する。

この表行対応を含む `118f76461e58f45e4894512c71d9fcd14ed095b5` は [GitHub run 36820491881](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36820491881) の4構成すべてでNative AOT発行・headless UI・E2Eに成功した。[同SHAのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36820491875)も成功。Windows x64/ARM64は各5,809 E2E成功・0失敗・3スキップ、Mac Intel/ARM64は各5,818成功・0失敗・2スキップ。UIは全構成230成功、SDK10.0.401、CS/IL/MSB警告0。先行の `af6ee107d` は両Macで失敗し、ARM64成果物で3goldenの改行変換によるSHA不一致を確認した。採取bytesを保持する `.gitattributes` を追加し、ハッシュ検査を維持したまま解消した。JSON・PNG・ログ・発行物は `artifacts/github/36820491881` に保持する。通常デスクトップ起動・Explorer/Finder操作・署名と公証は実測していない。

ローカル Windows x64 の Native AOT では元ソースの有効5,832ケースを全件実行し、区間・終了コード・入力バイト保持が一致した。孤立surrogate216ケースはUTF-8文書経路の直接照合から除外する。Native CLI E2Eは3,862成功・0失敗・3スキップ、実描画UIは224成功。WindowsのUnix権限・Mac大小文字別名・大小文字を区別する包装入力は対象外として理由を保持した。SDK10.0.401、ビルドとAOTのCS/IL/MSB警告0。補助確認を再利用したsource確認に加え、PNGを目視して一致文字のForeground=nullによる不可視も修正した。通常デスクトップ操作・全旧字素規則・旧配布GUIは未実測。成果物は `artifacts/verification/table-worddiff`、`artifacts/e2e/word-diff-native-full`、`artifacts/verification/win-x64` に保持する。

## 通常テキストへ接続した GNU 行算法

原本の `analyze.c` と `io.c` を変更せず実行して279ケースの入力同値クラス・変更scriptを採取し、Coreへ行算法を移植した。開発用CLI `--gnu-line-script` と実プロセスE2Eで、原本が作った同値クラス配列を入力し277ケースのscript一致を確認した。逆順4,095／4,097行の2ケースは共有8M予算の上限に達するため、全入力を一変更ブロックへ退避する契約を確認する。出典・再生成手順は [GNU行fixture](../tests/Fixtures/GnuLines/README.md)、開発用CLIは [開発手順](DEVELOPMENT.md) を参照する。

通常 `TextDiffer` のpatience/HirschbergをGNU行算法へ置換した。原文の共通端と文書状態を反映した比較キーを照合して本文を切り出し、本文・EOL・最終改行の構造キーを共有同値クラスへ分類する。ハッシュの文字走査・衝突比較・配列確保も行算法と同じ予算へ計上し、上限では未解決本文をまとめて変更扱いにする。共通端と全文同値の線形確認は予算0でも行い、全元行とフィルター前の行番号を保持する。GUIは上限時に行対応の省略を表示し、CLIは使用量・退避理由を返す。

原本default 273ケースの元bytesから通常CLIへ接続し271 script完全一致、逆順4095/4097の2件だけ8M予算退避を確認した。通常GNU限定E2Eは3399成功・0失敗、通常DLLの全体E2Eは12367成功・0失敗・8skip。独立レビューでP1/P2なし、新しい文脈の120追加入力も原本script・元行順序・本文・span境界など1442検証すべて成功。headless UIは233成功・0失敗で、反復行の原本対応と4097行の全行保持・上限表示を確認した。SDK10.0.401、最終Rebuildの警告0。成果物は `artifacts/verification/gnu-text` と `artifacts/verification/gnu-review`。最初の限定実行は更新前の検証器が従来の全体検証を走ったため採用せず、全solution Rebuild後に対象ケース選択とコード／程序集SHAを照合して再実行した。

Windows x64 Native AOTは12398 E2E成功・0失敗・3skip、headless UI233成功・0失敗、コンパイラーのCS/IL/MSB警告0。リンク拒否の経路も管理者実行で検証し、実行主体・アプリSHA・Python実体・終了コードを保存した。通常DLLの8skipと区別する。表の初期行対応への後続接続は下記に記載する。旧callerのUTF-8一時文書・引用内改行escape・全Unicodeとフィルター設定の厳密な互換は残る。この通常テキストGNU追加分は `5ea416e1778d3a6b8713c43bdb074c6a054e99df` の [GitHub run 36845332993](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36845332993) で全4構成に成功し、[同SHAのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36845332985)も成功した。Windows各12398 E2E成功・0失敗・3skip、Mac各12407成功・0失敗・2skip、headless UIは全構成233成功、SDK10.0.401、CS/IL/MSB警告0。表のGNU接続はこのrunに含まれない。

## 復号セル行へ接続した GNU 行算法

表の初期patienceアンカーをGNU行算法へ置換した。比較設定を適用した復号セル値と外側EOLを構造キーにし、引用表記だけの差は同値として扱う。祖先との全体一致組を保持し、右→左は各統合変更区間の範囲だけを比較する。GNU分類・算法と後段のraw WordDiff・投影・採点・三者合成で同じ予算を消費し、最初の退避理由と使用量を返す。旧callerのraw CSV一時文書・引用内改行escape・全フィルター座標との完全互換は未確認。

原本A/B・LFの225ケースから各変更が片側だけの143ケースを選び、全元bytes・SHA・scriptを保持して通常表CLIとHTMLを照合し、143件すべて一致した。両側変更のraw採点はこのscriptから推定せず、既存88件の別oracleを維持する。GNU表限定E2Eは2952成功・0失敗、既存行対応限定E2Eは1948成功・0失敗、通常DLL全体E2Eは15318成功・0失敗・8skip。headless UIは235成功・0失敗で、反復行のGNU対応とHTMLの元行を確認した。Release buildは警告0・エラー0。初回は検証器が原本A/Bを小文字a/bと誤認し入力確認256項目だけ失敗したが、元fixtureと期待mapを維持して修正した。初回と修正後の成果物を別々に保持する。

新しい文脈の独立レビューは82ケース・84CLIプロセスを実行しP1/P2なし。非ゼロで異なる開始座標を持つ三者区間の明示期待値とHTML、GNU後の投影や後続blockでの予算退避、引用表記だけ違う無変更側一致も成功。取消はこの独立レビューでは静的確認のみ。成果物は `artifacts/verification/gnu-table` と `artifacts/verification/gnu-table-review`。Windows x64 Native AOTは15349 E2E成功・0失敗・3skip、235 UI成功、CS/IL/MSB警告0。管理者実行・Python実体・アプリSHA・未コミット状態とソース指紋を保存した。この表接続を含む `cbb100bab6aca1bdee42794793c5c40cfcb5a32f` の [GitHub run 36848623334](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36848623334) は全4構成で成功し、[同SHAのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36848623256)も成功した。Windows各15349 E2E成功・0失敗・3skip、Mac各15358成功・0失敗・2skip、headless UIは全構成235成功、SDK10.0.401、CS/IL/MSB警告0。全8成果物を取得し、発行manifestの64ファイルのサイズとSHA-256は全件一致した。成果物と検証器を `artifacts/github/36848623334` に保持する。
## 画像のドラッグ操作

縦／横ワイプを追加した通常版はReleaseビルド警告0・エラー0、限定E2E1,506成功・0失敗・0skip、全headless UI9,697成功・0失敗、独立Python PNG133成功・0失敗。原本40ケース400状態、読取り専用二／三者の実pointer、TIFFページの境界縮小、取消・古い候補・失敗・capturelost、押下中に内部保存APIへ捕捉した単体／包装HTMLの選択強調と原画PNGを照合した。通常のrelease後GUI保存は原本と同じくワイプ解除後の表示であり、この内部API検証を通常保存入口の実証とは扱わない。選択強調が包装HTMLで通常色へ戻る実不具合は、entry別の一時表示snapshotで修正した。独立レビューで見つかった成功再描画による最新要求の喪失と、領域診断の45,068,506bytes出力は修正前に実再現し、最新要求保持と32MiB超過時の終了2・stdout空へ修正した。追跡レビューに未解決指摘はない。証拠は`artifacts/local/image-wipe-product/final-summary.json`。最終通常版の全体E2Eは151,642成功・0失敗・10skip、4,333コマンドの実exit・stdout／stderrが一致、timeout0。Windows x64 Native AOTも発行・UI9,697成功、全体E2E151,687成功・0失敗・3skip、4,348コマンドの実exit・全出力が一致、timeout0。固定333ソース・実行DLL／Native EXEの前後SHA、発行12ファイルのSHA・サイズ、原本alpha146状態316面・316PNGとレイアウトを照合した。Native自己検証は約95秒で完走した。自動restoreでCore／Providersの2lockはRID情報を含む既存HEADの内容へ戻り、4lockすべてのHEAD正規化一致と発行後維持を別記録した。証拠は`artifacts/local/image-wipe-integration/managed-final-proof.json`と`artifacts/local/image-wipe-native/final/final-summary.json`。この変更の4RID CIは次段の固定SHA 3b6f41bで完走し、下記の旧4モードSHAの結果と区別する。

ワイプを含むSHA `3b6f41b75e2eff6abc24f14c5fc2a4308b5867c4` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37102142914)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37102142897)は成功した。全構成UI9,697成功・0失敗。Windows各151,687 E2E成功・0失敗・3skip／4,348コマンド、Mac各151,696成功・0失敗・2skip／4,351コマンドで、実exit・stdout／stderr全一致、timeout0。各構成のワイプ40ケース400状態、None6ケース、GUI133 PNG（解除後baseline40を含む）と50面全BGRA、alpha原本146状態316面・316 PNG、レイアウト、実OSクリップボードの別writer／readerを照合した。Mac自己検証はARM64約195秒、x64約185秒で600秒枠内に完走し、最後の表検索と原子的な最終進捗を確認した。発行manifest・Mac tar・ライセンス、8 ZIP・81,426entryのCRC／SHA／サイズを照合し、全面展開せず約642MBで保持する。証拠は`artifacts/github/37102142914/terminal-summary.json`。これはワイプの固定コミット検証であり、その後の未コミットoverlay核のソース／DLLを証拠には使っていない。通常デスクトップのOS pointer capture・ネイティブ保存ダイアログは未検証。

原本WinIMerge v1.0.54の6値（NONE=0、MOVE=1、OFFSET=2、VERTICAL_WIPE=3、HORIZONTAL_WIPE=4、RECTANGLE_SELECT=5）のうち、左右／三者表示で全6値を移植した。既定はMOVEで、共有アプリ設定として全画像タブへ保存・反映する。倍率・位置等の既存プロジェクト設定とは独立する。支持矩形の負座標preview、ゼロ方向切捨てのrelease差、全paneの位置正規化、readonly・多ページ、既存矩形／浮動／Resize優先、Escape中のoffset capture保持、原画・Undo履歴と位置の分離を維持する。表示移動・thumbは絶対位置、wheel・line/pageは各paneへのdeltaとしてSideBySideの範囲内で同期する。

ワイプ画素核は固定原本C++の40ケース400状態・全BGRAを2回採取し、独立検証1,442件が成功した。[wipe fixture](../tests/Fixtures/ImageWipes/README.md)は約38KBのgzipで保持する。静的重ね合わせは無改変 `RefreshImages` の49ケース・全BGRAを2回採取し、独立検証1,483件が成功した。[overlay fixture](../tests/Fixtures/ImageOverlays/README.md)は約17KBのgzipで保持する。ワイプはApp・GUI・CLI・HTMLへ接続した。この原本採取時点では静的重ね合わせのNone原本6件を製品経路へ照合し、Alpha／XORの残り43件を後段の原本互換移植に保持した。時刻依存の[overlay fixture](../tests/Fixtures/ImageTemporalOverlays/README.md)は77ケース83状態・全BGRAを2回採取し、親の独立検証も1,459件成功した。ANIMのblendごとの時刻読取り・中央の中間byte切捨て・blink／highlight／wipe順を保存する。動的animation／blinkの製品接続は後続のPhase3で確認した。原本OS操作の実測は未完了。

その後のoverlay Stage1では、None／XOR／Alpha／ANIMとblinkの純画素核を開発用`--image-overlay-script`へ接続した。入力は原画・設定・個別epoch queueだけで期待値を渡さず、静的49ケース49状態と時刻依存77ケース83状態を照合する。限定E2Eは3,587成功・0失敗、親の独立照合は9,641成功・1,303frame全BGRA一致。新しい文脈のレビューも独立96ケース・870項目が成功し、成立した指摘はない。固定337ソース・4lock・App／E2E DLLの前後SHAを照合した全体通常E2Eは155,228成功・0失敗・10skip／4,390コマンド、timeout0。既存UI9,697成功・0失敗と独立PNG133件も維持する。診断内の8MiB入力・32MiB全出力・256M共有作業量、予算境界・取消・後半不正入力とstdout空を確認した。初回のhash大小表記・pair gridの符号差は全画素差0と区別して是正し、失敗入力と出力も保持する。証拠は`artifacts/local/image-overlay-product/stage1-summary.json`と`artifacts/local/image-overlay-integration/stage1-final-proof.json`。この段階の新overlayは通常GUI・設定・HTML・包装・timerへ未接続で、Native AOT／変更後4RIDは未実行。上記3b6のWipe CIとは別の未コミット検証である。

overlay Phase2では通常GUIへNone／XOR／Alpha／ANIM・差分点滅と共有設定を接続し、Release警告0・エラー0、全体headless UI9,893成功・0失敗（新overlay192項目）、限定画素核3,587成功・0失敗を確認した。親の独立照合は原本1,303frameの全BGRAとPNG181枚が一致し、検証時の135 C#ソース・5lock・2原本と実行物を含む252行のSHAも一致した。通常／最小ウィンドウ・操作欄64／80pxの36条件で実controlの到達を確認し、周期の4桁表示を修正した。4000×4000の三者Noneは既存描画64Mで受理し、Alpha追加描画256,001,287は時計読取り前に拒否して確定表示を保持する。完了済み8出力rootの9,944ファイルを全SHA・サイズ照合し、約436MBから74MBへ圧縮保持した。証拠は`artifacts/local/image-overlay-integration/parent-phase2-summary.json`と`artifacts/retained/image-overlay-integration/phase2`。この断面の全体E2E・Native AOT・HTML／包装接続は未実行である。続くPhase3で、設定取消後の候補採用と、offset／rotation更新後の描画失敗時に確定overlayが失われる2種類の不具合を実headlessで再現した。取消1ケースとoffset／rotation失敗2ケースをAlpha／ANIMでそれぞれ観測し、全6ケースの前後PNG36枚の全BGRAも独立照合した。修正とHTML／包装／通常CLIへの接続は進行中で、移植完了とは判定しない。

Phase3ではframe／overlayを同時採用し、取消／失敗時にも確定表示を保持する。選択組のtyped capture、全ページのtuple別時刻採取と選択clamp継承、全画像entryの包装SHA照合、保存後のsnapshot更新、通常CLIへ接続した。独立レビューで指摘された再比較／設定復元時の不透明度scopeと点滅checkboxの不一致は、変更前の実UIで再現して修正し、最終6観測すべてでcontrolと採用設定の一致を確認した。最終Releaseは警告0・エラー0、全体headless UI10,059成功・0失敗、全体通常E2E155,760成功・0失敗・10skip、4,502実コマンド・timeout0。親の独立照合で表示／report170・既存wipe133・前後36の計339 PNG、旧6故障の表示保持、保存後包装5entryと原画／表示6 PNGが一致した。通常CLIの原本HTML30ケース150 PNG・領域25ケース63 BGRAと、全体E2Eのstdout／stderr9,004本も一致した。ソース・実行DLL142行の検証前後SHA、runtime495ファイル、原本／canonical14ファイルと5lockを凍結して照合済み。証拠は`artifacts/local/image-overlay-product/phase3-summary.json`、`phase3-freeze-source-proof.json`と`artifacts/local/image-overlay-integration/parent-phase3-final-full-cli.json`、各PNG proof。10skipはWindowsのUnix／Mac／case alias制約とsymlink作成権限による。Nativeの実測は次段の記録を参照する。通常OS pointer・ネイティブ保存ダイアログは未検証であり、通常版の成功から推定しない。

同じ最終ソースのWindows x64 Native AOTは発行警告0・エラー0、headless UI10,059成功・0失敗、全体E2E155,805成功・0失敗・3skip、4,517実コマンド・timeout0で完走した。既存開発シェルからの発行と管理者プロセスを確認し、通常版で権限不足だったsymlink保護も成功した。残る3skipはUnixFileMode、Macの実APFS、case collisionのホスト制約である。発行manifestの全12ファイルとmanifest自身、171ビルド入力の前後SHA、原本1,303frame・31,056 BGRA bytes、GUI339 PNGとscope6観測、包装5entry、通常CLIの150 PNG・63 BGRA、実stdout／stderr9,034本を独立照合した。自己検証は約113秒、全体E2Eは約218秒。証拠は`artifacts/local/image-overlay-integration/native-final-summary.json`と各proof。固定commit `21824be` の[GitHub run 37118662889](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37118662889)ではWin x64／ARM64とMac ARM64が成功し、[同commitのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37118662810)も成功した。Mac Intelはワイプの選択変更後の描画検証で147.6秒・終了2となり、全体E2E・OS clipboardは未実行。失敗した複合条件の各実値は当時記録していない。旧要求だけの完了を待てる経路をWindowsで二段の候補gateにより再現し、旧要求完了・位置2・選択0・captureあり、別の最新要求が未完了というJSONを保持した。自己検証を最新の`CurrentWipeOperation`の完了まで待つよう修正し、待機中の表示保持と完了後の位置8・全BGRAを検査する。修正後のMac IntelはSHA dc20aaのCIでUI10081成功・0失敗となり、最新要求2→8と選択3paneの全BGRAも一致した（TAR.Z節の実測を参照）。成功3RIDの発行物・全命令・BGRA／PNG・別writer／reader OS clipboardと、失敗RIDの例外・直前PNGは`artifacts/github/37118662889`へ独立照合して保持した。実OSクリップボード、通常デスクトップのpointerとネイティブ保存ダイアログはローカルで未実行。

ワイプ単独移植時点では、縦／横ワイプはUIで選択し共有設定へ保存・読込みできる。重ね合わせと画素差表示では具体的利用条件を表示して操作モード欄を無効にし、確定modeは保持する。ワイプの一時表示と選択強調を保存開始時のsnapshotとして単体／全ページ／包装HTMLへ反映し、原画と分類・履歴は保持する。共有256M予算へ複製と入替えの作業を加算し、多ページは描画前に累積上限を検査する。両表示のドラッグ／同期、原本overlayとの同等性、旧全体設定の一括移行は後段の残作業である。原本の通常PNG保存はraw原画、レポートPNGはactive wipeを含みガイドを含まないため、プロジェクトの`imageSettings`へ一時wipe状態を混ぜない。

実アプリの`HeadlessImageDragChecks`はpointer/key・2/3paneのoffset preview/release、モード変更、取消境界、readonly TIFF、同期viewport、設定失敗をPNG/NDJSON/JSONへ保存する。固定offset原本12ケース43状態は既存`HeadlessImageOffsetChecks`と限定／全体E2Eで引き続き照合する。通常デスクトップのOS pointer capture・設定ディレクトリへの実保存と、headlessの実イベント／fixtureパス検証は区別する。最新検証の成否は親が保存するphase evidenceを正本とし、本節では未実行のNative/4RID検証を成功扱いにしない。

今回の4モード・入力範囲・同期スクロールと画像HTMLの予算事前確認を含む通常版は、Releaseビルド警告0・エラー0、headless UI9,416成功・0失敗。ドラッグ121件に、旧レビュー回帰36件、画像外余白の押下・確定8件、共有メニュー3件を含む。先の3指摘と追加2指摘を実操作で再現して修正し、独立レビューに未解決指摘はない。証拠は `artifacts/local/image-drag-modes/blank-viewport/final-boundaries` と `postfix-review/independent-final-review.json`。通常版の全体E2Eは150,137成功・0失敗・10skip、終了0で、固定ソース320ファイルと実行DLLのSHAが実行前後で一致した。証拠は `artifacts/local/image-drag-integration/managed-full-proof.json`。Windows x64 Native AOTもUI9,416成功・0失敗、全体E2E150,182成功・0失敗・3skip、発行／検証の終了0。固定ソース・実行ファイル・harnessの前後SHAと4,316コマンドの実exit・全stdout／stderrを独立照合した。発行物全12ファイル、alpha原本146状態316面の全BGRA・316 PNG・9条件のレイアウトも一致。全ページHTMLの上限拒否は53ms、包装拒否65msで、入力・既存出力を保持する。証拠は `artifacts/local/image-drag-integration/native/native-full-independent-proof.json` と `native-final-proof.json`。この変更SHA `283e8959febd90a98ff38391a62d3df9398f3feb` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37096525522)は、Windows両構成とMac x64が成功し、Mac ARM64の自己検証180秒timeoutによりrun全体は失敗した。[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37096525516)は成功。成功3構成はUI各9,416成功・0失敗、全体E2EはWindows各150,182成功・0失敗・3skip、Mac x64は150,191成功・0失敗・2skip。全コマンドの実exit・stdout/stderr、発行物全SHA・サイズ、alpha原本146状態316面・316 PNG、9条件のレイアウト、実OSクリップボードの別writer/readerを照合した。Mac x64の以前の全ページHTML拒否timeoutは59ms・exit2で解消した。Mac ARM64は画像ドラッグを通過し最後の表検索の入力生成まで到達したが、最終UI集計はなく全体E2E・OSクリップボードは未実行。実停止命令は未観測で、前回の同構成の自己検証周辺も約178秒だった。検証全体の時間枠と進捗観測を変更し、次の変更SHAで完走を確認する。証拠は `artifacts/github/37096525522/terminal-summary.json` と `failure-osx-arm64.json`。全7 ZIP・61,520entryのSHA・サイズを照合し、約472MBで保持、全面展開はしない。通常OSのpointer capture・ネイティブ保存ダイアログはheadlessと区別する。

## 画像のフレーム比較

先頭だけを表示していた画像ビューを、GIF/WebPの選択フレーム比較へ接続した。左右それぞれの番号指定・前後移動、最大現在番号を起点に短い側を末尾へ留める同期移動を追加した。GUIと新しい `--image` CLIは同じ画素復号・閾値判定を使い、CLI既定では全同番号フレームと枚数差、明示指定では選択した左右の組だけを比較する。入力スナップショット、上限、原文保持、取消時の旧表示保持と古い完了の破棄を維持する。操作と上限は [画像の契約](IMAGE-VIEWER.md) を参照する。

自作の26画像・30期待フレームは入力SHA-256と全画素の独立期待値を保持する。70ケースの画像限定E2Eは500項目成功・0失敗で、静止PNG、GIFの前フレーム合成・背景復元・前状態復元・透明更新、lossless WebP、後続だけの差分、枚数差、閾値・alpha・寸法、入力・枚数・画素・比較キャンバス・復号量の各上限と引数拒否を確認した。新しい文脈の独立レビューは31プロセスのうち20固定期待ケースを全件成功、残る11件は切詰め入力の観測として記録し、未解決P1/P2なし。WebPは別のPillow経路でも固定RGBA期待値とCLIの全画素SHAを照合した。Pillowは再生成・独立検証用で、アプリやCIの依存へ追加しない。

通常DLL全体E2Eは15817成功・0失敗・8skip、Windows x64 Native AOT全体E2Eは15848成功・0失敗・3skip、headless UIは247成功・0失敗。初回のUIで枚数が異なる同期移動を拒否していた問題を修正し、同じ入力の成功を確認した。その後、PNGで窮屈だった番号・閾値入力欄を90から140へ広げ、通常DLL全体E2Eは同じ15817成功・0失敗・8skip、Windows x64 Native AOT全体は15848成功・0失敗・3skip、UIは両方247成功・0失敗を再確認した。最終Release buildとAOT発行のCS/IL/MSB警告0、管理者実行・Python実体・アプリSHA・未コミット状態とソース指紋も保存した。コード・入力・出力・終了コード・ログ・JSON・PNGを `artifacts/verification/image-frames`、独立レビューを `artifacts/verification/image-frames-review` に保持する。

フレーム対応のSHA `4f47ba714ba0af49e2e216efd6b282aa38ec0dc1` は [GitHub run 36854029148](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36854029148) と [CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36854029156) が成功した。Windows両構成は15848成功・0失敗・3skip、Mac両構成は15857成功・0失敗・2skip、4構成とも247 UI成功・SDK10.0.401・コンパイラー警告0。取得した8成果物のmanifest列挙64ファイルのSHA・サイズはすべて一致した。証拠は `E:/DiffBeacon-artifacts/github/36854029148`。Cドライブ容量不足による途中取得とMac x64の部分ファイル復旧は、移動記録 `artifacts/verification/image-frames/ci-artifact-relocation.json` と取得先の `osx-x64-recovery-ledger.json` に保持する。このSHAには後述の画像HTMLは含まれない。

デコーダーの成功は完全な画素の取得を示し、コンテナー全体の構造検証とは区別する。GIFの末尾やPNGのIEND等が欠けても成功する実測を記録した。この段階ではAPNGが未移植だったが、下記の追加実装で対応した。この段階での通常デスクトップのネイティブ操作、TIFFの全ページ、全WebP設定、色管理・EXIF方向、ページ設定の保存は未検証または未移植。画像マージとHTMLの追加範囲は後述する。

入力欄調整後の初回AOT自己検証は画像項目の後、アーカイブ展開の一時ディレクトリ移動でアクセス拒否になった。親ACLには実行ユーザーのFullControlがあり、同じnative実行ファイルで別出力への247項目と、その後の同じ発行スクリプトの247項目は成功した。原因は未確定で、失敗結果と再実行記録を保持し、恒久的な解消とは扱わない。

## APNGの合成フレーム比較

静止画の先頭だけへ退避していたAPNGを、合成済みの各フレームのGUI・CLI・単体／包装HTMLへ接続した。App内のmanaged処理が制御チャンク・CRC・sequence・位置・枚数を検証し、個別PNGの復号には既存Skia経路を使う。SOURCE/OVERとNONE/BACKGROUND/PREVIOUS、アニメーションから除外された既定画像、透明SOURCEの色成分を扱い、共通の入力・画素・復号量・キャンセル予算を維持する。先頭fcTLがacTLより前にある有効入力を拒否する不具合も実アプリで再現して修正した。[fixture](../tests/Fixtures/Images/Apng/README.md)の全原画期待値はデコーダーから逆算しない。

ローカルWindows x64では限定E2E836成功、全体managed E2E27177成功・0失敗・10skip、Native AOT全体27222成功・0失敗・3skip、headless UIは双方927成功・0失敗。GUI原画40件はmanaged/native全bytes一致し、3枚のAPNG画面も目視した。Release buildとAOT発行は警告・エラー0、96ソースファイルと発行manifest10件のSHA・サイズも一致した。証拠は `E:/DiffBeacon-artifacts/local/apng-port/final-order-verification.json` と入力・ログ・JSON・PNGに保持する。

APNG最終SHA `892f362d1327e85268a9486991ef302b84a27edc` の[四RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36912489623)はWindows両構成とMac ARM64が成功し、Intel Macだけ発行後のUI自己検証が60秒で打ち切られた。Intel MacでもAPNGの全40 raw BGRA・期待値manifest・3画面を取得できたが、後続の表検証途中で終了し、全UIと全体E2Eの成功には数えない。[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36912489412)は成功。発行時の自己検証上限を180秒へ変更した後のIntel Mac再検証は未完了。実測fixtureは8bit RGBAであり、palette・16bit・Adam7・ICC・EXIFの対応をこの結果から推定しない。遅延・繰返しの再生、複数フレーム編集、元APNG保存、ページ設定の永続化は未完了。通常デスクトップ起動とOS固有ダイアログもheadless UIとは区別する。

## TIFFの主ページ比較

先頭だけを扱っていたTIFFを、classic TIFF／BigTIFF・両byte orderの主IFDごとのGUI・CLI・単体／包装HTMLへ接続した。ページごとの寸法とstrip／tileの展開予算を保持し、全主IFDの参照範囲・循環・巨大宣言を復号前に拒否する。AppだけにTiffLibrary／JpegLibraryとMITライセンスを追加し、旧C++／FreeImage／submoduleへの実行時依存を増やさない。

[自作31入力](../tests/Fixtures/Images/Tiff/README.md)は正常17・拒否14。正常入力のBGRAはliteral期待値であり、デコーダーから逆算しない。managed限定E2Eは1079成功・0失敗・0skip、headless UIは1089成功・0失敗（TIFF162項目）。ページ寸法・全画素、圧縮と色の代表構成、全／選択・三者・短い側の反復、HTML原画と包装展開、取消・古い完了・入力と既存出力の保持を照合した。scanのないJPEGが旧実装で成功していた不具合を実CLIで再現し、拒否へ修正した。旧JPEGのtable数と参照範囲も確保前に検査する。証拠は `E:/DiffBeacon-artifacts/local/tiff-port/managed-limited-rebuilt` と `managed-ui-final`。

ローカルWindows x64の全体managed E2Eは28255成功・0失敗・10skip、Native AOTは28300成功・0失敗・3skip、headless UIは双方1089成功・0失敗。TIFF原画46件とobservationsはmanaged／nativeで全bytes一致し、3画面も確認した。Release buildとAOT発行は警告・エラー0、発行manifest12件のSHA・サイズも一致。Native全体E2Eは管理者実行とPython指定を記録する。証拠は `E:/DiffBeacon-artifacts/local/tiff-port`。四RID CIとIntel Macの180秒上限は未検証。正常旧JPEG6、全JPEG entropy、LZW dictionary幅切替／predictor、planar、SubIFD、色管理と方向補正、旧FreeImageの全構成との互換性は未検証。複数ページ編集・元TIFF保存・旧全体設定の永続化も未完了。詳細な上限と対応構成は[画像の契約](IMAGE-VIEWER.md)へ集約する。

## 画像プロジェクトの表示設定

画像プロジェクトを開き直すと先頭・閾値0へ戻り、CLI project-copyで設定が消失する経路を修正した。新しいJSONの`imageSettings`がページ、閾値、倍率、重ね合わせ不透明度、強調、全／選択レポートと表示方式を保持する。GUIで復号を完了した状態を保存し、CLI・包装レポートも同じ設定を使う。既存の設定省略プロジェクトは従来の既定値を維持し、CLIの明示指定を優先する。包装の相対参照と保存設定を展開・再読込み後も保持する。

変更前の実CLIでは設定が消失し、選択組のHTMLが2ページになった。追加の限定E2Eは614成功・0失敗。最終Release buildは警告0・エラー0、全体E2Eはmanaged 28868成功・0失敗・10skip、Windows x64 Native AOT 28913成功・0失敗・3skip、headless UIは両方式1103成功・0失敗。2／3入力の全値・既定値・境界・CLI上書き、独立PNG復号による全BGRA、包装展開、不正設定・実ページ範囲外の拒否と入力／既存出力の属性保持、再比較・workspace復元・取消・古い完了を確認した。記録は `E:/DiffBeacon-artifacts/local/image-project-settings`。差分閾値360.62445840513925がGUIで丸められる不具合も実測して修正し、ページ切替時と最小正double値を含め判定値の保持を確認した。発行manifest12ファイルのサイズ・SHAと実行中の製品ソース857ファイルを照合した。全体E2Eの証拠は全entry SHA照合済みZIPとして保持し、GUIのJSON・PNGと再現記録は展開した状態で残す。この設定変更の四RID CIは未実行。

旧`ImgMergeFrm.cpp`のLoadOptions／SaveOptionsは倍率・閾値等をOptionsMgrの全体設定へ保存する。今回のJSONプロジェクト保存は、その全体設定の移行完了を意味しない。画像ドラッグの4モードだけは独立した共有アプリ設定へ追加したが、旧全体設定の一括移行と全表示オプション、未保存原画・Undo履歴・選択領域のセッション保存は未完了。利用手順と上限は[画像の契約](IMAGE-VIEWER.md)を参照する。

## 画像の回転・反転

GUI・通常CLI・HTML・包装へ表示変換を接続する。水平反転・垂直反転・反時計回り回転の順と、原画保存・共有履歴を保つ。静止画のコピーは表示座標で行い、確定した画素を逆写像して原画へ戻す。回転だけではdirty・Undoを変更せず、読取り専用画像と複数ページの表示にも適用できる。プロジェクトには各入力の角度・水平／垂直反転・ブロックサイズを保存する。位置合わせ・挿入削除・矩形編集・多ページ編集・元形式保存は未完了。

WinIMerge v1.0.54の公式DLLから32表示ケース・256コピーケース、計2,816状態を採取した。alpha0のhidden RGBも含む全BGRA、寸法、領域、共有Undo／Redo、保存点とPNG原画を固定期待値とし、実アプリ別プロセスへ照合する。GUIは代表14操作列と実ボタン・ページ切替・取消・古い完了を操作する。強調画面の共通canvasへ追加する透明paddingと、PNG保存・HTML原画の寸法は別に照合する。正本・SHA・再生成は[変換fixture](../tests/Fixtures/ImageTransforms/README.md)、操作は[画像の契約](IMAGE-VIEWER.md#回転反転)を参照する。最終Release buildとWindows x64 Native AOT発行は警告0・エラー0。限定変換E2Eは46391成功・0失敗・0skip、設定限定は614成功・0失敗。全体は通常版75258成功・0失敗・10skip、Native版75303成功・0失敗・3skip、headless UIは両版2429成功・0失敗。代表14ケースの124状態・346画面画素、各28原画PNGとGUI HTML4PNGを独立Python復号へ照合し、両版の観測JSONは全bytes一致した。発行12ファイルのSHA／サイズと248ソースの不変を確認し、画面PNG各4枚も目視した。証拠は `E:/DiffBeacon-artifacts/local/image-transforms/final-local-verification.json` と容量索引の照合済みZIP。旧935状態の比較では新しい表示設定だけをidentityと検査し、元の全property照合を維持する。通常desktopとOSの保存ダイアログ、offset、矩形編集、多ページ編集はこの検証に含めない。今回の変更のMac・Windows ARM64はGitHubで検証する。

画像設定の前段SHA `6d21a11607224d6e49bba717f3f8148e7610b533` は[4 RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36925383370)と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36925383199)が成功。Windows E2E各28913成功・0失敗・3skip、Mac各28922成功・0失敗・2skip、headless UI各1103成功・0失敗。Native76ファイル・Mac tar26ファイルのSHA／サイズ、MITライセンス12コピーを取得後に照合した。これは今回の回転・反転変更を含まない前段の検証であり、通常desktop・OS clipboardの実測とも区別する。

## 原本の画像差分領域処理

WinIMerge v1.0.54の原本関数を無改変で採取し、二／三者のblock比較・8近傍の連結領域・行順ID・block矩形・LeftOnly/MiddleOnly/RightOnly/Conflict分類をC#へ移植した。`ImageRegionDiffer` と開発用 `--image-regions` が対象。固定164件のraw BGRAをPNGへ包装し、実アプリの復号から全pair grid・領域ID・矩形・分類・個数を原本と完全一致照合する。通常buildへC++、FreeImage、submoduleを追加しない。原本・GPL・入力・期待値・SHA・再生成手順は[fixture](../tests/Fixtures/ImageRegions/README.md)、診断コマンドは[画像の契約](IMAGE-VIEWER.md#原本の差分領域処理の照合)に集約する。

原本はBGRAユークリッド距離と01∨21候補を用いる。後続の通常GUI・`--image`・HTMLへの接続は次節に記載する。位置合わせ、変換、挿入削除、ベクター、OCR、画像コピー／保存・マージと全旧復号形式は引き続き未完了。取消チェックは存在するが、OSシグナルによるこの核の中断は未実測。

原本採取と再採取は164件・bytes一致・523検証成功。通常DLLの核限定E2Eは1902成功・0失敗・0skip、全体E2Eは19029成功・0失敗・9skip。Windows x64 Native AOT全体E2Eは19069成功・0失敗・3skip、headless UIは260成功・0失敗、Release／AOTコンパイラー警告0。Native版は管理者実行でリンク拒否も確認し、入力・出力・終了コード・原本全grid・ソース／程序集／発行物のSHAを `artifacts/verification/image-regions/Verify-LocalEvidence.ps1` の96項目で照合した。採取証拠は `artifacts/verification/image-regions-reference`。以前の原本調査担当による追加読取り確認では成立P1/P2なし、新しい文脈の独立レビューとは区別する。この核の `5eae173c2817632e22468b50775f25d7ce148d12` は [GitHub run 36865234830](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36865234830) と [CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36865234885) に成功した。Windows両構成は19069成功・0失敗・3skip、Mac両構成は19078成功・0失敗・2skip、UIは全構成260成功。8成果物のmanifest64ファイルのSHA・サイズが全一致、SDK10.0.401・コンパイラー警告0。証拠は `E:/DiffBeacon-artifacts/github/36865234830` と `artifacts/verification/image-regions/ci-36865234830-summary.json`。旧maintenance 459aのwin-x64自己検証60秒timeoutは新runで再発していないが、原因・恒久対策は未確定。

## 通常画像へ接続した原本強調と三者分類

原本MarkDiff/GetDiffColorFromPositionをC#へ移植し、二者・三者の通常GUI、`--image`、単体HTML、包装HTMLへ同じ領域分類と強調を接続した。中央入力は保存形式の `BasePath` を第三画像へマッピングし、祖先として扱わない。最大成分差をBGRAユークリッド距離へ置換し、同期移動は対象ページがない入力の直前選択を保持する。全ページCLI／HTMLは短い入力の最後のページを繰り返し、枚数差を別に検出する。元画素は共通canvasへ複製して保持し、強調・選択色・透明度・paneごとのonly除外を原本どおり計算する。通常の全ページ描画量にも256M上限を設け、細長い入力同士の共通canvas増幅を拒否する。

無改変原本から72件の強調BGRAを採取・再採取し、bytes一致・745検証成功。golden SHAは `853A08102656CD1F726E39647D98AF47CF4C8918C7F3EB051D078FEA87BC057A`。[強調fixture](../tests/Fixtures/ImageHighlight/README.md)が出典・ライセンス・限定範囲を記録する。新しい依存パッケージ、C++、FreeImage、submoduleは通常buildへ追加していない。

ローカルRelease buildは警告0。原本強調限定E2Eは2956成功・0失敗、旧画像HTMLの原画／左右mask検証は1325成功・0失敗・リンク1skip、描画予算を含むフレーム限定は517成功・0失敗。headless UIは469成功・0失敗、alpha0.7の24件を実Bitmapの全BGRAへ照合し、領域・競合移動、強調解除、HTML、三者の個別／同期選択・取消・古い完了破棄、原本保持を確認した。PNGの目視で三者の選択色とページ位置を確認した。最初のUI失敗は期待SHAが大文字、HTMLが小文字なのに大小文字を区別した検証器の不備であり、全画素照合は通っていた。検証器を修正して再実行し、初回証拠も保持した。成果物は `artifacts/verification/image-three-way`。通常DLLの全体E2Eは22007成功・0失敗・9skip。Windows x64 Native AOTは22047 E2E成功・0失敗・3skip、469 UI成功・0失敗、コンパイラー警告0。Native実行は管理者・Python3.14・実行ファイルSHAを記録し、リンク拒否も検証した。`Verify-LocalEvidence.ps1` の244項目でソース・程序集・原本SHA・全体結果・24件66paneのGUI画素・発行manifest10ファイルを照合し全成功。この統合の `d650faccaccebb23eb802ff83ed630e5e448de30` は [GitHub run 36874016002](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36874016002) と [CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36874015978) が成功した。Windows各22047 E2E成功・0失敗・3skip、Mac各22056成功・0失敗・2skip、全構成469 UI成功。各原本72件198描画frameとGUI24件66paneのSHAが一致し、normal8・region164・保護と描画上限拒否も確認した。8成果物のmanifest64ファイルのSHA・サイズ全一致、SDK10.0.401・コンパイラー／CodeQL警告0。Node非推奨annotation4件は別に記録する。証拠は `E:/DiffBeacon-artifacts/github/36874016002` と `artifacts/verification/image-three-way/ci-36874016002-summary.json`で、この限定結果だけで画像の全機能移植完了とは判断しない。

## 静止画像の領域コピーと共有履歴

原本の選択／全領域コピー、三者auto、全pane共有Undo／Redo、dirty／savepointを `ImageEditSession` へ移植し、開発用 `--image-copy` と透明RGBを保つPNG別名保存へ接続した。無改変原本143ケース・935状態は採取と再採取がbytes一致、5142検証成功。golden SHAと出典、zero-filled BGRA拡張・raw pasteのadapter境界は[コピーfixture](../tests/Fixtures/ImageCopy/README.md)、操作・処理上限・出力保護は[画像の契約](IMAGE-VIEWER.md#静止画像コピー核の診断)を参照する。FreeImageの新規画素／paste、元BPP／palette／format、アニメーション／多ページ保存は完全互換を確認していない。GUI編集・編集済みrawのHTML接続は次節の実装へ進めた。

2026-10-02のRelease buildは警告0・エラー0。限定E2Eは4336成功・0失敗・リンク1skip、通常DLL全体は26342成功・0失敗・10skip。原本全状態と5種類のPNG保存を独立BCL復号・再読込みへ照合し、Undo直後savepoint・既存出力属性・入力／script／readonly保護とJSON／作業／履歴容量拒否を実測した。Windows x64 Native AOT全体は26387成功・0失敗・3skip、管理者実行でリンク拒否も成功、headless UI回帰469成功・0失敗。sourceと通常程序集は検証中不変で、原本再照合・発行manifest10ファイル・実行環境を `VerifyEvidence.py` の574項目へ照合し全成功。証拠は `artifacts/verification/image-copy-cli` と `E:/DiffBeacon-artifacts/local/image-copy-cli-d650-20261002`。最初の管理者runner起動は終了記録がなく、診断ログと捕捉範囲を追加した再実行で管理者と終了0を確認した。最初の原因は未確定で、製品コードを変更した復旧ではない。GUI編集、履歴128件の厳密な境界、処理中OSシグナル取消、通常デスクトップはこの単位で実測していない。4RIDのGitHub検証は以下の記録に示す。

コードコミット `33a67d1798beb1794b922b345dd4cdc44d06386e` は [GitHub .NET CI 36884448976](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36884448976) と [CodeQL 36884449144](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36884449144) が成功した。

| RID | CLI E2E成功 | 失敗 / スキップ | headless UI成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 26387 | 0 / 3 | 469 | 発行・実行成功 |
| `win-arm64` | 26387 | 0 / 3 | 469 | 発行・実行成功 |
| `osx-x64` | 26396 | 0 / 2 | 469 | 発行・実行成功 |
| `osx-arm64` | 26396 | 0 / 2 | 469 | 発行・実行成功 |

全構成SDK10.0.401、コンパイラーのCS/IL/MSB警告0。原本143ケース・935状態、5種類のPNGをCIのBCL復号と別のPython標準ライブラリで全BGRA照合し、Windows属性・Mac Unix 0600、Undo直後savepoint、入力／script／readonly／実リンク拒否、JSON・累積作業・履歴容量の拒否理由と既存出力保持も成功した。既存強調72ケース・領域164ケースとUI469項目も維持した。Windowsの3skipはUnix権限・Mac大小文字別名・包装の大小文字衝突、Macの2skipは包装の大小文字／NFC衝突入力を同一ファイルへ解決する環境条件で、画像コピーのリンク拒否は4構成すべて実行した。

取得した8成果物のmanifest64ファイルのSHA・サイズが全一致。証拠は `E:/DiffBeacon-artifacts/github/36884448976`、集計は `artifacts/verification/image-copy-cli/ci-36884448976-summary.json` に保持した。upload-artifactのNode非推奨annotation4件はコンパイラー警告と区別する。全watch・download・verifierは終了0。GUIの画像編集接続と通常デスクトップ／ネイティブ保存ダイアログの実測は、この結果に含まれない。

## 静止画像GUIの編集・PNG保存

二者の両方向・三者の6方向の選択／全領域コピー、競合以外の自動コピー、共有Undo／Redoを通常画像ビューへ接続した。保存は原画PNGの別名保存とし、成功した側だけのパス・比較済みパス・snapshot・保存点を更新する。閾値再比較は編集を保持し、HTMLは未保存の原画も反映する。未保存画像の包装は拒否する。仮sessionと描画をまとめて採用し、取消・古い完了では直前の状態を保持する。保存中の編集・再比較・破棄を拒否し、全タブの入力・フィルター・workspace・readonly・リンクを置換直前にも確認する。

2026-10-02の最終Release buildは警告0・エラー0。通常DLL全体E2Eは26342成功・0失敗・10skip、通常DLLとWindows x64 Native AOTのheadless UIは各812成功・0失敗。無改変コピー原本の代表12操作列・66状態で全原画・領域・履歴・dirtyを照合し、両実行形式の観測JSONはSHAも一致した。PNGは独立BCL復号と再読込み、HTMLは現在の原画と採取後の編集分離を確認した。最小850×550でも画像viewportとスクロール後の保存操作を確認した。最終Native AOT全体E2Eは26387成功・0失敗・3skip、管理者実行のリンク拒否も成功した。証拠は `E:/DiffBeacon-artifacts/local/image-copy-gui-33a67`、過去の途中結果は `artifacts/retention/index.json` から参照する。Native発行は `Publish.ps1 -SkipVerification` とし、UI自己検証をEドライブへ別途出力した。GUI接続のMac／ARM CIと通常デスクトップ操作はこの記録時点では未実測。元形式／多ページ編集保存、FreeImageの完全互換と残りの画像操作は引き続き未完了。

GUI接続のコミット `6734dc1101190279fc2ca3a445e0699c8a74efb4` の [GitHub run 36900262981](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36900262981) はWindows両構成が成功し、Mac両構成はNative AOT発行後のUI自己検証で失敗した。Windowsは各E2E26387成功・0失敗・3skip、UI812成功。Macは最小850×550で画像viewportの高さが0となり、309成功後に停止したため全体E2Eは未実行。[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36900262982) は成功した。取得した検証成果物4個は `E:/DiffBeacon-artifacts/github/36900262981` に保持し、発行物のSHA照合はこの失敗runでは未実測。画像モードの共通設定欄の最大高さを比較ペインの30%から20%へ縮め、同じ最小サイズ・viewport下限の検証を維持して再検証する。

この高さ修正後のローカルRelease buildは警告0・エラー0、通常DLLの全体E2Eは26342成功・0失敗・10skip、Windows x64 Native AOTは26387成功・0失敗・3skip。両実行形式のUI812項目が成功し、850×550の三者viewportは各61 px、代表12操作列・66状態の観測SHAも一致した。ソース72ファイルと発行manifest10ファイルのSHA・サイズを照合した。証拠は `E:/DiffBeacon-artifacts/local/image-copy-gui-mac-layout` に保持する。Macでの修正確認は新しいコミットのCIを必要とし、このローカル結果に含めない。

## 画像の全フレームHTML

画像HTMLのコミット `675e61255d1cfbcbb64c29763bcee40821e533de` は [GitHub run 36859703730](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36859703730) と [CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36859703759) が成功した。Windows両構成は17168成功・0失敗・3skip、Mac両構成は17177成功・0失敗・2skip、4構成とも260 UI成功・SDK10.0.401・コンパイラー警告0。画像HTML固有項目は各構成196成功。取得した8成果物のmanifest列挙64ファイルのSHA・サイズがすべて一致した。証拠は `E:/DiffBeacon-artifacts/github/36859703730`。全体取得時のデバイスエラーはWindows成果物の個別再取得で解消し、最初の失敗ログも保持する。これはCI実行失敗とは区別する。upload-artifactのNode非推奨annotation4件はコンパイラー警告に含めない。以下のローカル記録と同じコードを4RIDで検証した結果である。

このSHAは後から追加した原本ブロック・三者領域処理を含まない。通常デスクトップのOS操作はheadless UIと区別する。

以下はSHA 675e時点の二者HTML検証記録であり、後続の三者／原本強調接続は上記を参照する。二者画像の全同番号フレームまたは表示中の選択組を、左右・ピクセル差分PNGを埋め込む自己完結HTMLへ出力した。GUIは表示中の原本スナップショットと確定閾値を使い、選択位置を変えない。CLIの `--report-project` は画像にだけフレーム組と閾値を指定でき、包装は同梱原本の確定内容から同じ生成器を呼ぶ。base64増幅を含むUTF-8本文32 MiBと、既存の入力・画素・枚数・復号量・包装合計上限を維持する。この時点では三者画像の詳細を明示拒否していた。操作は [画像の契約](IMAGE-VIEWER.md) に集約する。

単体とZIP包装の埋込みPNGを独立したBCL PNG復号で全画素・SHA・CRCまで照合する。既存26画像・30フレームの固定期待値、後続差分・枚数差・寸法・透明合成・閾値、選択組、説明escape、包装展開と相対プロジェクト再読込み、32 MiB超過と既存出力保持を確認する。新しい文脈の独立レビューでは別に作ったPNG/GIFと実CLI30呼出しの243項目成功・0失敗・リンク1skipを確認した。

レビューで、GUIが入力プロジェクトのパスをレポート・包装の保護へ渡さず、プロジェクト自身を上書きできるP2を検出した。実GUIで両方の上書きと元SHAの変更を記録してから、ロード・保存したパスを保持して共通ガードへ渡すよう是正した。修正後は画像・テキストのレポートと包装を拒否し、元SHAを保持する。別名保存後の通常更新は成功する。修正成立の追加確認は同じレビュー担当の静的・証跡照合であり、新しい独立実行とは区別する。

最終コードの通常DLL全体E2Eは17128成功・0失敗・9skip、Windows x64 Native AOT全体E2Eは17168成功・0失敗・3skip、headless UIは両方260成功・0失敗。通常版の9skipには権限不足で作れなかった画像レポートのリンク検証1件を含み、Native版は管理者実行で入力・出力・親ディレクトリのリンク拒否も成功した。最終Release buildとAOT発行のコンパイラー警告0。全発行ファイルのSHA・サイズ、GUI保護前後のSHA、ソース・程序集の不変を `artifacts/verification/image-reports/Verify-LocalEvidence.ps1` で照合した。証拠は同ディレクトリ、独立レビューは `artifacts/verification/image-reports-review` に保持する。追加したGUI検証で非同期読込みを同期的に待って停止した実行は失敗記録として保持し、UIイベントを処理しながら待つ修正後の260成功を採用する。通常デスクトップ、生成中のOSシグナル、一回のネイティブ復号・PNG符号化内の中断、包装合計1 GiBの厳密な境界、全旧画像形式・三者画像・画像マージは未検証または未移植。この画像HTMLを含むMac・Windows ARM64のSHA単位検証はGitHubで行う。

## 表のセル内検索・置換

同セル内の前後一致と折返し、固定した文字選択範囲、一件・選択ペイン内の全置換と一括Undoを追加した。decoded値で一致を計画し、既存の `ReplaceCell` を通して必要なセルだけを再引用する。capture展開を制限し、未反映セル編集・古い選択・計画中の対象変更を拒否する。原文反映後に再比較を取消した場合も原文とモデルを復元する。長い一覧セルは512文字・高さ64までのプレビューとし、編集・検索・保存・HTMLの全文を保持する。詳細は[表の操作](TABLE-EDITOR.md)を参照する。

通常DLLの全E2Eは2954成功・0失敗・8スキップ、headless UIは220成功・0失敗。ローカルWindows x64 Native AOTは2985 E2E成功・0失敗・3スキップ、220 UI成功。最終の単独ビルド・Native AOTコンパイラー警告0を確認した。UTF16BE BOM付きの一括置換保存を別Native AOTプロセスでレポート化し、Python標準CSVの独立期待値・全18 HTMLセルと一致した。入力・出力・失敗時のJSON・PNG・再現契約・ログは `artifacts/verification/table-search`、`artifacts/verification/table-search-readonly-final`、`artifacts/e2e/table-search-readonly-final`、`artifacts/e2e/table-search-native-win-x64` に保持する。初回は10万文字セルの全文描画でUI比較がtimeoutし、描画を制限した後に同入力の成功を確認した。実デスクトップ・旧raw buffer横断検索・PCRE/Rx完全互換・矩形文字編集は未検証または未移植。新規文脈のレビュー枠はagent thread limitで未取得、既存担当の補助確認を独立レビューと称さない。

初回の[GitHub run 36800893396](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36800893396)（`1a20d97470444fc22d46f2d2cf7581985bc46528`）はWindows両構成で成功したが、両MacはNative AOT後の表セル描画のUI確認で失敗した。同SHAの[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36800893386)は成功。操作欄を縦スクロールし、ビューの半分以下に収めて一覧領域を確保した。UI検証では描画を確定し、700px高のウィンドウで行へ移動後のセル座標が実viewport内にあることも確認する。旧操作欄では一覧高0、修正後58/セルY23をローカルで観測した。単なるTextBlockの存在確認だけでは、この表示不具合を検出できない。失敗と修正後のPNG/JSONは同契約ディレクトリに保持する。

追加の実画面検証で、原文反映後に読取り専用へ変更して中止すると、通常writerが原文復元も拒否することを確認した。反映した原文がそのままである場合に限る復元callbackを使い、原文とモデル、元ファイル、変更後の読取り専用状態を保持する。通常DLLとWindows x64 Native AOTの220 UI成功にこのケースを含める。失敗と修正後の記録は `artifacts/verification/table-search-readonly-before`・`table-search-readonly-final`、契約・ログは `artifacts/verification/table-search` に保持する。

Macの700px画面では一覧高21・セルY15で文字の下端が切れており、セルが少しでもviewport内にある旧確認では見逃していた。共通の設定・操作欄もスクロール可能にし、960×700でセルの高さ全体が一覧内にある確認へ変更した。最新のローカル通常DLLとWindows x64 Native AOTは220 UI成功、通常DLLの全E2Eは2954成功・0失敗・8スキップ。Windowsで一覧高63・セルY28・文字高17を観測した。結果は `artifacts/verification/table-search-full-viewport-final`・`artifacts/e2e/table-search-full-viewport-final`、ビルド・発行ログは同契約ディレクトリに保持する。

最新の表示修正を含むコード `67ef483608befbd7fc9825535a3ef31cf7f040d8` は、[GitHub run 36805372990](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36805372990)の4構成すべてでNative AOT発行・headless UI・CLI E2Eに成功した。[同SHAのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36805373087)も成功。SDKは全構成10.0.401、CS/IL/MSBコンパイラー警告0件。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 2985 | 0 / 3 | 220 | 発行・実行成功 |
| `win-arm64` | 2985 | 0 / 3 | 220 | 発行・実行成功 |
| `osx-x64` | 2994 | 0 / 2 | 220 | 発行・実行成功 |
| `osx-arm64` | 2994 | 0 / 2 | 220 | 発行・実行成功 |

4構成とも960×700でセルの文字高全体が一覧内にあることを確認し、Mac ARM64のPNGでも欠けずに表示されることを目視確認した。全構成の入力・出力・JSON・PNG・manifest・ログ・run情報・集計を `artifacts/github/36805372990` に保持する。集計の再現手順は `artifacts/verification/table-search/Verify-GitHubResults.ps1`。各Macの `.app` と実行権限を保持するtarを同runのArtifactsから取得できる。UIはheadless操作・描画であり、通常デスクトップの実測とは区別する。署名・公証・公開は実施していない。

## 表の行合わせ・セル編集

原文区間付き文書と共有行対応モデルを導入し、GUI・`--table`・単体/包装HTMLの判定を統一した。完全一致アンカーの間で上限付き類似対応、三者では祖先対応と左右挿入対応を統合する。セル編集は一つの原文区間だけを変更し、Undo/Redo、readonly・祖先・ghost・古い座標の拒否、既存保存へ接続した。検索は前後・折返し・case・regex・word・ペイン指定、画面は32列ページと行仮想化。同セル内検索、固定文字範囲、一件/選択ペイン全置換と一括Undoへ拡張した。旧方式の全スコア・矩形文字編集・raw横断検索は未完了。[表の契約](TABLE-EDITOR.md)を参照する。

通常DLLの全E2Eは2954成功・0失敗・8スキップ、headless UIは139成功・0失敗。ローカルWindows x64 Native AOTの発行と139 UI成功を確認した。入力・出力・行対応JSON・実テキスト入力/ボタン操作・UTF16BE BOM保存後のbytes・PNGを`artifacts/e2e/table-full`・`artifacts/verification/table-repaired`へ保存する。疎な表は各側2万実セル/1万列/10001行で、表示コントロール数を制限し、従来2億座標相当の検索を実セルのみで完了した（ローカル通常DLLの1実測25 ms、比較用benchmarkではない）。

ローカルWindows x64 Native AOT全E2Eは2985成功・0失敗・3スキップ。[GitHub .NET CI 36796867483](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36796867483)は実装SHA `d14a9a9932c771a53e7bb8840b9dc4a84e32ea46` の4構成で成功。Windows x64/ARM64は各2985成功・0失敗・3スキップ、Mac Intel/ARM64は各2994成功・0失敗・2スキップ、UIは各139成功。SDK10.0.401、CS/IL/MSB警告0をログ・manifestで確認。同SHAの[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36796867472)もワークフロー成功。全構成の入力・出力・PNG・JSON・ログを`artifacts/github/36796867483`へ保存した。WindowsのUnixモードとMac固有経路、ファイルシステムのcase/NFC alias作成可否によるスキップを集計へ保持する。GUIでUTF16BE BOM付き保存した表を別のNative AOTプロセスで再読込みし、Python標準CSVパーサーで三者の全セル値・順序を照合して一致した（`external-reload-oracle.json`）。新規文脈のレビューはagent thread limitで未実施。既存担当の補足確認でクリック例外・疎な表示/検索の膨張を修正し、記録を`artifacts/verification/table-next`に保持する。headless操作と通常デスクトップの実測は区別する。

## 実行した検証

2026-10-01 JST、WordDiffと変更ブロックの行内強調を移植したコード `760900e5a4efec5c5175f446036b3eda91c03e2c` は、[GitHub run 36811104557](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36811104557) の4構成でNative AOT発行・headless UI・CLI E2Eに成功した。[同SHAのCodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36811104538) も成功。SDKは全構成10.0.401、CS/IL/MSBコンパイラー警告0件。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 3862 | 0 / 3 | 224 | 発行・起動成功 |
| `win-arm64` | 3862 | 0 / 3 | 224 | 発行・起動成功 |
| `osx-x64` | 3871 | 0 / 2 | 224 | 発行・起動成功 |
| `osx-arm64` | 3871 | 0 / 2 | 224 | 発行・起動成功 |

今回追加した135代表goldenの終了コード・全区間・文字コード・入力保持、fallback・引数拒否・NUL拒否、ブロック投影・共有予算・HTMLを全構成で実行した。Windows x64では別途、有効5,832旧ソースケースをすべてNative AOTと照合した。全構成のJSON・入力・出力・PNG・manifest・ログ・run情報は `artifacts/github/36811104557`、全件照合は `artifacts/verification/table-worddiff/native-golden-profile-all` に保持した。両MacのWordDiff PNGを実際に目視し、一致文字と離れた変更の表示を確認した。

WindowsのスキップはUnix権限・Mac大小文字別名・包装の大小文字衝突用入力、Macの2スキップは包装の大小文字/NFC衝突用入力を別ファイルとして作れない実ファイルシステムによるもの。リンク拒否経路はローカルの管理者プロセスとGitHubの全対象で実行した。8成果物を確認し、両Macの `.app` とtarを含む `DiffBeacon-osx-x64` / `DiffBeacon-osx-arm64` は同runから取得できる。署名・公証・公開・通常デスクトップの手動操作は実施していない。runnerのupload-artifact Node20廃止通知とMac ARMの容量通知はコンパイラー警告と区別する。

以下は前工程の検証記録。

2026-10-01 JST、テキスト・表・JSONの二者／三者共通HTMLレポート、単体プロジェクトCLI、GUI中止・保存保護を追加したコード`c005d377c4748657b262a91c85570406b7724de6`を[GitHub Actions run 36793389828](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36793389828)で実行し、4ジョブすべて成功した。全構成SDKは`10.0.401`、CS/IL/MSBコンパイラー警告0件。[CodeQL workflow](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36793389800)も同コードで成功した。これはworkflow完了の記録であり、全機能の同等性を証明するものではない。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 2070 | 0 / 3 | 111 | 発行・実行成功 |
| `win-arm64` | 2070 | 0 / 3 | 111 | 発行・実行成功 |
| `osx-x64` | 2079 | 0 / 2 | 111 | 発行・実行成功 |
| `osx-arm64` | 2079 | 0 / 2 | 111 | 発行・実行成功 |

単体HTMLと包装内HTMLを実CLI・独立BCL ZIP読込みで検証した。三者では祖先アンカー、異なる位置への挿入、連続空行、片側削除、空祖先を含む全側の本文・元行番号の順序と完全保持を確認した。表は各セル・行列座標、欠落と空セル、引用符・引用内改行、比較設定を検証し、Python標準CSVでも行数・列数と生成HTMLグリッドを照合した。JSONは正規化後の内容・祖先だけの変更・キー順の同等性を確認した。

未選択タブのreadonlyフォルダー配下を含む全入力・フィルター・プロジェクトの保存保護、readonly属性・リンク・不正解析・32 MiB上限による既存出力保持も確認した。GUIは現在の未保存本文と三者・表・JSONを出力し、実際の中止ボタンで未完了レポートを取り消せる。取消テストは処理未完了と既存出力保持の実測であり、描画の特定ループ内や原子的保存の途中で取消した証拠ではない。Windowsの3スキップ、Macの2スキップは前の包装工程と同じOS・ファイルシステム固有項目で、各検証JSONへ理由を保存した。

ローカル通常DLLは2039成功・0失敗・8スキップ、リンク作成権限のあるWindows x64 Native AOTは2070成功・0失敗・3スキップ、画面111成功。初回の限定検証では修正前の期待値を取り込んだテストアセンブリとGUIの入力固定値で失敗したため、原本の直前バイト列を確認する検証へ直し、全体を再ビルドした。フォルダーモードはファイルパスが指定されていても包装対象から明示拒否する。失敗記録は`artifacts/e2e/reports-managed`・`artifacts/verification/reports-managed`、最終結果は`artifacts/e2e/reports-full`・`artifacts/e2e/reports-native-win-x64`・`artifacts/verification/reports-contract`へ保持する。

4構成の入力・出力・JSON・PNG・manifest・ログ・集計を`artifacts/github/36793389828`に保持する。UIはheadless描画・操作で、HTML自体のブラウザー描画やネイティブファイル選択は未実測。agent thread limitのため新規文脈の独立レビューは未実施で、既存担当の補足確認と成立指摘の修正記録を同契約ディレクトリに残す。このrun時点の表レポートは行列座標の対応。後続の表エディター移植は下記に区別する。

以下は比較文書の包装追加時点の記録。

2026-10-01 JST、比較文書とHTMLレポート・パッチ・プロジェクトの包装を追加したコード`ef1e44df438e602560eb0578ff5316d9c0bf7546`と検証修正`9f5e92d0f6c3465c0b64c244a716d5c00c59a408`を[GitHub Actions run 36790193652](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36790193652)で実行し、4ジョブすべて成功した。全構成SDKは`10.0.401`、コンパイラーのCS/IL/MSB警告は0件。Mac Intel/ARM64の`.app`とtarを含む発行物も同runに保存した。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 1317 | 0 / 3 | 103 | 発行・実行成功 |
| `win-arm64` | 1317 | 0 / 3 | 103 | 発行・実行成功 |
| `osx-x64` | 1326 | 0 / 2 | 103 | 発行・実行成功 |
| `osx-arm64` | 1326 | 0 / 2 | 103 | 発行・実行成功 |

包装後のZIPを独立したBCLデコーダーで読込み、全内容・相対プロジェクト参照・設定・選択順を確認した。実アプリでの展開、プロジェクト再読込み、パッチ適用、7z/TAR.GZ/TAR.BZ2、URL参照、全未選択入力を含む上書き保護、リンク・未保存編集・比較前のパス変更の拒否、処理開始後のキャンセルと一時出力除去も検証した。Windowsの3スキップはUnix権限・Mac大小文字別名・包装の大小文字衝突、Macの2スキップは包装の大小文字・NFC衝突である。各ファイルシステムが二つの入力名を同一ファイルへ解決する衝突ケースは作成できず、理由を記録した。WindowsではNFC衝突拒否、MacではUnix権限・既存大小文字別名の保護を実測した。

ローカル通常DLLは1294成功・0失敗・7スキップ、リンク作成権限のあるWindows x64 Native AOTは1317成功・0失敗・3スキップ、UIは103成功。容量限定の別実測では、入力合計1 GiB超過と生成レポート合計1 GiB超過の2ケースが既存出力を保持して拒否され、一時出力も残らなかった。ピークWorking Setはそれぞれ約16.4 MiB・120.2 MiBで、入力生成手順・ハッシュ・計測・清掃記録を`artifacts/e2e/packaging-capacity-probes/20260930T231514228-ba030c35`に保存した。この容量実測はローカルWindowsのみで、4構成共通の計測ではない。

全構成の入力・出力・JSON・PNG・run情報・ログ・集計を`artifacts/github/36790193652`に保持し、Mac ARM64の包装ダイアログと復元した比較画面を目視確認した。クリップボードのファイル形式はheadlessバックエンドで検証したもので、通常デスクトップでの持続性・ネイティブファイル選択は未実測。新規独立レビュアーはagent thread limitで起動できず、既存担当の読み取り専用確認と成立指摘の解消記録を`artifacts/verification/workspace-managed/packaging-review-after.json`に保持する。

先行run `36789620215`のWindows両構成はリンク保存先の表記比較だけ失敗した。アプリはリンク出力を拒否し、元ファイルとリンクを保持していたが、検証側が作成APIの正規化前後のスラッシュ表記を同一と仮定していた。作成直後の実リンク値との比較へ修正し、原本のバイト列保持も確認した。失敗結果・ログ・`link-spelling-proof.json`を保持し、最終runで同じ経路の成功を確認した。

以下は複数比較プロジェクト追加時点の記録。

2026-10-01 JST、複数比較プロジェクト・読取り専用・表の引用符設定を追加したコードコミット`a17ef62c24d134d6eb0d25ad7501c4d176e0c4ad`を[GitHub Actions run 36786270273](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36786270273)で実行し、4ジョブすべて成功した。全構成SDKは`10.0.401`、コンパイラーのCS/IL/MSB警告は0件。Mac Intel/ARM64の`.app`とtarを含む発行物を同runに保存した。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 1063 | 0 / 2 | 93 | 発行・起動成功 |
| `win-arm64` | 1063 | 0 / 2 | 93 | 発行・起動成功 |
| `osx-x64` | 1075 | 0 / 0 | 93 | 発行・起動成功 |
| `osx-arm64` | 1075 | 0 / 0 | 93 | 発行・起動成功 |

全タブの順序・選択位置・比較設定の保存／復元、旧単一JSONと複数`.WinMerge` XML、相対パス・URL・未対応設定の保持、256件／4 MiB上限、不正入力・DTD・リンクの拒否、既存ファイル属性とMac Unix権限の保持を実アプリ経路で検証した。省略された設定と置換ルールの既定値がsource-generated JSON読込みで消える問題を、setterを持つ保存DTOと明示的なAOT対応コンバーターで解消した。

GUIでは全タブ復元・未保存確認のキャンセル・設定ダイアログ、表の区切り／引用符／引用内改行、readonly編集・コピー・テキスト／バイナリ／アーカイブの保存先保護を確認した。保存された外部ツールIDは自動登録・実行しない。Windowsの2スキップはUnix権限・Mac大小文字別名だけで、両Mac構成では成功した。全構成の入力・出力・JSON・PNG、run情報・ログ・集計を`artifacts/github/36786270273`に保持し、Mac ARM64の表・readonly・旧プロジェクト画面を目視確認した。

ローカルWindows x64 Native AOTは1063 CLI＋93 UI成功。通常DLLは1048成功・0失敗・5スキップ（リンク作成権限とOS固有項目）。プロジェクト限定E2Eは127成功・0失敗・1スキップで、リンク拒否も権限のあるNative AOT実行で成功した。新規独立レビュアーの起動はagent thread limitで拒否されたため、新しい文脈での独立レビューは未実施。既存の別機能担当による読み取り専用確認と成立指摘の再現・修正記録を`artifacts/e2e/workspace-review`に保持する。検証側の配列比較が既定値補完を誤って拒否したケースも修正し、最終E2Eで成功を確認した。UI検証はheadless描画・操作であり、通常のネイティブウィンドウ・ファイル選択・シェル統合の手動実測を含まない。

以下はZIP派生・TAR系追加時点の記録。

2026-10-01 JST、ZIP派生・TAR/TAR.GZ/TAR.BZ2の作成・再梱包と全件展開を追加したコードコミット`96bdc45ab8f687fb2d2c33263229d5eca6109269`を[GitHub Actions run 36781192763](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36781192763)で実行し、4ジョブすべて成功した。全構成SDKは`10.0.401`、コンパイラーのCS/IL/MSB警告は0件。Mac Intel/ARM64の`.app`とtarを含む`DiffBeacon-<RID>`発行物を同runに保存した。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 931 | 0 / 2 | 69 | 発行・実行成功 |
| `win-arm64` | 931 | 0 / 2 | 69 | 発行・実行成功 |
| `osx-x64` | 943 | 0 / 0 | 69 | 発行・実行成功 |
| `osx-arm64` | 943 | 0 / 0 | 69 | 発行・実行成功 |

新規形式の日本語名・空ディレクトリ・内容ハッシュ、暗号化入力の全件展開、既存出力保持、TARチェックサム・リンク・親子衝突、gzipのCRC・欠落フッター・連結メンバー、深い暗黙親パスの上限、GUIで展開開始後のキャンセルと一時出力除去を検証した。Windowsの2スキップはUnix権限・Mac大小文字別名だけで、両Mac構成では成功した。Python 3.13.15の独立デコーダーでもTAR.BZ2の日本語名と全内容を確認した。PythonはE2E専用で発行アプリの依存ではない。各発行物のSharpCompressライセンス同梱も確認した。

全構成の入力・出力・JSON・PNG、run情報・ログ・集計を`artifacts/github/36781192763`に保存し、Mac ARM64のTAR.GZ比較画面・暗号化ZIPのパスワードマスクを目視確認した。ローカルWindows x64 Native AOTは931 CLI＋69 UI成功、通常DLLは922成功・0失敗・4スキップ（リンク権限とOS固有項目）。UI自己検証は同じ保存先での連続実行も成功した。UI検証はheadless描画・操作で、通常のネイティブウィンドウ・ファイル選択・シェル統合の手動実測を含まない。

先行run `36779758667`はWindows ARM64のOS標準tarによる独立検証で日本語名の失敗と作成プロセスのクラッシュが発生した。同runのアプリ経路とUI 69件は成功し、生成TAR.BZ2を別環境のPythonで読めることを確認したため、独立検証を全構成同じPython標準ライブラリに統一した。失敗の入力・ログと別デコーダーの結果は`artifacts/github/36779758667`に保持している。

以下は暗号化・solidアーカイブ読込みと7z作成追加時点の記録。

2026-10-01 JST、アーカイブ比較・暗号化/solid読込み・非暗号化7z作成/再梱包を追加したコードコミット`a27989e328d3850d3bfcdbc682a7b7e0395835b0`を[GitHub Actions run 36775611278](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36775611278)で実行し、4ジョブすべて成功した。全構成SDKは`10.0.401`、コンパイラーのCS/IL/MSB警告は0件。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 687 | 0 / 2 | 64 | 発行・起動成功 |
| `win-arm64` | 687 | 0 / 2 | 64 | 発行・起動成功 |
| `osx-x64` | 699 | 0 / 0 | 64 | 発行・起動成功 |
| `osx-arm64` | 699 | 0 / 0 | 64 | 発行・起動成功 |

Windowsの2スキップはUnix権限・macOS大小文字別名の検証で、両Mac構成では実際に成功した。暗号化7z/RAR4/RAR5/WinZip AES、solid、CRC値0へ破損させたZIPの拒否、原本・既存出力保持、フォルダー内への再作成、17 MiBエントリのプレビュー・保存を検証した。各発行物へSharpCompressのMITライセンスを同梱した。全構成の入力・出力・JSON・PNG、run情報・ログ・集計を`artifacts/github/36775611278`に保存し、WindowsとMac ARM64のアーカイブ画面を目視確認した。ローカルWindows x64 Native AOTも687 CLI＋64 UI成功、通常DLLは678成功・0失敗・4スキップ（リンク権限とOS固有項目）。UIはheadless描画・操作で、通常ウィンドウ・ファイル選択・シェル統合の手動実測を含まない。

以下はマージ結果セッション・詳細フィルター追加時点の記録。

2026-10-01 JST、マージ結果セッション・詳細フィルターを追加したコードコミット`1d1e54d3b3db999a95bacdb71506f9bb4d2e8747`を[GitHub Actions run 36770239518](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36770239518)で実行し、4ジョブすべて成功した。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 552 | 0 / 0 | 55 | 発行・起動成功 |
| `win-arm64` | 552 | 0 / 0 | 55 | 発行・起動成功 |
| `osx-x64` | 554 | 0 / 0 | 55 | 発行・起動成功 |
| `osx-arm64` | 554 | 0 / 0 | 55 | 発行・起動成功 |

同runの全構成のJSON・入力・出力・PNG、run情報とログを`artifacts/github/36770239518`に保存した。ローカルWindows x64のNative AOTでも552 CLI＋55 UI成功。通常DLLでのE2Eは543成功・0失敗・2スキップで、リンク作成権限がないプロセスのスキップ理由を残し、権限のあるNative AOT実行でその経路も確認した。独立レビューの成立指摘は修正後に再現ケース・連続編集・Undo/Redoで解消を確認した。今回のUI操作もheadless描画であり、ネイティブウィンドウ・ファイル選択・シェル拡張の手動検証は含まない。

以下は初回の基盤移植時点の記録。

2026-10-01 JST、コードコミット`adde5eccfd2dea91a3c485eceb03e5335ca72bfc`を[GitHub Actions run 36759572632](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/36759572632)で実行し、4ジョブすべて成功した。SDKはすべて`10.0.401`。通常ビルド・AOT発行のコンパイラー警告は0件。

| RID | CLI E2E成功 | 失敗 / スキップ | UI自己検証成功 | Native AOT |
| --- | ---: | ---: | ---: | --- |
| `win-x64` | 446 | 0 / 0 | 17 | 発行・起動成功 |
| `win-arm64` | 446 | 0 / 0 | 17 | 発行・起動成功 |
| `osx-x64` | 448 | 0 / 0 | 17 | 発行・起動成功 |
| `osx-arm64` | 448 | 0 / 0 | 17 | 発行・起動成功 |

runには`DiffBeacon-<RID>`発行物と`verification-<RID>`の入力・ログ・JSON・PNGを保存している。UI検証はSkiaで実際のアプリ画面を描画するheadless実行であり、Explorer/Finder起動・ネイティブファイル選択・署名・公証を実測したものではない。Windows x64 / Mac ARM64の4ペインPNGを目視し、文字とレイアウトを確認した。ローカルのWindows x64でも同じ446 E2E＋17 UIが成功した。

## 既存ブランチと旧ソース

| 参照 | 判断 |
| --- | --- |
| `origin/feature/4pane-merge` (`74c2ae1`) | 結果ペイン・競合移動の仕様を移植。130コミットすべてを新C#へ移したわけではないため参照を保持 |
| `origin/Release/v2.16.58.2` (`8221725`) | 最終バイト・フィルター・比較中のソート・隠し行などの回帰条件を参照するため保持 |
| `feature/convert-toolbar-icons-to-svg` / `fix/manual-localization-parameters` / `per-monitor-dpi-aware` | 旧MFC/DPI/DocBook固有。新Avaloniaの通常経路で使わず、ローカルのremote-tracking参照を削除。GitHub上のブランチは削除していない |

旧`Src`、プラグイン、翻訳、シェル拡張・インストーラーのC++実装は未移植項目の根拠として保持する。通常の入口は`DiffBeacon.slnx`だけで、旧ルートsolution・ビルドcmd・`.gitmodules`・13トップレベルsubmoduleの作業ディレクトリは除去した。Git内部のsubmoduleオブジェクトは履歴復旧用に残る。残した旧実装は対応表の未完了項目が移植・対象OSで検証できた時点で除去する。

標準プロバイダーの厳密な対象・上限・意味的な制限は [README](../Src/DiffBeacon.Providers/README.md) に記載する。

## バイナリ範囲編集

Frhedの挿入／既存範囲内の上書き（EOF越え拒否）／start-count削除を実Avaloniaダイアログへ移植。固定長Hex編集、全体コピー、共有履歴、通常保存と内包作業保存の境界は維持する。直接ニブル入力、OSクリップボード互換、Binary本文HTML／patchは残工程。Releaseは警告0／エラー0、限定E2Eは12成功／0失敗／0skip、実ダイアログGUIは110成功／0失敗、全体UIは10,768成功／0失敗。全二者／三者側の保存bytes、訂正・取消・readonly・旧dialogのdraft／revision／保存世代／入力／mode／provider／中止／owner／dispose拒否、4095／4096・空入力・全削除、16MiB／共有64MiB／256操作とRedo保持（履歴48MiB＋既存Redo16MiBで80MiB候補を拒否し、全bytes・revision・保存点・Redo再実行を実panelと独立readerで照合）、保存中の後発編集を確認した。Python標準readerで固定全bytes・全workspace asset・原本ZIPの全entry／CRC・包装展開再読込みを独立照合。1280×850と850×550のPNG・座標でHexと中央保存ボタンへのスクロール到達を確認。証拠は `artifacts/local/binary-range-edits/handoff.json`。実管理者・終端0・実行前後AppSHA一致を確認し、限定scope metadataも明示する。親の固定ソースによる全体Managed／Windows x64 Native AOT検証は、各E2E160,140成功／0失敗／3skip、6,192コマンド・12,384stdout／stderr、UI10,768成功／0失敗、PNG547枚を独立照合して終了0。Managedは266ファイルの全bytesを保持、Nativeは264ファイルを保持し、Core／Providersの2lockはAOT restoreでHEADと同じ依存グラフへ復帰した。範囲編集の保存bytes・全workspace asset・原本ZIP／CRC・包装展開再読込みを、無改変の独立readerで全体E2Eと全体UIの両方から照合した。発行物13ファイルのSHA・サイズと実行前後Native／harness SHAも一致。証拠は `artifacts/local/binary-range-edits-parent/full-managed02-independent.json`、`native01-independent.json` と `range-readers-managed02-independent.json`／`range-readers-native01-independent.json`。範囲編集commit `9d6be1a33dd27293c5c97999d7e714d7ec053a5b` の[4RID CI](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37212361194) attempt 2と[CodeQL](https://github.com/1llum1n4t1s/DiffBeacon/actions/runs/37212361220)は成功した。合計E2E640,578成功／0失敗／10skip、24,774命令・49,548stdout／stderr、UI43,072成功、PNG2,188枚、公式8 ZIP719,846,182 bytesのdigest・全entry CRC／SHA／size・reader・発行物を独立照合した。Windows x64初回の600秒timeoutは失敗証拠を保持し、同じSHA再実行の成功と区別する。証拠は `artifacts/local/binary-range-edits-parent/ci/all-four-receipt.json`。このCIは後続のBinary clipboard変更を含まない。既存image clipboardのsourceSha256は画素fixtureのSHAであり、製品sourceのSHAを意味しない。実OS pointer／保存dialog／新Binary clipboardはこの範囲編集CIで検証していない。

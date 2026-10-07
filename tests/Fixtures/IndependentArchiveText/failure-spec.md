# Independent 実在Archive Text 次verticalの検証仕様

由来: synthetic。製品の実行結果・既存goldenから期待bytesを採取しない。
この仕様をgenerator実装前に保存した。製品未実装・製品未検証の準備物。

根拠（現行コード、2026-10-07）:
- DESIGN.md:87-93: 原本root固定readonly、leaf編集はInheritedReadOnly、作業保存と外部保存の分離、revision競合、asset先行公開、全タブ出力保護。
- Src/DiffBeacon.App/ArchiveProjectInput.cs:8-64: root/chain/leaf/SHA、workingTexts、継承readonly、格納名とsnapshot検証。
- Src/DiffBeacon.App/ProjectInputReader.cs:8-43: 原本leaf検証後に作業版を採用、原本文字コード/BOMと一致。
- Src/DiffBeacon.App/ArchiveTextWorkingStore.cs:8-137: Text metadata、SHA、共有128MiB/256文書、revision競合。
- Src/DiffBeacon.App/WorkspaceStore.cs:453-479: v6 typed Text、旧pathとArchive payloadの重複拒否。
- Src/DiffBeacon.App/TextInputs.cs:36-41: 現行IndependentはPhysical/Untitledだけ。ここで生成するArchiveのv6提案は現行では拒否対象。
- tests/Fixtures/ArchiveWorkingText/README.md と verify.py: 標準ライブラリで全ZIP/CRC/SHA、保存bytes、metadata、相対asset、HTML本文、包装を照合する境界。

成功条件:
1. left/middle/rightの実在leafをIndependentとして取得し、中央を祖先として扱わず3側で編集できる。
2. 各側の編集本文、6方向の全本文コピーを、コピー先の文字コード/BOMで保存。copy-cases.jsonは「全hunkを適用して本文全体がコピー元になる」場合のoracle。部分hunk選択の座標oracleではない。
3. 通常Saveは作業保存点だけを更新しrootの全bytes/全entryを保持。中央も保存・Undo/Redo・dirty判定の対象。
4. 外部SaveAsは指定側だけPhysicalへ移行し、他側と元rootの出力保護を保持。混在改行を一字も正規化しない。
5. v6 workspaceにsemantics Independentを保持、middle descriptorに対するpayload fieldはbaseArchiveInput。workingTextsはencodingName/hasBom/SHA/相対snapshotPathを保存しkindを省略。新versionは導入しない。
6. 保存済み3側をHTML/包装・展開/再読込みで同じliteral本文とbytesへ照合。包装内原本rootは固定SHA/全entry/CRCへ照合。
7. nested2（outer.zip→inner.zip→texts/leaf.txt）と同rootの兄弟leafの識別/更新を分離。同root同leaf共有revision競合を拒否。

起こり得る失敗と期待結果（境界は将来の実アプリE2Eで確認する）:
- Absent descriptor、MissingEntryChain、provider、merge、patch要求: 未対応として明示拒否し既存出力を保持。
- InheritedReadOnly=true、null、省略: leaf編集/copy先/作業Saveを拒否。falseだけ編集可。原本rootへの保存はfalseでも拒否。
- root同size/mtime差替え、root SHA不一致、後続entry破損、CRC不一致、不正chain/leaf、存在しないleaf: 原本検証を省略せず拒否。
- 物理pathとArchive payload同側重複、未知/重複field、kind不一致、snapshot SHA/encoding/BOM不一致、root/assetの外部相対参照/リンク: 復元拒否。
- 同rootの異なるsiblingを同一文書と誤認、古いrevisionからのSave: sibling分離、revision競合は本文/保存点/現行作業版を保持。
- コピー先文字コードで表現できない文字、Text64MiB/store128MiB/256文書境界: 例外を隠さず本文/履歴/保存点/既存出力を保持。本fixtureは上限を実生成しない。
- 取消、古い完了、比較/タブ/入力変更、候補採用失敗: 確定表示/本文/操作状態を保持し古い候補を破棄。
- workspace JSON公開失敗: 旧JSON/既存assetを保持。先行公開assetは保護/再利用対象。
- 外部Save先が全タブ入力/root/workspace/filter/asset/readonly/link: 公開直前にも拒否。
- Save中の後発編集: 固定した保存bytesだけ公開、後発本文はdirty。公開後の再読込/採用失敗: ファイルは保持、paneをPhysicalに切替えない。
- 未保存本文: 単体HTMLは確定snapshotと未保存表示、workspace/包装は拒否。
- 包装root重複basename/chain混同/asset混同: 全SHAと格納名のidentityを維持し展開再読込みで3側を独立に復元。

未検証: 製品CLI/GUI/HTML/package、native Saveダイアログ、実OS、revision競合と取消の実動作。生成したv6 JSONは将来の契約案であり現行互換の成功fixtureとは扱わない。

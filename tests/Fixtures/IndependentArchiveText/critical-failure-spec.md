# 追加境界の失敗仕様（実装前）

既存failure-spec.mdの原本は維持する。期待本文は既存synthetic literal／encoding／BOMで固定し、製品出力から採取しない。

1. 各側の外部SaveAs: 指定側だけPhysicalへ移行し、他2側Archiveの原本／作業版を保持する。三側working保存後に、注入したSavePathPickerを使い実SaveAs APIを呼ぶ。出力全byteをUTF8-noBOM／UTF16LE-BOM／1252のliteralへ照合する。旧rootへの上書きは拒否。v6保存再読込み・包装全ZIP/CRC・展開再読込み・HTMLも三側literalへ照合する。ネイティブdialogは未実測。
2. 同size／同mtime差替え: 各側のrun内専用rootをrawバックアップし、ZIPの最後のcomment byteだけをXOR 1してsize／mtimeを復元する。入力ZIPは依然stdlibで完全復号可能であり、既存rootSHAだけが不一致になる。比較は旧三者本文／doc／保存点／diffを採用保持し、WorkingSave／外部保存は原本変更診断で拒否。dirty／revisionと既存出力を保持する。固定入力は変更しない。
3. 後続entry破損: 既存CC0 Archive Sourcesのlate-bad-sibling.zipをrunにコピーして使う。候補のrootSHAをその破損原本の固定SHAへ合わせ、SHA一致だけで早期拒否する検証にしない。最初のinner.zip／leaf.txtは正常でも後続after.txtのCRCで全体拒否され、候補のleafを三側表示へ採用しない。全3側で旧本文／doc／保存点／diffを保持する。stdlib readerが先頭leafの固定literalと後続CRC拒否を独立確認する。

製品build／実行は親が担当。実装・静的検証・fixture検証と製品合格を区別する。

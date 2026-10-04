# 不在の内包入力の独立検証

固定原本は[Source fixture](../Archives/Sources/README.md)を使い、そのSHA・ライセンスを維持する。新しい期待差分をアルゴリズムへ渡さず、実アプリ別プロセスのworkspace保存・HTML・包装・展開・相対再読込みを検証する。

`verify.py <E2E出力のarchive-missing/proof.json>` はPython標準ZIPの全CRCと原本SHA、実在親の不在anchor、全本文・改行、全包装fileと不在表示を再計算する。入力・出力・ログ・assertions.jsonとindependent-proof.jsonを保持する。空fileの存在変更はGit形式のmetadataを使い、通常のzero-length hunkへ置き換えない。

包装patchの6ケースは、Python readerが元containerから生成した左右本文を基準に、実アプリの `--patch-apply` で外部テキストへ適用します。結果の全byteと入力・patchのSHA保持を確認し、入力・期待値・実出力と実行ログを保持します。

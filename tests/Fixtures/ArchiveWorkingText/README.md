# 実在する内包Textの作業保存の独立検証

入力は `HeadlessArchiveWorkingTextChecks` がrun内だけに生成します。既存の原本fixture・goldenは変更しません。原本ZIPの全byte/SHA・全CRC・内包ZIP全entry、Windows-1252/BOM付きUTF-8の保存bytes、混在改行とHTML全文、相対snapshot・包装全entry・4MiB超作業本文をPython標準ライブラリだけで照合します。入力の構造と期待本文はreader内に明示し、製品の復号・diff・包装APIを期待値生成には使いません。

実アプリ別processの限定E2Eは `--archive-present-only`。GUIの通常保存・外部保存ボタンはheadlessで操作し、ネイティブ保存ダイアログはpath picker注入であり、実OS操作の検証には含めません。生成物・ログ・JSON・PNGはrun内へ保持します。

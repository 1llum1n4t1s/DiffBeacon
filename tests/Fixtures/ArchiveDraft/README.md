# 未命名の内包文書の検証

実アプリの `HeadlessArchiveSourceChecks` と `HeadlessArchiveDraftChecks` が、独立したZIP・空の相手ZIP・編集本文を生成し、同じ比較側の実コントロールと保存経路を操作します。旧版の不在側UNNAMEDとreadonly継承は `Src/DirView.cpp`、初回SaveAsと成功時だけのpath採用は `Src/MergeDoc.cpp` を参照しています。生成物を旧版から採取したgoldenとは扱いません。

`verify.py` はPython標準ライブラリだけで、全ZIP CRC・全格納byte・保存本文・取消／古い完了の既存出力・包装相対参照・HTMLの本文と改行を照合します。行内強調と行番号のHTMLを区別し、固定した原文からパッチ入力・期待出力を独立生成します。E2Eはそのパッチを実アプリで適用し、全byteを照合します。

既存schemaの継承情報省略はreadonlyとして復元し、編集した不在側のSaveAsから元アーカイブを書き換えません。ネイティブ保存ダイアログを操作した検証とは区別します。入力・JSON・PNG・HTML・ZIP・パッチ・ログは各E2E出力へ保持します。コードと生成fixtureはリポジトリと同じライセンスです。

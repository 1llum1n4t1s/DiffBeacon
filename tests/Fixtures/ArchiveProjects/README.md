# 内包入力のプロジェクト独立検証

固定入力は [Source fixture](../Archives/Sources/README.md) を変更せず使う。新しい大容量原本は追加しない。E2Eは二者／三者・混在・empty・内包ZIP／raw TARとgzip rootのプロジェクトを作り、実アプリの保存・HTML・包装・展開・再読込み・leafの外部パッチ適用を実行する。

`verify.py` はPython標準libraryのzipfile／tarfile／gzip／bz2／HTMLParserで全container・包装entryのCRCとbytes、root SHA・chain・leaf、本文・caption、適用後bytesを独立照合する。source・copied・包装projectの対応と実出力pathはE2Eの `archive-projects/products.json` に保持し、`independent-proof.json` と標準出力・エラー・終了コードを生成する。scriptは検証用CC0-1.0、Pythonは製品依存ではない。

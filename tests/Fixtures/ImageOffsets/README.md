# 画像位置ずらしの原本観測

WinIMerge DLL v1.0.54、revision `da639cdfaeca87aaad0eaceec509afa11ad61421` の12ケース43状態。公式DLLの全表示BGRA・offset・履歴・dirty/savepointと実保存PNGを期待値として使う。入力PNGは自作の極小RGBAで、既存ImageTransformsと同じCC0契約。原本由来の処理はImageRegions/LICENSE.txtのGPL-2.0-or-laterに従う。

DLL SHA-256は `36F2A726C34A2323D2E569903B4F1DC6C28ABF1C10447006335F2785FC511BA6`、public headerはblob `d5c1a0c0bcf463fd7ef3237b18e4b7a60b4853fa`。offset付き画素はglobal座標へoffsetを加えて採取した。巨大offsetやoverflowを原本DLLで実行しない。

生成元observations.jsonのSHA-256は `3E24388CA2994F08D0AB20E4A29503DBEA9AB97B9130074C34BA340CE8B63D76`。3回の独立した実行で一致し、最終採取の検証は204項目成功、失敗0。

採取プログラムと原本実行記録は `E:/DiffBeacon-artifacts/reference/image-offsets-v1.0.54/capture-offsets.py` と `run3`。過去2回は内容照合したZIPへ圧縮しており、再実行やfixture利用に重複展開は不要。最終記録から `python -B tests/Fixtures/ImageOffsets/extract.py <run3絶対パス>` で再生成する。通常ビルドとE2Eには原本DLL、C++コンパイラー、Pythonは不要。

確認する失敗は座標正規化、回転との順序、支持矩形外の比較、copy左上拡張、Undoでoffsetを戻す誤り、readonly、raw PNG保存。支持矩形外を透明画素同士の比較へ置換しない。GUI・設定復元・レポート接続は別途自己検証する。

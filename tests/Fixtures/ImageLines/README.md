# WinIMerge画像行Myersの固定script

[原本Diff.hpp](https://github.com/WinMerge/winimerge/blob/da639cdfaeca87aaad0eaceec509afa11ad61421/src/WinIMergeLib/Diff.hpp)を無改変で保存し、BGRA row adapterから既定Myersだけを呼んだ14,797ケース。2回の採取で観測JSONの全bytes一致を確認した。製品C#の出力を期待値へ使わない。原本のLGPL-2.1-or-later著作権表示をheader内に保持する。WinIMerge由来の処理は[GPL v2以降](../ImageRegions/LICENSE.txt)に従う。自作入力はCC0。

0～4行・3種類の行の全14,641組、空側・左右反転・置換・重複・共通端、signed charの126/127/128境界と透明RGBA、threshold 0/.5/1/1.42/2/3/10/510、非推移的equalsの代表順36組、幅違い、64/256/4095/4096/4097行を含む。全script bytesと元行hashを記録する。scriptの元行数保持を全ケース検査した。深いheuristic分岐へ到達したというcoverageの実測はまだない。

失敗先行契約は、hashをunsigned byteで量子化する誤り、32bit wrapの喪失、hashが違う近似行の統合、左→右・最新代表からの探索順の変更、GNUの境界移動の追加、xdl固有discard・同点・対角線走査の変更。これは算法移植用の原本であり、現行アプリの画像挿入削除検出の実装済み宣言ではない。

製品核の照合はE2Eの`--image-lines-only`で行う。実`--image-line-script`へBGRAと閾値だけを送り、期待scriptとhashを除いた入力で全ケースを確認する。核は入力を借用し、両側合計100万行・内部累積metadata128 MiB・作業量2億までとし、上限と取消を例外で返す。閾値の整数量子化が原本C++で未定義になる値は安全に拒否する。CLIは入力JSON16 MiB・2万ケース・累積作業量4億・結果32 MiBを上限とし、全件の成功後にだけstdoutへ公開する。`--max-work N`で累積予算を0..4億へ限定でき、各比較へ残量を渡して処理中に制限する。画像表示・構造コピーとの接続は別の検証単位。

| 固定物 | SHA-256 |
| --- | --- |
| 原本header（Git blob `8863642be2c604a0c8df72886f207c83a8e0bf7f`） | `DB4938A98F1953DDBA9AA6F1F8A8AFB064C5C133036467379633CC6108A98142` |
| gzip | `38B5DC1CAE36478FB59D713ACF7DE19BA704B5BD94AF3D50955A6F4F56BB966B` |
| 展開JSON 5,267,752 bytes | `159878A6360C7DCFC79EA092379D24EBBF90A1EA8ACF789BD50F5B8A154A8CC1` |
| 原本観測JSON | `8CD74EC009262E239E78B2C9E28C973E02331A5C6FFEE79395303F051350C13E` |

採取プログラムは `E:/DiffBeacon-artifacts/reference/image-lines-v1.0.54/capture.py` と `line-probe.cpp`、最新観測は `run2`。前回は照合済みZIPへ圧縮保持する。[共通extractor](../ImageInsertions/extract.py)の再生成手順は[挿入削除fixture](../ImageInsertions/README.md)を参照する。通常ビルドへC++／DLL／submoduleを加えない。

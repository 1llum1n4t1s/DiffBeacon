# 自作 TIFF fixture（CC0）

generator、画像、手書き期待値は [CC0-1.0](LICENSE.txt)。一次資料は Adobe Developers Association の TIFF Revision 6.0（1992-06-03）の [PDF原文（mirror）](https://image-js.github.io/tiff/media/TIFF6.pdf)。公式 LibTIFF の[仕様案内](https://libtiff.gitlab.io/libtiff/specification/index.html)が案内するOSGeo/ITU版は取得時403/timeoutだったため、同仕様のmirror本文を確認した。原文自体は同梱しない。

§2 pp.13–16のheader/IFD、§6 p.24のRGB必須タグ、§8 p.31のExtraSamples、§18 p.77のAssociated Alphaに基づく。II/42とMM/42、main IFD連鎖、昇順タグ、word-aligned offset、Compression=1、Photometric=2、8bit RGB/RGBA、chunky、top-left、単一strip、72dpiのX/YResolutionを明示する。最終IFDのnext offsetは0。アプリの全page対応はTIFF Baseline最低要求より広い製品契約として照合する。

| 入力 | 期待値／拒否理由 |
| --- | --- |
| two-pages-le.tif / two-pages-be.tiff | 同一画素をLE/BEで格納。2×1 [赤,青]、1×3 [緑,白,青] |
| same-first-right.tif / same-first-middle.tif | 同じ先頭。後続先頭だけ赤／青に変更、三者比較用 |
| single-page.tif | 先頭だけ。枚数差・repeat-last用 |
| unassociated-alpha.tif | ExtraSamples=2、straight赤alpha128とhidden BGRA[13,37,71,0] |
| associated-alpha.tif | ExtraSamples=1、stored RGBA[128,0,0,128]→straight BGRA[0,0,255,128]、alpha0透明黒 |
| ifd-cycle.tif / ifd-outside.tif | 2件目から先頭への循環／後続IFDがファイル外 |
| later-strip-truncated.tif / later-strip-outside.tif | 後続strip最終sample欠損／offsetファイル外 |
| too-many-pages.tif | 正常な1×1 IFD1025件、製品1024上限を超える |
| later-oversized.tif | 後続width16000001、height3の小宣言。画素確保前の容量拒否 |

初期正常7件・拒否6件の画像bytesと全asset手書き期待値は追加後も不変。期待BGRAはliteral配列で記述し、デコーダ出力から逆算しない。associated alphaは仕様の事前乗算に従い、straight値が一意な色のみを使う。unassociated alpha0は生のRGBを保持する製品契約。無効入力は画素期待値なし・exit2・成功JSONなし。後続だけ壊れた入力も先頭選択の成功で隠してはいけない。

## 再生成と固定SHA

Python標準ライブラリのみ。新しい空directoryを指定し、既存結果の上書きとlink/reparse経路を拒否する。

```powershell
python tests/Fixtures/Images/Tiff/generate.py --output E:/DiffBeacon-artifacts/local/tiff-port/fresh
```

初期 `reproduced-run5` と `reproduced-run6` の全14ファイル・219,090 bytesは一致。追加後は `artifacts/verification/image-copy-cli/tiff-format-smoke-new/reproduced-run6` と `reproduced-run7` の全29ファイル・235,301 bytesが一致。JSONはUTF-8 write_bytesでOSの改行変換を避ける。当該段階の29入力のSHA/sizeとgenerator/license SHAはexpectations.jsonへ記録する。

- manifest: `0AC09BBC0D62A821484A77A1C8F7F7B4554216E051349F2674F255A401A8D75B`
- generator: `A33CE46B456D7A21E0F0096406D7A4B60633FA4DBC7A88D257BBDD698EA918BE`
- license: `57A5A54D735E65A149F9AFA052EB444DEFDE757FD2ED647097EAEED5B5ABD607`

## 最小現行baseline

`E:/DiffBeacon-artifacts/local/tiff-port/baseline/final-summary.json` とfinal-stdout.txt/final-stderr.logに実プロセス証跡を保持。SDK10.0.401、DLL SHA `F1E7B693E31603E85B3F8C11EE4350E9BE35534F4A8C97DD34DC939DA7FC4AF5` で415bytesのtwo-pages-le.tifを同一両側へ通常`--image`で指定したところexit2、stdout空、stderr「対応する画像データを読み込めません。」。入力SHA `720E469A71B0DB1B303F21FCEAE43A93558F151435658DF3FA317ED6D4AF7E37` とDLL SHAは実行前後一致。対応後は2page取得・同一比較exit0が期待される。

初回run1はX/YResolution未記載の試作なので最終valid fixtureと区別し、最終baselineは必須resolutionタグ追加済みrun3を使用した。run3と初期最終run5の13画像bytesは同じ（最終変更はlicense provenanceのみ）。この旧baselineは上書きしない。link拒否はコード確認のみ。生成物は再現証拠として保持し、子プロセスは終了済み。

## 追加形式の代表と観測境界

| 追加入力 | literal期待値／拒否理由 |
| --- | --- |
| packbits-rgb / deflate-rgb / lzw-rgb | 2×1 [赤,青]。PackBitsは6bytes literal packet、Deflateはzlib、LZWはClear256→6literal→EOI257の9bit MSB順 |
| palette-8 | 2×1 index0/1、ushort RGB三plane Colormapで赤／青 |
| gray-white-is-zero | 2×1 bit0/1、白／黒。行末6bit padding |
| rgb-16 | 1×1 RGB ushort [65535,0,32896]→BGRA[128,0,255,255] |
| tile-padding | image2×1、tile16×16。表示外のtile paddingは全ゼロ |
| bigtiff-le / bigtiff-be | 上記2pageと同じliteral、version43/8byteoffset、2mainIFD |
| big-entry-count / big-tag-count | 64bit巨大entry/tag宣言。pixel allocation前に拒否 |
| tile-huge-dimensions | tile width巨大宣言、scratch allocation前に拒否 |
| jpeg-huge-sof | 小stripにSOF65535×65535だけを宣言し拒否。正常JPEGの期待値ではない |
| jpeg-interchange-outside / jpeg-interchange-length | Compression6、旧JPEG tags513/514のoffset／長さがファイル外。library読取前に拒否 |
| jpeg-without-scan | Compression7の1×1 Gray8 stripがSOI/SOF0/EOIのみ、SOS/scanなし。成功した空画素へ置換しない |

Scan無しJPEGの追加前manifest `42CD00F87057C50BE55498C0E47DF726EFD5071F1693EC7F9908DF6BE3F441DE` と28画像証拠はreproduced-run6/7および既存summaryに保持。追加後scanless-run1/2の全30ファイル・235,854 bytesが一致、元28画像bytesと全asset metadataも不変。217bytesのjpeg-without-scan.tifを15秒期限の実CLIへ同一両側指定すると、DLL597D…はexit0・成功JSON・1×1画素SHA e3820096…を返した（期待はexit2）。前後入力/DLLSHA不変、プロセス終了済み。反証の全command/SHA/exit/原stdout/stderrは同smoke directoryのscanless-before-*、再生成証拠はscanless-regeneration.json。製品修正後の再実行は親が担当する。

旧JPEGInterchangeFormatの追加2件は危険経路のguard追加前に実行しない。親のguard完成後にE2Eで確認する。Deflate=8の登録値は [公式LibTIFF tiff.h](https://gitlab.com/libtiff/libtiff/-/blob/df547d0ee52b70591f5192f9f01324cf50f860bf/libtiff/tiff.h) のCOMPRESSION_ADOBE_DEFLATEも照合した。

正常合計16件・拒否13件。TIFF6 §9 PackBits、§13 LZW、§15 Tiles、§8 ColorMapが追加の根拠。LZWは短いliteralコード列だけで、dictionary幅切替・predictorは未検証。[BigTIFF Design](https://libtiff.gitlab.io/libtiff/specification/bigtiff.html)に従いinline8bytes、IFD count/offset64bit、各外部valueを8byte整列。Deflate/JPEGのcompression登録は[LibTIFF形式対応](https://libtiff.gitlab.io/libtiff/specification/coverage.html)の拡張範囲で、TIFF6の旧JPEG方式とは区別する。

新規13入力だけをmanaged実CLIへ同一両側としてsmoke実行し13件一致（正常9は全page寸法と両pane SHA一致、拒否4はexit2/成功JSONなし）。実DLL SHA `597D7B8AE04307D332E2051B173FBF796DE2CAAB9B1D2B392C9163583E2B99D8` は前後一致。stdout/stderr原bytes/command/exit/inputSHA/再生成比較は `artifacts/verification/image-copy-cli/tiff-format-smoke-new/summary.json` と各ログへ保持。全E2E/GUI/nativeは親が別途実施する。この13件smoke段階では正常JPEG・planar・orientation変換・SubIFD・色管理は範囲外。

## 最終 JPEG 正常系と旧table count

追加後は正常17・拒否14、画像31件。jpeg-normal-run1/2の32ファイル・237,566 bytesは全一致、前29画像bytes/全asset metadataは不変。31入力SHAはmanifestへ記録する。

`jpeg-gray-128.tif` は自作1×1 Gray8 JPEG7。JPEGはSOI、DQT table0（64個の1）、SOF0（8bit/1×1/component1/sampling11）、DHT DC0とAC0（各length1 count1、symbol0）、SOS、entropy byte3F、EOI。DC係数0とAC EOBで全係数0、IDCT後のlevel shiftは128となり、literal期待BGRAは[128,128,128,255]。規格の一次識別は [ITU-T T.81 (09/92)](https://www.itu.int/rec/T-REC-T.81-199209-I/en)。ITU公開ページで本文は有償のため、全文取得・規格全文照合はしていない。今回指定の最小baseline構成を明示し、実画素でも照合する。

現DLL597D…で337bytes正常入力を15秒期限の実CLIへ指定しexit0、1page/1×1、両pane SHA `79DFAD351F79EF0E65A11FFF0A9ED44BF628F9390FF06B92EE4EE5E2477616EA` がliteralと一致した。前後入力/DLLSHA一致、子process終了済み。証拠はjpeg-normal-{summary.json,stdout.json,stderr.log,regeneration.json}に分離し前段階proofは保持。

`jpeg-old-table-count.tif` はSamples1/Compression6にJPEGQTables/DC/AC tags519/520/521 count5を宣言。各配列の5offsetは実際の正常Q/DC/AC table bytesへ同じ参照を繰返す。sample数を超えるtable宣言をlibrary/table確保前に拒否する期待で、guard前は実行しない。正常JPEG6は短時間で仕様適合と生decoder挙動を確定できないため追加していない。正常JPEG7の他色・係数・sampling、planar、orientation、SubIFD、色管理は未検証。

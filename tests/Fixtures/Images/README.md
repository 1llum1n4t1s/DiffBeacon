# 自作画像フレーム fixture

すべてこのリポジトリ向けに作成した画像。`generate.py` と画像データは CC0-1.0。外部画像・アプリ復号出力の転用はない。GIF/PNGはPython 3標準ライブラリの `python tests/Fixtures/Images/generate.py` で再生成し、その後WebPは `uv run --no-project --with pillow==12.3.0 python tests/Fixtures/Images/generate_webp.py` を実行する。WebPの採取環境はPillow12.3.0/libwebp1.6.0、lossless/exact、100ms、loop0。再生成順を守り、最終expectationsと全入力SHAを照合する。Python は fixture 作成時のみ使用し、配布アプリの依存ではない。

`expectations.json` は各入力ファイルの SHA-256、各ページの寸法、手で指定した全 RGBA、行詰め BGRA8888 Unpremul の全 bytes (`bgraHex`) と SHA-256 を保持する。期待値は画像デコーダーから採取していない。GIF は8色のグローバルパレット、透明 index 0、毎画素 clear code の単純 LZW。PNG は filter 0 の RGBA8 と zlib を使用する。

| fixture | 意図と期待ページ |
| --- | --- |
| same-first-left.gif / same-first-right.gif | 3×2、[全面赤,全面緑] / [全面赤,全面赤]。先頭だけの比較を検出 |
| short.gif | 3×2、全面赤1ページ。ページ数差と欠落側 null |
| disposal.gif | 3×2、4ページ。全面赤→(1,0)だけ緑→背景復元後 (2,1)だけ青→前フレーム復元後 (0,1)だけ緑 |
| disposal-1.png〜4.png | disposal の独立期待合成画素。透明画素 RGB は0。GIF第3/4ページの (1,0) は透明 |
| transparent-update.gif | 先頭行の透明・緑・透明の部分更新で、透明indexが元の赤を消さず第2ページは disposal-2.png と同じになる |
| red.png / green.png / red-wide.png | 静止画1ページ、全画素の色差と3×2対4×2の寸法差 |
| alpha-opaque.png / alpha-half.png | 同じ赤、alpha 255/128。Unpremul の赤は両方255 |
| threshold-100.png / threshold-105.png | 不透明赤成分100/105。差5は閾値4で検出、5で許容 |
| broken.gif / truncated.gif | 非画像とグローバルパレット途中の切詰め。exit 2 |
| over-pixel-limit.gif | logical canvas 4097×4096、16M画素を超える宣言。exit 2 |
| over-frame-limit.gif | 1×1、1025ページ。exit 2 |
| over-work-limit.gif | canvas 4000×4000、1px更新17ページ。全ページの復号作業は256M超過。exit 2 |

canvas-wide.gif / canvas-tall.gif は各10000×1／1×10000の小容量画像で、比較キャンバス100M画素の拒否を検証する。作業量とキャンバスの拒否は該当する診断も要求し、別の上限で早く拒否されただけの結果を合格にしない。

E2E はコピーした fixture へ実アプリ CLI を呼び、ページ番号・寸法・全画素 SHA・差分数・JSON metadata・終了コード・stderr・入力不変を検証する。欠落/寸法外画素は閾値に依存せず差分、共通範囲は最大 BGRA 成分差が閾値を超えたら1差分画素とする。64MiB+1 の入力上限 fixture は実行時に先頭GIFとゼロ埋めで作成し、runner の出力 fixture に保持する。生成元の小画像から期待値を算出し、アプリ実装を oracle にしない。

制限の選択モードも検証する。256M復号作業上限の選択モードは末尾17ページを指定し、必要な復号作業を超えた場合の拒否を確認する。

lossless-left/right.webpは赤→緑／赤→青の2ページ、lossless-transparent.webpは透明を含む4ページの固定RGBAから独立Pillowで生成する。全frameのBGRA/SHAは元のRGBAから定義し、アプリの復号をoracleにしない。CLIで全ページ、先頭選択、透明ページの全SHAを確認する。WebPの全圧縮・全blend/disposal設定、TIFF/APNG、JPEG等の不可逆圧縮画像の色一致は未確認。

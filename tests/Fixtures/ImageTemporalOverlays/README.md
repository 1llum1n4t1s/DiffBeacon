# WinIMerge 時刻依存 overlay 原本 fixture（核採取）

WinIMerge v1.0.54、commit `da639cdfaeca87aaad0eaceec509afa11ad61421` の無改変 `RefreshImages` と `AlphaBlendImages2` を、標準 clock dependency mock で実行した **77 cases／83 states**。全 pane 出力 BGRA **5,136 bytes** を独立 Python で照合した。通常 C#／Native AOT 製品には未接続。

| 原本・保存物 | SHA-256 |
| --- | --- |
| `golden.json.gz`（22,994 bytes） | `A69F8DAA4A4B69168C96AE7AAB903F590BB5C6C690E3AFAED23BD6DBA816F8F2` |
| 展開 JSON payload | `FCDDA9E2065A54E57123A62881FF636CF7B1D44CB50ACD8117E98EE126186121` |
| `../ImageRegions/reference-source/ImgDiffBuffer.hpp` | `7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28` |
| `../ImageRegions/reference-source/image.hpp` | `173CE00FE65187D34A6C7D2B10C0B45443EA6CFFBAA50FE1263BCE5840149609` |
| `probe-adapter.cpp` | `992DC2DF4BE2E22C929C8C3C346AAF207463E6AD5346EDED55FB2C7AE93110FB` |
| canonical GPL license | `D3533C10B656BEC2782600B05B471ABE3AC916E228B27929CDA1F83C49D7E7A5` |

出典は [ImageRegions README](../ImageRegions/README.md)、GPL v2以降の本文は [canonical LICENSE](../ImageRegions/LICENSE.txt)。原本 header／license の重複コピーは作らない。原本 excerpt は再生成時だけ artifacts に展開し、照合後に除去する。固定 gzip は filenameなし・mtime0・level9、JSONを展開併存しない。

## 無改変 excerpt と mock 境界

ImageOverlays と同一の excerpt を canonical bytes から抽出する。types 33–161、modes 479–487、color image.hpp 384–395、functions 1166–1218／1663–1700／1702–1964／1966–2053／2055–2125。各範囲とSHAは golden `sourceExcerpts`／採取 `extraction.json` に保持し、独立 verifier が原本から再計算する。functions SHA は `CCFBB4AC89E353D53064B69772F1D37CAD3E451792B7F6E76F01DEC3EE89503A`。

class を `temporal` namespace 内に置く。`temporal::std` は `::std` の型／関数を参照し、同 namespace の `std::chrono::system_clock::now()` だけを scripted queue とする。戻り値は本物の `::std::chrono::system_clock::time_point`。`::std` 自体への型追加、原本文字列置換、実 clock／sleep／OS操作はない。

adapter の Image は既知 preprocessed BGRA、canvas の透明ゼロ初期化、scanLine を提供する。透明度 cache は呼出し回数だけの画素非作用stub。FreeImage復号・cache同等性、原本 GUI timer の周期／時間実測、pointer／capture／clipboard、通常デスクトップ操作は未証明。分類 mask は原本観測であり、分類算法の新しい独立証明ではない。

ANIM=3 は各 blend の now を個別に読む。二者の順序は `1→0, 0→1`、三者は `1→0, 0→1, 2→1, 1→2`。source は常に preprocessed、中央は各段で byte 切捨て。ANIM alpha は epoch modulo P と整数 `a=2P/10,b=5P/10,c=7P/10` の piecewise 値で、保存 static alpha を振幅に乗算しない。show&&blink の場合だけ overlay 後に追加 now を読み、epoch modulo B が floor(B/2)未満なら MarkDiff を行わない。その後は highlight→wipe→cache。P1000/B800を既定とし、adapter period は200..8000に制限する。

## ケース・独立検証

非対称2×3 BGRA、alpha0 hidden RGB／128／255を含む二者・三者を使用する。

- P1000の0,1,100,199,200,201,499,500,501,600,699,700,701,999,1000と、B800の0,399,400,401,799,800。
- P2009の整数境界、三者のread2/read3 skew、最後blendとblink半周期cross、保存alpha .1/.9対照、show=falseでblink read無し。
- NONE/XOR/ALPHAのblink、強調alpha0/.7/1・選択、少数vertical/horizontal wipe、連続Refreshで出力非累積と原画／分類保持。

別 Python は原本の scanline/pane loop を呼ばず、piecewise alpha と各画素閉式で期待値を計算する。中央中間byte切捨て、原本観測maskの強調、絶対画素のwipe循環を独立に扱う。**1,459 checks／0 failed**、一時刻化／保存alpha振幅乗算／中央中間切捨て省略の3 mutantを実差異で検出した。

standalone adapter の18拒否ケースを2 run各々で確認：period0/-1/199/8001、負epoch、queue不足／余剰、show=false余計なblink read、pane1/4、byte256、寸法0、入力欠落／余剰。clock順序は有効な異時刻queueと閉式のpane別出力を照合する。任意の正のepoch順序そのものを不正入力とはしない。

`cases.images` は入力、`states.epochs` は各Refreshのqueue。`expected` は原本のclassificationBefore/After、rawBefore/After、baseCanvas、statesの全BGRA／寸法／base64／SHA、clockReads、wipe位置、cacheCalls。二回MSVCのpayloadとgzipは全bytes一致した。

## 再生成

既存 MSVC 14.51.36231／VS18 Community の x64 DeveloperShell と Python標準libだけを使う。通常アプリbuildへ採取器依存を追加しない。

```powershell
Import-Module 'C:/Program Files/Microsoft Visual Studio/18/Community/Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
Enter-VsDevShell -VsInstallPath 'C:/Program Files/Microsoft Visual Studio/18/Community' -DevCmdArguments '-arch=x64 -host_arch=x64' -SkipAutomaticLocation
$python = 'C:/Users/IMT/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
& $python tests/Fixtures/ImageTemporalOverlays/generate-reference.py --output artifacts/local/image-overlay-temporal-fixture/run1
& $python tests/Fixtures/ImageTemporalOverlays/generate-reference.py --output artifacts/local/image-overlay-temporal-fixture/run2
& $python tests/Fixtures/ImageTemporalOverlays/verify-reference.py artifacts/local/image-overlay-temporal-fixture/run1/golden.json.gz artifacts/local/image-overlay-temporal-fixture/run2/golden.json.gz --output artifacts/local/image-overlay-temporal-fixture/independent
```

独立成功・原本SHA保持・二回完全一致の後だけ run1 gzip を fixture へ採用する。再生成exe／obj／inc／raw JSONは全SHA・sizeの inventory と再生成手順を残して清掃し、inputs／logs／metadata／extraction／独立proof／二回gzipだけ保持する。

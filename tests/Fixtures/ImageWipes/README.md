# WinIMerge wipe 画素核 fixture（stage1）

固定 WinIMerge `v1.0.54`、commit `da639cdfaeca87aaad0eaceec509afa11ad61421` の `WIPE_MODE` と `WipeEffect` を bytes 無改変で抽出・実行した40件、400状態。全 pane の BGRA 48,000 bytes を記録する。原本GUI互換性の実測ではない。

| 対象 | SHA-256 |
| --- | --- |
| `winimerge-image-wipe-golden.json.gz`（38,282 bytes） | `0EAFB9AB3051F4781A190CE987956A5732BF7FCA1D71BAEED2E42993BC5E2A78` |
| gzip展開後のJSON payload（1,596,072 bytes） | `C33C0201152C73A4C6B88658DA083490502A4C3949EB540EE94750D09CF25192` |
| canonical `../ImageRegions/reference-source/ImgDiffBuffer.hpp` | `7071EDFA31CCC138685CA7B9FD3D41EE5E84B942FB3D8028C7E3A31C76F93F28` |
| canonical `../ImageRegions/reference-source/image.hpp` | `173CE00FE65187D34A6C7D2B10C0B45443EA6CFFBAA50FE1263BCE5840149609` |
| `WIPE_MODE` 485–487行の抜粋 | `D2DE998B0D7B3D6B1E993C1A0FDF1F94209A77069B38D3E5015E9EDE6911E40E` |
| `WipeEffect` 1966–2053行の抜粋 | `681EE6712638E09B620EFF21136034E03DEC53D9351495511EE784DAA45D8C27` |
| `probe-adapter.cpp` | `A1CE40A9471C2B8D06DD5DAB9FD81FB3C6450E2035D1D65921763D18B30C7E2A` |

原本出典・GPL v2以降は [ImageRegions README](../ImageRegions/README.md) と [LICENSE.txt](../ImageRegions/LICENSE.txt) を参照する。headerの複製は置かない。元header全体のinclude、private/public macro、原本改変を使わない。ライセンス・原本SHAと抜粋SHAはgolden内にも保存する。gzipはfilenameなし・mtime=0・圧縮level9で再現可能に保存し、展開JSONをrepoに併存させない。BCLのGZipStreamでも展開できる。

## 入力・採取範囲

2/3 pane × vertical/horizontal × 同寸法/寸法差padding × 初期位置5種の40件。共通canvasは幅3・高さ4で、入力はBGRA字句として固定し、画素をPNGやFreeImageで復号しない。alpha 255/128/0 と完全透明画素のhiddenRGBを区別する。寸法差入力は pane 順 2×3、3×4、1×2（2paneでは最初の2枚）。

初期位置は -2、0、1、軸の端、端+2。各caseの10操作は初期位置、同じ位置、端、1、0、端+2、-2、端、0、端。初回 `m_wipePosition_old=INT_MAX` を保持して原本を呼び、clamp後の position/oldPosition と全画素を各操作後に採取する。

- vertical はy、horizontalはxを境界とし、境界以降が交換領域になる。画面上の境界線描画は採取しない。
- 2paneは交換、3paneは `(left,middle,right) → (middle,right,left)`。端への戻りで元のpane順へ戻る。前進・逆進・繰返し・同位置をraw状態の連続更新で観測する。
- `GetPixelColor` は呼ばず、`m_imgDiff` の全4 BGRA byteを直接出力する。
- adapterは最小 `Image(width,height,scanLine)`、入力検証、共通canvasの左上配置とBGRA=0のpadding、核への呼出し、JSON出力だけを担う。入力画像と初期canvasを保存し、画素核以外の原本処理は実行しない。
- flip/rotation/ghost/offset/overlayRefresh/FreeImageはadapter境界の外。mode変更・down/up/escape・pointer capture/focus・OS clipboard・GUIを呼ばない。これらと旧GUIの互換確認は未実施。

## 成果物と独立期待値

`cases.images` はwidth/heightと `bgraBytes`、`actions` は要求position、`initialCanvasBgraBytes` は核呼出し前の共通canvas。`expected.states` は原本核のclamp後position/oldPositionと `processed`。processedはwidth/height、全 `bytes`、`bgraBase64`、画素SHAを持つ。原本生出力は別 `probe-output.jsonl` に保持する。

`generate-reference.py` は絶対領域maskと固定pane順による期待literalを `independent-literal-expectations.json` に記録して400状態を照合する。さらに独立の `verify-reference.py` は字句で列挙した画素番号領域とpane順から全画素を計算し、標準libだけでbase64/bytes/SHA/寸法/状態数と2回のgolden bytesを照合する。原本のincremental swapとoldPosition分岐をPythonに移植しない。

採取前の [failure-contract.md](failure-contract.md) が想定失敗と期待結果を定義する。2026-10-03の採取はMSVC build/probeともexit=0、2回の展開golden bytes・gzip bytes一致、独立検証1,442項目成功・0失敗。成果物は `artifacts/local/image-wipe-fixture/{run1,run2}/` と `artifacts/local/image-wipe-fixture/independent-assertions.json`。原本入力・plain probe入力・JSONL・log・metadata・抽出SHA・期待literal・goldenを保持する。exe/obj/原本抜粋incは使用後削除し、再現時に生成する。

## 再現（既存MSVC・Python 3のみ）

```powershell
Import-Module 'C:/Program Files/Microsoft Visual Studio/18/Community/Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
Enter-VsDevShell -VsInstallPath 'C:/Program Files/Microsoft Visual Studio/18/Community' -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null
python tests/Fixtures/ImageWipes/generate-reference.py --output artifacts/local/image-wipe-fixture/reproduced1
python tests/Fixtures/ImageWipes/generate-reference.py --output artifacts/local/image-wipe-fixture/reproduced2
python tests/Fixtures/ImageWipes/verify-reference.py artifacts/local/image-wipe-fixture/reproduced1/winimerge-image-wipe-golden.json.gz artifacts/local/image-wipe-fixture/reproduced2/winimerge-image-wipe-golden.json.gz --output artifacts/local/image-wipe-fixture/reproduced-assertions.json
Get-FileHash tests/Fixtures/ImageWipes/winimerge-image-wipe-golden.json.gz
Get-FileHash artifacts/local/image-wipe-fixture/reproduced1/winimerge-image-wipe-golden.json.gz
```

使用compilerはMSVC `14.51.36231/bin/Hostx64/x64/cl.exe`。採取metadataに実際のINCLUDE/LIB/compile引数を保存する。`Launch-VsDevShell.ps1` のインストール探索は当環境でdescriptionのJSON escape変換に失敗したため、上記のDevShell DLL直接Importと明示VsInstallPathで復旧した。新しい依存の取得やインストールはしていない。

固定版の明示更新時だけ採取コマンドに `--golden tests/Fixtures/ImageWipes/winimerge-image-wipe-golden.json.gz` を付ける。通常build/E2EへC++・DLL・FreeImageを追加しない。managed実装・実アプリE2E・HTML・Native AOTとの照合は親の統合工程。

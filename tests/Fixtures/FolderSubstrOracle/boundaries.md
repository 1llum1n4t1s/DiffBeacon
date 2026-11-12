# 固定 native Substr oracle の境界

固定 upstream WinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6` の無改変原典を original に保持。SubstrFunc 1871–1911、getAsString 51–58、applyToScalarOrArray 1693–1704、ToStringValue 2315–2357 を byte-identical に抽出し、明示型 literal/throw node adapter から直接呼ぶ。MSVC x64 C++20。採取専用で通常 .NET build の依存ではない。GPL 原典ライセンスは original/COPYING。

268 cases: Int64 極値・byte境界・省略/不正型・None・混在/入れ子配列・引数評価順/例外を含む。開始位置とlengthはsourceに先行し、startが不正でもlengthを評価する。source配列だけを浅くmapし、入れ子は文字列化する。null argument vectorは原典がdereferenceするため呼ばない。arity不正のbody結果は公開parserの受理条件を意味しない。

入力/結果の `type: string` の `value` は Unicode text ではなく lowercase hex の生bytes。埋込みNULと不正UTF-8を保持する。goldenはnative出力をそのまま固定し、verify.pyは独立したPython byte-slice modelと全型・値・traceを照合する。UTF-8分断の結果も正しいnative結果であり、置換復号や文字位置sliceへの変更は禁止。このfixtureはmanaged stringへの全結果投影を保証しない。managedがUnicode stringしか保持できない場合、不正UTF-8を生bytesで保持する設計か明示的な非互換境界の判断が必要で、これが決まるまでnative完全同等のSubstr実装を成立と扱えない。正当UTF-8結果だけならstrict decodeで投影できるが、後続のLen/比較/連結におけるbyte意味論まで別途確認が必要。timestamp/regex/filecontentはadapter stubが実行を拒否し、本ケース集合に含めない。浮動小数は有限1.25のみを採取し、他のformat/非有限値は未検証。

parser、FunctionNode constructor、Optimize/constant-folding、folder loader/scanner、managed evaluator、public .flt、GUI/CLIは実行していない。ここから製品動作を主張しない。

## 再採取・独立reader

Windows x64 MSVC developer shell、Python 3.13以降。`python`がStore aliasの場合は実python.exeを指定する。未使用Dドライブ外部runだけを受け付け、TEMP/TMP/obj/exe/logをrun内に揃える。

```powershell
python -B -X utf8 tests/Fixtures/FolderSubstrOracle/reproduce.py --output <作業フォルダー>\DiffBeacon\<unique>\run1 --compiler cl.exe
python -B -X utf8 tests/Fixtures/FolderSubstrOracle/reproduce.py --output <作業フォルダー>\DiffBeacon\<unique>\run2 --compiler cl.exe
python -B -X utf8 tests/Fixtures/FolderSubstrOracle/verify.py
python -B -X utf8 tests/Fixtures/FolderSubstrOracle/verify.py --run <作業フォルダー>\DiffBeacon\substr-oracle-20261020-r1\run1 --run <作業フォルダー>\DiffBeacon\substr-oracle-20261020-r1\run2
```

初回receipt hashをcapture-index.jsonへ固定。再採取のrawを既存goldenと照合してからcapture-indexとmanifestを明示更新する。readerはsource/Git blob・全fixture file・compiler/exe/include・引数・stdout/stderr・PID/birth・終了handle・exit・EOF・時刻・二つの独立processを検査する。既定readerだけでは外部run証拠を確認した扱いにしない。receiptは観測記録であり暗号署名された環境証明ではない。

初回raw証拠は `<作業フォルダー>\DiffBeacon\substr-oracle-20261020-r1`。再検証用に保持し、別runの完全照合とcapture-index移行後に旧runを清掃できる。今回削除/清掃は行わない。

# フォルダーフィルター ToLower / ToUpper 原典fixture

固定WinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6` の原典Git blobを `original/` に保存する。`provenance.json` のsize・SHA-256・Git blob IDをビルド前に照合する。WinMergeのGPLは `original/COPYING`、PocoのBoostライセンスは `original/Poco-LICENSE`。

原典の `ToXFunc`・`getAsString`・`applyToScalarOrArray`・`ToStringValue`・登録行・arity検証、および `unicoder.cpp` のWindows `LCMapStringAuto`・`toLower`・`toUpper`・UTF8/UTF16橋渡しを無改変抽出する。実際のPoco `UnicodeConverter::convert`・`TextIterator`・`UTF8Encoding` も固定原典から無改変抽出する。adapterは型接続と明示AST・評価throwを提供し、独自UTF8 decoderや.NET casingは使わない。

`golden.ndjson` は70件（62成功・8評価/arityエラー）。成功はtyped resultと最終文字列をUTF-8小文字hexで、評価は引数indexのtraceで固定する。正常文字列・ASCII・Turkish I・ß/ẞ・sigma・結合記号・補助面・NUL・bool/int/double/None・regex/fileのnative文字列化・浅い配列/入れ子・空配列・arity 0/2/null・評価throw・不正UTF8断片を含む。

`cases.json` の `type=bytes` はvalueがraw UTF8 hex。`projection=public-flt-candidate` は公開式への候補、`public-substr-byte-candidate` はSubstrで切ったbyteを公開式へ渡す候補、`native-only` は内部値の採取、`arity-boundary` は登録時の境界を表す。候補という印は製品E2Eの合格を意味しない。parser/Optimizeの実行・Poco timestamp・XP fallback・非Windows・他localeは未検証。

Windows 10.0.26300、user/system locale ja-JP、thread LCID 1033、ACP/OEMCP 932で独立した2つのMSVCビルドを実行した。全stdoutはbyte一致、SHA-256 `8b770bbb1abc5dbfd0b3fe4323b2eaf5827363bcf5d505ae36d0dbb68618cca8`。詳細な原典範囲・環境・PID/誕生時刻・終了/EOF・raw証拠への参照は `capture-index.json` に記録する。raw build/run/log/receiptはリポジトリ外の指定workrootにのみ保持する。

`LCMapStringEx(nullptr, LCMAP_LOWERCASE/UPPERCASE)` を実行するため、.NET invariant casingの期待値へ置換しない。今回のWindowsではİ/ı・ß/ẞ・ligatureはそのまま、Σは語尾でもσ、ςの大文字はΣ。Deseretは変換しAdlamはそのまま。不正byteの `a9`/`c3`/`e282`/`f09090`/`c080`/`eda080`/`f4908080`/`e24142` は各U+FFFF（`efbfbf`）、`8081` は2個、`ff4142` はU+FFFFとab/ABになる。`00c300` はNULとU+FFFFとなる（C3が後続NULも消費）。全malformedケースは両runで例外・faultなし。

配列は一段だけ変換する。入れ子配列は原典ToStringValueで文字列化し、その文字列を変換する。Noneの型はNoneのまま、最終ToStringValueは `none`。float文字列はnative `std::to_string` の `1.250000` を採取する。

## 再現

Python 3とMSVC x64を使用する。C++はfixture採取専用で通常製品buildの依存に加えない。`--output` は未使用のDドライブ配下・repo/CODEX_HOME外を指定する。reparse ancestor・出力先既存・空き1GiB未満は拒否し、TEMP/TMPも各outputへ集約する。

```powershell
python tests/Fixtures/FolderCasingOracle/reproduce.py --output D:/Claude/DiffBeacon/<fresh-run>/native-a --vcvars <vcvars64.bat>
python tests/Fixtures/FolderCasingOracle/reproduce.py --output D:/Claude/DiffBeacon/<fresh-run>/native-b --vcvars <vcvars64.bat>
python tests/Fixtures/FolderCasingOracle/verify.py
python tests/Fixtures/FolderCasingOracle/verify.py --actual D:/Claude/DiffBeacon/<fresh-run>/native-a/reference.stdout
```

各outputの親は事前に検証して作成する。`verify.py` は製品やadapterをimportしない独立readerで、manifest・全typed bytes・trace・件数・順序を照合し、値/型/trace改変と切詰めを拒否する自己検証も行う。locale依存の別Windows出力が一致しなければ差を報告し、固定goldenを書き換えない。
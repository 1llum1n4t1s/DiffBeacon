# Fixed native Poco Glob malformed-byte boundary

固定 WinMerge commit `6780924ba35f7f55af0f6f14dd302561175830a6`。Glob/TextIterator/UTF8Encoding/Unicodeのsource/headerとPoco LICENSEをGit blobそのままoriginalへ保持。provenance.jsonに全Git blob/size/SHAを固定する。PCRE Unicode表はconsumers/reference-source.zipの固定原典を共有し、そのarchive SHAも固定する。PCRE2 amalgamationを各runで独立compileする。

756 cases: 元Substr oracleのmalformed出力33個すべてに、感度2種×11ケースを割り当てる。raw literal pattern、*、?、前後/両側wildcard、空pattern、high-bit不正pattern、[と末尾backslashの構文不正、A*Zによるmalformed中間入力を確認する。空subject/pattern、有効ASCII/Unicode/emoji/NUL、set、異なる不正列同士の対照30ケースを加える。

Glob constructor/match/matchAfterAsterisk/matchSet、TextIterator class/全native body/inlines、UTF8Encoding queryConvert/sequenceLength/isLegal/charMap、Unicode class/properties/isUpper/toLowerを無改変byte範囲で抽出する。TextEncodingとUTF8Encodingの型接続はadapterの最小宣言。元native query/sequence関数bodyとvirtual呼出しは保持し、filesystem探索APIや非UTF8 encodingを実装した扱いにはしない。Poco SyntaxExceptionの例外型接続だけstd::runtime_errorを用いる。文字/UTF8/Globの本体をstubに置換していない。

native TextIteratorは不正/不完全UTF8をcode point -1として返す。incrementはUTF8Encodingの宣言幅に従って進むため、truncated leadの残りや不正continuationを含む列が一つのinvalid tokenになることがある。Globはこのtoken列を比較し、raw byte equalityとは異なる。固定対照では `ff` subjectと`fe` pattern、`f8` subjectと`ff` patternが感度にかかわらず一致する。*はmalformedを含む任意列を受け入れ、?は一つのiterator tokenを受け入れる。原典の不正UTF8を全拒否する経路ではなく、replacement Unicode stringを作っているわけでもない。

case-insensitiveはUnicode::toLowerを使い、Poco ASCII lowerやCRT localeとは別経路。Unicode::propertiesには負のchを事前拒否する検査がなく、iteratorの-1もPCRE table索引計算へ進む。この固定build/ケースでは独立modelのinvalid sentinel -1保持と結果が一致しnative faultは0だったが、負値の意味論をUnicode scalarの正規仕様として保証しない。別compiler/別PCRE表/別Poco版への一般化は未検証。TCHAR/Windows文字列/OS localeへの変換は今回起きない。

各ケースを別native processで実行し、2回の独立build×756ケースで1512 native case processesを記録する。native faultをfalseへ変換せず、exit code、stdout/stderr全bytes、PID/birth、保持handle終了、EOF、時刻を保存する。今回の最終2runはfault0、全756 normalized raw出力が一致。構文不正はPoco SyntaxExceptionメッセージとして記録し、UTF8 tokenの不正とは区別する。

入力はsubjectHex/patternHex。glob-golden.ndjsonは各caseのexitCode/stdoutHex/stderrHexを固定し、stdoutにはtyped bool結果/status/errorが入る。verify.pyはcollector/nativeをimportせず、独立したbyte iteratorとbounded Globアルゴリズムで全値/例外を確認し、source/Git blob、PCRE/archive/include/compiler/object/EXE、正確なcompile/run引数、全process証拠とraw bytesを再照合する。PythonのUnicode lowerは今回の有効制御文字集合だけに使用し、全Unicode case table同等性を主張しない。

Windows x64 MSVC developer shellで `python -B -X utf8 tests/Fixtures/FolderStringByteObserversOracle/glob/reproduce.py --output <未使用D外部run> --compiler cl.exe` を2回実行する。完全reader:

```powershell
python -B -X utf8 tests/Fixtures/FolderStringByteObserversOracle/glob/verify.py --run <作業フォルダー>\DiffBeacon\string-bytes-glob-20261020-r1\run3 --run <作業フォルダー>\DiffBeacon\string-bytes-glob-20261020-r1\run4
```

最終raw/receiptは上記run3/run4へ保持。run1は最小adapterのPath forward宣言不足だった初回compile失敗証拠として保持し、最終成功証拠とは区別する。別runへ完全移行後に清掃可能。今回削除/清掃なし。parser/optimizer/.flt受理/managed実装/folder scanner/GUI/CLI/全Glob featureを検証した扱いにはしない。

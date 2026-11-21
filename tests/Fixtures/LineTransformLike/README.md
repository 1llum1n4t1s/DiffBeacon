# like の独立したPoco Glob原典fixture

## adapter実装前に固定する失敗条件

- 全文一致を部分一致にする。空subject／空patternの結果を混同する。
- `*`の0文字以上、`?`のUTF-8 codepoint 1個をbyte／UTF-16単位／書記素へ変える。BMP・補助面・結合文字を含める。
- `[SET]`／`[!SET]`／範囲／escapeした特殊文字を正規表現の意味へ変える。
- case-insensitive時のPoco Unicode::toLowerを文化依存や複数文字casefoldへ変える。
- regex句読点と`^`／`$`を正規表現の制御文字にする。
- newline／slash／先頭dotにfilesystem globの制限を追加する。
- 不正setをsubjectに関係なく先行拒否する、または終端backslashの入力依存性を消す。

次はGlob.cppから読んだ静的期待値であり、native採取で確認するまではsource-derived:
`a`と`[a`はclosing-bracket例外、`b`と`[a`はfalse、`b`と`[!a`はtrue。
空subjectと空patternはtrue。空subjectでは不整合`[`や末尾`\`を早期終了してfalseにできる。

99件を独立compile・独立processで2回採取し、stdout全bytesが一致した。
上記の静的期待値はすべてnativeで確認済み。`?`は猫／😀にはtrue、`e`＋結合acuteにはfalse、
`??`は同じ結合文字列にtrue。newline／slash／先頭dotは通常の文字として一致する。
case-insensitiveではÄ／Σ／Я／Deseretは小文字へ一致し、final sigma／dotted I／dotless I／
Kelvin sign／long s／titlecaseは今回の対になるpatternへ一致しない。ẞとßは一致し、ßとSSは一致しない。
これらは固定原典のUnicode propertiesとothercase tableによる実測であり、一般的なcasefoldの主張ではない。

## 採取と境界

固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の原典を使う。
`Glob.cpp`のconstructor／destructor／match全overload／matchAfterAsterisk／matchSetはbyte単位で無改変抽出。
`Glob.h`、`TextIterator.h/.cpp`、`UTF8Encoding.h/.cpp`、`Unicode.h/.cpp`とPCRE2 Unicode tableを固定する。
filesystem列挙は呼ばない。Foundationの宣言・assert・例外型、TextEncodingの純粋virtual宣言、未使用isAのString接続だけadapter。
文字の走査・UTF-8復号・Unicode小文字化・Glob照合は原典を実行し、Pythonや.NETで期待値を計算しない。
入力はvalid UTF-8文字列と明示caseSensitiveで、旧likeが使う0／GLOB_CASELESSだけを渡す。
旧parser／AST／配列lifting／none／not／optimizer／Content／製品UIの同等性はこのfixtureの対象外。
不正UTF-8、locale依存、全Unicode casefold、GLOB_DOT_SPECIAL、filesystem列挙、計算量上限は未検証。
C++／MSVC／PCRE2は検証専用で製品依存へ追加しない。

## 再現

`python -B tests/Fixtures/LineTransformLike/capture.py --output <新しい外部Temp> --compiler <cl.exe> --vcvars <vcvars64.bat>`
で別processへ2回compile・実行する。出力先はrepository／CODEX_HOME外のTempのみ。
`python -B tests/Fixtures/LineTransformLike/verify.py` がplain Pythonの独立reader。
固定source／capture／goldenのSHA、全ZIP entryのCRCとbytes、抽出範囲、2回のstdout全bytesを検証する。
`--actual <NDJSON>` では全ID・status・bool結果を比較する。製品の検証は実施していない。
BSL-1.0とPCRE2の原ライセンスをsource archive内へ保持する。固定fileは`* -text`で保護する。
GPL-2.0の`LICENSE.md`／`Src/COPYING`、PCRE2 source header内のBSD条件とUnicode noticesも保持する。
`source-manifest.json`は全60原本のraw Git blob ID／SHA-256／sizeを固定する。
`native-proof.zip`は2回の原典抽出span・実compile入力・全stdout／stderr・process receiptを保持する。
EXE／OBJはproofへ含めず、各receiptにcompilerとEXEのSHA・PID・birth time・終了とEOFを記録する。
原典の未使用filesystem処理はcompileしない。例外はPoco同名の接続型へ置き換え、本文と継承階層は比較しない。

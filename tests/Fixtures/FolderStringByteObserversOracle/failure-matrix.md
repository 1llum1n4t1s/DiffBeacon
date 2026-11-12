# 先に整理した失敗条件と検証範囲

- 元Substr oracleの不正UTF-8 33出力をID/hexつきで保持し、各出力を AllEqual、runtime == / != / < / <= / > / >=、Len、Strlen、StrFind/StrCount、二度目のSubstr、nested array stringificationへ渡す。
- 異なるraw byte列がreplacement decodeでは同じになる場合を対照する。文字化け置換での同一化を期待値へ混入しない。
- AllEqualは厳密variant型で再帰比較、firstがarrayなら残りの引数を読まない。scalar引数の不一致で後続評価を止める。throw probeでtraceを確認。
- runtime binary operatorは固定computeとBinaryOpNode::Evaluateを直接実行。case-sensitive std::stringとcase-insensitive Poco icompareの両方を検証。後者はMSVC signed charの比較順があり、非ASCIIに関してunsigned byte orderingと同一とは限らない。
- Len/Strlenはfixed unicoder.cppのstringlen_of_utf8/Utf8len_fromLeadByteを呼ぶ。strict decoderではなく先頭byte幅だけで進み、truncated leadやcontinuation byteを拒否しない。空・NUL・有効UTF-8と対照する。
- StrFind/StrCountはbyte位置/非重複一致、Poco ASCII小文字化。None/不正needle/空needleと評価順を確認する。
- Substrはbyte切出し。入れ子配列のToStringValueは構造用ASCIIを加えてraw bytesを保つ。NoneのgetAsString拒否とToStringValueのnone文字列を区別。
- null args/arity不足で原典がdereferenceする経路は呼ばない。timestamp/regex/FileContent/locale/TCHAR変換はadapterが実行拒否するstubであり、本oracleの実装済み変換に含めない。
- 原典hash、固定入力、compiler/EXE/include、2process独立性、exit/handle/EOF、typed出力/traceのどれかが不一致なら失敗。
- parser/optimizer/loader/scanner/managed/公開flt/GUI/CLIや全native機能の同等性は主張しない。

# 採取前の失敗条件と期待結果

- 原典・ライセンスのSHA/size/Git blobが固定出典と違う場合は採取を拒否する。
- 空文字、comma除去後の空文字、末尾空白・suffix・埋込みNULは全消費にならずNone。先頭ASCII空白はMSVCの変換に従う。
- bool/int64/double/None入力はstringではないためNone。arrayは一段だけmapし、入れ子arrayはNoneになる。
- int64上下限は成功、境界外と整数への小数・指数・hexはNone。ToDoubleのhex/NaN/inf/underflow/overflowはMSVC実測を固定する。
- ToNumberは文字列にdot/e/Eがある場合だけdoubleを選択する。integer overflow時のdouble fallbackはしない。
- 引数0はNoneで評価traceなし。余分な引数は関数本体では評価しない。登録は全て1引数で、公開constructor/parserの実測とは区別する。
- 引数評価例外は変換のcatch外なので伝播する。先頭引数は1回だけ評価し、出力traceで照合する。
- native adapterは別processで2回実行し、終了code、EOF、stdout SHA/bytesおよびgolden一致を検査する。
- 独立readerはケースID/件数/trace/型/bit pattern/文字列化/原典SHA/manifestを検査し、欠落・重複・改変を拒否する。
- runtime/compilerの全出力は指定D workrootへ限定する。repo内出力、リンク祖先、既存runへの上書きは拒否する。

# 採取前の失敗条件
- 大小文字・locale差・複数code point化・補助面・結合記号の差を全UTF-8 byte照合する。
- Noneと空文字の混同、bool/数値のnative文字列化、配列の再帰変換を区別する。
- arity違反は引数評価前に失敗し、正常arityのthrowは評価traceを持つ。
- nativeビルド/起動失敗、stderr、部分stdout、二重採取不一致は成功にしない。
- parser/Optimize/Poco timestamp/XP fallbackは採取対象外。public候補は製品E2E合格を意味しない。

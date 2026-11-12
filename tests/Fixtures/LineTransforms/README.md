# 一般行変換の旧C++原典fixture

`cases.json` の86ケースは `original/` の固定Git blobから無改変で切り出した
`VectorLineDataProvider`、`ParseColumnRanges`、`columnRangeFunc`、`IfFunc`、
`LineField`、`LineNumberField`、`ColumnField`、`ColumnCountField`、`lineAt`、
`lineOffsetAt`、`TrimFunc`、`ToStringValue`、`ConvertString`、`ApplyFilterExpressionsToLines` で採取した。
`capture.json` は MSVC コマンド、原典の行範囲、実PID・Windows birth FILETIME、
実終了コード0、stdout/stderr EOF、両ストリームSHAを保持する。
stdoutの原bytesは `golden.ndjson`、空stderrは `reference.stderr`。
GPL v2原典のライセンスは `LICENSE.txt` と `original/COPYING`。

## 採取範囲と接続の境界

- 旧WinMerge全体・生成parser・Poco・MFC・ファイルdecoder/writerをbuildした結果ではない。
  必要な純粋関数だけを検証専用C++へincludeする。C++を製品依存に追加しない。
- `cases.json` は製品へ渡す `expressions` と同じ意味の明示 `ast` を併記する。
  原典parserは実行しない。ASTの構築、整数等値比較、prefixからprovider paneへの接続、
  target maskごとのgroup実行は `adapter.cpp` の型・呼出接続である。
  元 `FilterParser.y` と `FunctionNode::Optimize` を丸ごと原典保存し、接続根拠として固定する。
- scalar値の型接続、Trimのscalar helper接続、例外からstatusへの包装もadapter。
  文字列化は `ToStringValue` と `ConvertString` の無改変本体を呼び、noneは `"none"`。
  この本体をcompileするためTimestamp/Regex/FileContent型をbindingするが、fixtureは
  それらの値を構築しない。Poco日時stubは呼ばれたら失敗する。
  array・double・Poco・Unicodeの汎用変換を検証済みとしない。
- `IfFunc` のbool-only判定と選択枝だけの評価は無改変関数本体。
  literal ColumnRangeのcompile段階は、adapterが全子ASTを先に訪れ
  `ParseColumnRanges` を実行する接続で再現する。旧Optimize全文の実行ではない。
  未選択literal不正rangeのcompile拒否と、動的未選択文字列の非評価を区別する。
- 原典providerはpaneを区別しない一つの文書を持つ。RightColumnRangeのprefixと
  target maskを別々に与える3ケースでこの境界を確認する。
  Rightは原典の `side=-1` と単一providerの `dirs-1=0` をadapterで接続する。
  `providerPane` はこの単一providerへの呼出番号であり、三者GUIのpane選択検証ではない。
- 原典providerの `GetColumns` をforwardする記録proxyが `selections` を採取する。
  各項目は0-based `row`、`providerPane`、0-based選択列 `columns`、外側EOLなしの
  RAW `keyHex`。無改変原典関数で決定した全列順・dedup・除外結果を保存する。
- plainでも指定delimiter/quoteで列数を走査し、選択非空ならwholeline、空なら空を返す。
  delimiter/quoteは明示入力で、旧拡張子判定による既定値の採取ではない。
- `lines` は呼出元が分割済みのUTF-8行で、外側CR/LF/CRLF/EOFを含む。
  tableのESC・引用内CRLFは `DiffTextBuffer.cpp:485-487` のESC倍化・ESC r/nに従い、
  callerがescape済みの入力を一件供給する。
  callerのescape実装・文字コード/BOM復号・物理行分割・ファイル保存を検証しない。
- 式がCR/LFを生成して本文行数を変える旧fileIOとrow mappingは未解決。
  正常goldenへ混ぜず、製品側の拒否と残る移植範囲を別に検証する。
  C++整数overflow等の未定義入力、GNU残余blockの分類もこのoracleの範囲外。

同じmask groupは行外／式内で即時置換する。前行は既変換、後行は未変換として参照する。
`group-same-mask` のkeyは `b\n,b\n`、`group-mask-boundary` は
`changed2\n,changed2\n` となり、この実行順の違いを識別できる。

## 再現

MSVC x64 developer shellで、リポジトリ・CODEX_HOMEの外にある作業別Tempを明示する。
未使用の出力先を使い、実行後の一時物は作業契約のごみ箱手順で清掃する。

```powershell
python tests/Fixtures/LineTransforms/reproduce.py --output "$env:TEMP\Codex\DiffBeacon\<run-id>\reference"
python tests/Fixtures/LineTransforms/verify.py
python tests/Fixtures/LineTransforms/verify.py --actual <product-results.ndjson>
```

`reproduce.py` は出力先のrepository/CODEX_HOME内と禁止名を拒否する。
ソース抽出はraw bytes、Git blobはSHA-1、原典・各固定fileはSHA-256で照合する。
元working treeはCRLF checkoutなので、採取時にHEAD raw blobとのLF正規化一致も確認した。
固定fileは `.gitattributes` の `* -text` で保護する。属性とblobは初回登録時にも再照合する。

製品結果は全86 IDを一行ずつNDJSONで返す。各行は `id`, `status` (`ok`/`error`),
`errorPhase` (成功は空、`compile`/`evaluate`), `lines` を持つ。
各lineは原text/keyの全UTF-8 bytesを小文字hexで表した `originalHex`, `keyHex` と
`originalEol`, `keyEol` (0 EOF/1 LF/2 CR/3 CRLF), 原文の `columnCount`。
`keyHex` は外側EOLを含む。失敗時も原文保持を照合し、診断文の言語は比較しない。
製品が列選択traceを返す場合は `selections` 全件も比較し、
`--require-selections` で省略を拒否できる。独立readerはC#をimportしない。

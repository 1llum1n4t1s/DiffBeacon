# 配列一般行変換の旧C++原典fixture

固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の原典Git blobを
`original/` に無改変で保存し、無改変の関数本体を `reproduce.py` で切り出す。
`adapter.cpp` の明示AST接続で採取した `golden.ndjson` は全90件、88成功・2評価エラー。
元の90件goldenは公開Plan向けに書き換えない。原典・ライセンスは `provenance.json` と
`original/COPYING`、全固定fileのSHA-256とsizeは `manifest.json` で確認する。
`capture.json` は実PID・birth FILETIME・終了・stdout/stderr EOFとraw SHAを記録する。
`report.txt`・reader-selfcheck・recompile-proofは採取時の履歴であり製品の検証結果ではない。
既存 `LineTransforms` の86件は別fixtureとして維持する。

## 原典と公開Planの境界

Array/At/AnyOf/AllOf/AllEqual/AndEach/OrEach/NotEach/If/IfEach/Choose/ChooseEach、
算術・比較・scalar lifting・Len/Trim・最終ToStringValue・UTF-8・外側EOL・target groupを照合する。
式は `@cs @noopt` により大文字小文字区別・無最適化を明示する。旧parser、arity検証、
旧C++ Optimizeの実行は採取していない。Optimizeについて原典コードから読み取れる
観察は実行検証と区別する。doubleリテラル/Poco/date/regex/FileContentの構築、
整数overflow、数値doubleとstrict AllEqualの追加境界はこのoracleの範囲外。
adapterの未使用tostring opは製品のToString関数登録を示さない。
極端なindexは旧lexerの整数literal範囲を広げず、原典ToInt(string literal)で接続する。

内部ValueEngineは全90件を原典と照合する。公開Planは89件を同じ原典へ照合する。
唯一の例外 `arrays-value-eol-values` は実CR/LFを含む文字列literalとAST textが一致し、
原典ValueEngineでは正常なkeyを生成するが、公開Planは物理行対応を保持するため拒否する。
`--public-plan` はこの1件だけ `status=error`、`errorPhase=evaluate`、全行の
`keyHex=originalHex`・`keyEol=originalEol` を要求する。全90 ID・原文bytes・EOL・
columnCountの照合を維持し、欠落・skip・別の差分を許可しない。診断文の言語は比較しない。
旧fileIOが生成CR/LFを物理行へ分割する処理とrow mappingは移植・検証済みとしない。
Ifはlazy、IfEachはeager。原典の2評価エラーはIfEachの非選択枝のzero division。

## 再現と独立reader

MSVC x64 developer shellで未使用の外部Tempを明示し、終了後は共通ごみ箱手順で清掃する。
C++とMSVCは採取専用で通常.NET buildの依存ではない。

```powershell
python tests/Fixtures/LineTransformArrays/reproduce.py --output "$env:TEMP\Codex\DiffBeacon\<run-id>\reference" --compiler <cl.exe>
python tests/Fixtures/LineTransformArrays/verify.py
python tests/Fixtures/LineTransformArrays/verify.py --actual <internal-value90.ndjson>
python tests/Fixtures/LineTransformArrays/verify.py --actual <public-plan90.ndjson> --public-plan
```

独立readerはC#をimportせず全bytesを小文字hexで照合する。
`lines` の各要素は `originalHex`, `keyHex`, `originalEol`, `keyEol`, `columnCount`、
EOLは0 EOF / 1 LF / 2 CR / 3 CRLF。`keyHex` は外側EOLを含む。
選択traceがあれば全 `selections` を比較し、`--require-selections` は省略を拒否する。
公開Plan集計の `okCases=88`・`errorCases=2` は `caseCountBasis=fixedCppOracle` の原典件数。
`publicPlan=true`・`oracleMatchedCases=89`・`publicBoundaryCases=1` は公開境界の照合件数。
公開結果そのものは87成功・3評価エラーになる。
固定fileは近い `.gitattributes` の `* -text` でcheckout時の変換を禁止する。
再採取出力はrepository/CODEX_HOME・禁止名・linked ancestorを拒否する。

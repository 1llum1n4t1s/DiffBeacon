# StrCount / StrFind の旧C++原典fixture

固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の原典14fileを
`original/` にraw Git blobのまま保存する。`adapter.cpp` と `reproduce.py` は
旧関数本体 `FilterExpressionNodes.cpp:2923–3031` を無改変で切り出し、明示ASTへ接続する。
`golden.ndjson` は全68件・74行、62成功・6評価エラーの関数本体oracle。
公開API向けに原典goldenを書き換えない。SHA・sizeは `manifest.json`、原典commit・
blob・SHAは `provenance.json`、GPLとPocoのライセンスは `original/COPYING` と
`original/Poco-LICENSE`。初回と再採取のreceiptは実PID・birth・保持handle・終了・EOFを記録する。

## 検証範囲と公開コンストラクター境界

StrFindは0始まりの一致回を指定し、その一致の開始位置をUTF-8 byte offsetで返す。
第3引数を省略したときは0回目。両関数とも一致を重ねて数えない。
none、空needle、型違い、一致回の省略・負値・極値・型違い、@cs/@ci、target maskを固定ケースで照合する。
全式に `@noopt` を明示し、caseSensitiveは式ごとの `@cs` または `@ci` に対応する。
@ciでは固定Pocoの実Ascii class・inline関数・properties表・toLower templateを使用する。
Foundation_APIの空定義とUInt32のuint32_t接続は採取用の型binding。Pocoを製品依存へ追加しない。

公開Planは関数本体oracleの62件と同じ結果を要求する。
残り6件 `search-strcount-arity-{0,1,4}` と `search-strfind-arity-{0,1,4}` は
関数へ直接接続した原典ASTでは正常なnoneになる。一方、旧FunctionNodeの登録表
`FilterExpressionNodes.cpp:3835–3836` はStrCountを2引数、StrFindを2〜3引数とする。
同ファイル4221–4282のconstructorは範囲外でInvalidArgumentCountErrorを投げ、
`FilterParser.y:139–190` がcompile errorへ変換する。これは固定原典コードに基づく境界で、
旧C++constructor・parserの実行を採取した証拠ではない。

`--public-plan` はこの6件だけ `status=error` / `errorPhase=compile` を要求し、
全原文bytes・EOL・columnCountと `keyHex=originalHex` / `keyEol=originalEol` を照合する。
readerは固定ソースSHA確認後、登録表・constructor・parserの該当箇所、6件のAST引数数、
元goldenの正常noneを検査してから投影する。欠落ID・skip・その他の差分は許可しない。
公開結果は56成功・12エラー（6 compile＋6 evaluate）。生成CR/LFの別境界はこのfixtureにない。

旧parser・Optimizeは実行していない。原典4391–4404では定数StrCountの結果を
直接get<int64_t>する経路と、整数結果だけを置換するStrFindが見える。
空needle等のnoneでの型アクセス失敗はソースからの観察で、旧最適化実行の実測ではない。
既定Optimizeと@nooptの同等性、未採取のdate/regex等の構築は検証済みとしない。

## 原本bytesと再現

保存した14原本は全て固定blobとbytes一致。ローカルcheckoutとのraw一致は1/14、
CRLF→LFだけの正規化後一致は14/14。Poco4fileのraw一致は0/4、正規化後一致は4/4。
raw照合と正規化照合は `provenance.json` で別々に記録し、保存原本を正規化しない。
近い `.gitattributes` の `* -text` で原本とgoldenのcheckout変換を禁止する。

MSVC x64 developer shellで未使用の作業別外部Tempを明示する。通常.NET buildには不要。
再採取はrepository/CODEX_HOME・完全folder名artifacts・linked ancestorを拒否する。
終了後は共通のごみ箱経由清掃で今回分だけを除去する。

```powershell
python -B -X utf8 tests/Fixtures/LineTransformSearch/reproduce.py --output "$env:TEMP\Codex\DiffBeacon\<run-id>\reference" --compiler <cl.exe>
python -B -X utf8 tests/Fixtures/LineTransformSearch/verify.py
python -B -X utf8 tests/Fixtures/LineTransformSearch/verify.py --actual <body68.ndjson> --require-selections
python -B -X utf8 tests/Fixtures/LineTransformSearch/verify.py --actual <public68.ndjson> --public-plan
```

独立readerはC#をimportせず全68 IDを要求する。全74行の5fieldを照合し、列選択traceが
存在する場合は全selectionsも比較する。`--require-selections` は省略を拒否する。
原典自己照合は438 checks（370 field＋68 traces）、公開結果にtraceがなければ370 checks。
`okCases=62` / `errorCases=6` は `caseCountBasis=fixedCppFunctionBody` の原典件数。
公開集計は `publicPlan=true`, `oracleMatchedCases=62`, `publicConstructorCases=6`,
`publicBoundaryCases=6`。診断文の言語は比較しない。

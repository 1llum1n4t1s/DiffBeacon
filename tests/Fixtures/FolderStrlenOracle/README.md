# StrlenFunc の固定 native oracle

旧 WinMerge の固定 commit `6780924ba35f7f55af0f6f14dd302561175830a6` から raw Git blob を取り、`StrlenFunc`、`ToStringValue`、直下配列 map、UTF-8 文字数 helper、登録行と引数数検証ブロックを無変更で抽出した45件です。MSVC x64で二回独立compileし、別child processから得た同一stdoutを `golden.ndjson` に固定します。製品コードから期待値を作らず、native stdoutを手編集しません。

`golden.ndjson` のSHA-256は `4d31dbab06e96df545b2346481b36721d7cc394d9365a74578ce127aa01ab0d6` です。`fixed-manifest.json` は自身以外の全固定fileのSHA-256とsizeを記録します。`.gitattributes` は全fileを `-text` に固定します。

## 原典と採取境界

`reference-source.zip` は固定commitの380 raw blobsを保持します。原典のpath、Git blob SHA-1、SHA-256、sizeは `source-manifest.json` に記録します。旧 WinMerge の `LICENSE.md` とPocoの `Externals/poco/LICENSE` を同梱し、`unicoder.cpp` 内のMIT noticeも保持します。再採取にGit履歴や既存fixtureは不要です。

| 原典 | 実行範囲 |
| --- | --- |
| `Src/FilterEngine/FilterExpressionNodes.cpp:1836` | `StrlenFunc` 全文 |
| 同 `:1693` | `applyToScalarOrArray` template 全文 |
| 同 `:2315` | `ToStringValue` 全文 |
| 同 `:3778,3837,4270` | `FunctionInfo`、`strlen`登録行、constructor内の引数数検証ブロック |
| `Src/FilterEngine/FilterExpressionNodes.h:15–31` | full ValueType / ValueType2、ExprNode宣言 |
| `Src/Common/unicoder.cpp:112,145` | `Utf8len_fromLeadByte` / `stringlen_of_utf8` 全文 |
| `Externals/poco/Foundation/src` と `include/Poco` | Timestamp、LocalDateTime、DateTimeFormatterとその日時依存関数・宣言 |

正確な23 spanのbyte範囲・行番号・SHA-256は `provenance.json` を正本とします。spanはraw bytesのまま連結し、置換しません。末尾改行を含むspanのendLineが次の行になる場合があります。Pocoのheader/platform includeは原本をそのまま配置します。日時関数もMSVCでcompileして実行し、Pythonやadapterの日時変換でnative結果を代用しません。

Pocoの未使用 `DateTimeFormat::isValid`（regex依存）、`Timezone::name/standardName/dstName`（文字コード変換依存）、`NumberFormatter::append` のfloat/double overloadはcompile対象spanから外します。`DateTime::checkValid` が不正日時を検出した際の `Poco::format` にだけadapterのthrow guardを置きます。正常日時の検査・変換・書式化は原典のままです。不正日時の例外整形は検証しません。これらの除外・guardをPoco全体の互換性の証拠にしません。

`adapter.cpp` は空context、typed値を返すAST spy、配列storage、JSON出力を提供します。FileContentRefのpath変換もthrow guardで、regex/FileContentRefは入力対象外です。文字列はstrict UTF-8の各byteをoctal escapeと明示長の `std::string` にし、埋込みNULを保持します。`stringified` は評価成功時に同じ入力へ原典 `ToStringValue` を追加適用した観測で、Strlen戻り値は `result` です。NoneのStrlen結果はnull、ToStringValue結果は `"none"` です。

日時の原本文字列を固定するため、採取はWindows UTC+09:00環境を要求し、adapterでPocoの実offsetを検査します。child processにだけ `TZ=JST-9` を設定します。利用者のWindows設定は変更しません。timestamp入力はUnix epochからのmicrosecondsです。

引数数ケースはconstructor内の検証ブロックだけを実行し、`InvalidArgumentCountError` を `std::runtime_error` にaliasします。`result:true` は受理sentinelで、Strlen戻り値ではありません。全spyをthrow指定し、arityで引数未評価を確認します。

**FunctionNode constructor全体、findFunction、parser、optimizer、`.flt` loader、case directive、folder scanner、GUI、製品build/E2E/Publishは実行しません。無効UTF-8・孤立surrogate、無限大/NaN、不正日時、regex/FileContentRef、他OSは対象外です。**

## 入力と観測

`failure-matrix.json` を先に整理し、`case-plan.json` の型付き45入力をnative実行前に固定しました。

- string13件: 空、ASCII、日本語、補助plane、結合文字、ZWJ、家族emoji、途中/先頭NUL、CR/LF/TAB、variation selector、flag、4096文字。Unicode code pointを数え、grapheme clusterを数えません。
- scalar18件: None、boolean両値、0/負数/int64両端などinteger5件、double7件（負の0、小数6桁丸め、微小値、大きな値）、Timestamp3件。doubleはMSVCの旧 `std::to_string` の6桁固定形式を観測します。
- array9件: 空、単要素、異種混合、空nested、複数nested、深いnested、scalar混合、nested NUL、64要素。直下へ一度だけ適用し、nested arrayは `[a, b]` 形式に再帰文字列化して長さを返します。
- 引数throw1件とarity4件（0/1/2/3）。通常の評価traceは `[0]`、arityは `[]` です。

## 再採取と独立reader

Python 3.13標準ライブラリ、MSVC x64を使います。作業先は既定の一時フォルダー（`[IO.Path]::GetTempPath()`）配下の `Codex\DiffBeacon\<task-id>` 形式の新規run rootにし、既存親階層にreparse pointがないことを確認します。repository、`.codex`、CODEX_HOME内は使いません。TEMP/TMPもrun内compiler-tempへ注入します。

```powershell
$python = (uv python find 3.13).Trim()
$parent = Join-Path ([IO.Path]::GetTempPath()) 'Codex\DiffBeacon'
$null = New-Item -ItemType Directory -Force -Path $parent
for ($path = $parent; $path; $path = Split-Path -Parent $path) {
  $item = Get-Item -LiteralPath $path -Force
  if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point: $path" }
}
$taskId = [guid]::NewGuid().ToString('N')
$root = Join-Path $parent "folder-strlen-oracle-$taskId"
if (Test-Path -LiteralPath $root) { throw "Run root already exists: $root" }
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231\bin\Hostx64\x64\cl.exe'
$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
$stamp = [guid]::NewGuid().ToString('N')
& $python -B tests/Fixtures/FolderStrlenOracle/capture.py --output "$root/repro-$stamp-a" --compiler $compiler --vcvars $vcvars
& $python -B tests/Fixtures/FolderStrlenOracle/capture.py --output "$root/repro-$stamp-b" --compiler $compiler --vcvars $vcvars
& $python -B tests/Fixtures/FolderStrlenOracle/verify.py --observed "$root/repro-$stamp-a/golden.ndjson"
& $python -B tests/Fixtures/FolderStrlenOracle/verify.py --observed "$root/repro-$stamp-b/golden.ndjson"
```

`verify.py` は製品/captureをimportせず、Pythonのcode point数、固定小数書式、UTC+9日時、再帰文字列化から結果・stringified・例外・評価順を独立に照合します。全固定file、raw blobのGit SHA-1/SHA-256、全span、二回のnative compile入力、exe、stdout/stderr、終了code、held process handle終了、stdout/stderr EOF、PIDとbirth FILETIMEの別process証拠を検査します。`--observed` は新しいstdoutも固定native bytesへ照合します。通常の固定検証は引数なしで実行できます。

`native-proof.zip` は二回分のexe・全Poco入力・抽出span・adapter・生成入力・compiler response/command・compile/実行stdout/stderr・receiptを保持します。再生成可能なobjは含みません。`freeze.py --run1 <path> --run2 <path>` と `capture.py --initialize-source` は初回固定用で、既存固定物は上書きしません。

2026-10-18の初回原本は、その採取時のrun rootにある `final-a` / `final-b`、採取器構築途中の証拠は `run-a` / `run-b` / `run-c` / `run-d` に保持しています。既存証拠は移動・上書き・削除せず、fixture ZIPとreaderで再現・監査できます。再採取する場合は上記手順で新しいD: run rootを使います。

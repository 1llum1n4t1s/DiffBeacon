# RegexFind / RegexCount / RegexExtract / RegexReplace の固定原典

固定commit `6780924ba35f7f55af0f6f14dd302561175830a6` の C++ 関数と同commitの Poco / PCRE2 を実際にMSVCでcompile・実行した432件です。390件は関数本体、42件は原典の選択した最適化分岐を実行します。初回採取と別ディレクトリでの再compileは、432行のstdoutがbytes完全一致しました。通常の.NET製品buildにC/C++ compilerやPCRE2の依存を追加しません。

原典は [DiffBeaconの固定commit](https://github.com/1llum1n4t1s/DiffBeacon/tree/6780924ba35f7f55af0f6f14dd302561175830a6) の `Src/FilterEngine/FilterExpressionNodes.cpp`（RegexFind:3033、RegexCount:3106、RegexExtract:3148）です。`reference-source.zip` は57個のraw Git blobと40個の正確な抽出範囲、計97 entryを含みます。原典関数・helper・literal・Poco match／subst／compile／options・PCRE2 amalgamationは無改変です。抽出ファイルに付けた末尾LFだけは外側の区切りです。`provenance.json` はcommit、原本Git blob ID／SHA-256／サイズと、抽出元のbyte範囲／行番号／SHAを記録します。既存Replace fixtureは変更していません。

`LICENSE.md`（GPL-2.0）、Pocoの `LICENSE`（BSL-1.0）、PCRE2 source headerのBSD条件とUnicode noticesはraw原本に保持しています。原典と同じPCRE2 Cソース一式をZIPに含め、システムのPCREやPython regexで期待値を置換しません。`source-archive-manifest.json` が全entryのSHA・サイズ・CRCを、`fixed-manifest.json` が自身を除くこのfixture全ファイルのSHA・サイズを固定します。近い `.gitattributes` の `* -text` で原本bytesを保持します。

| 検証対象 | 固定ケース |
| --- | --- |
| RegexFind / RegexExtract | それぞれ122件。group／発生回数の省略、負数、int64極値、型違い、未一致／空capture、UTF-8 byte位置、非重複、末尾までのゼロ幅一致 |
| RegexCount | 81件。空入力／空pattern、非重複、一致なし、ゼロ幅、Unicode、配列／none、引数個数 |
| 共通 | 文字列／型付きRegex、文脈のcaseSensitiveとtyped Regex独自設定、Unicode文字種・casefold、NUL、不正pattern／UTF-8、引数評価順、例外、動的引数と最適化 |
| RegexReplace | 107件。`$0..$9`、`$10`／`$99`／`$$`／`${1}`／backslash、未参加capture、空／ゼロ幅／末尾一致、NUL／Unicode／不正UTF-8、型・none・配列、引数評価・Throw、最適化14件 |
| 最適化 | 42件。原典のregexcount／regexfind／regexextract／regexreplace分岐、literalへの折り畳み、pattern事前compile、動的source／pattern／発生回数、失敗時の残存AST |

正常396件と例外36件（PCRE2 9、assertion 6、adapterのThrowNode 17、std::out_of_range 2、bad_variant_access 2）を収録しています。`cases.json` は入力計画、`golden.ndjson` は実native stdoutで、原典 `ToStringValue` による `serializedUtf8Hex` も含みます。例外時にはserializerを実行しません。`capture-receipt.json` と `recompile-receipt.json` は実processのPID、birth time、保持handleのsignaled状態、実終了コード、stdout／stderr EOF、compiler／実行file／stdoutのSHAを保持します。

## 再現

Windows、Python 3.12以降、MSVCと固定commitのGit objectを持つcheckoutを使います。成果物は新しい外部Temp作業ディレクトリへ出力します。既存出力・リンクを拒否し、repository／Codex home／名前 `artifacts` のfolderを使いません。

```powershell
$python = (uv python find).Trim()
& $python tests/Fixtures/LineTransformRegex/verify.py --verify --git
& $python tests/Fixtures/LineTransformRegex/reproduce.py `
  --output "$env:TEMP/Codex/DiffBeacon/regex-reference-fresh/native" `
  --compiler 'C:/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe' `
  --vcvars 'C:/Program Files/Microsoft Visual Studio/18/Community/VC/Auxiliary/Build/vcvars64.bat'
```

実際のMSVC versionに合わせてcompiler／vcvarsのパスを指定します。すでにMSVC環境を初期化済みなら `--compiler cl` とし `--vcvars` を省略できます。通常readerはGitやcompilerなしでZIPの全raw blob IDとSHA、全抽出範囲、固定ファイル、stdout framingとschemaを照合します。`--git` は固定commitの実Git objectも照合します。`--actual <native stdout.ndjson>` は全432 IDの原順序と型付き結果・serializer・例外・評価順・最適化診断をJSON完全一致で照合します。製品用のdiagnosticsや公開parserの同等性をこのnative形式だけから主張しません。

`generate-cases.py --output <新しい外部Tempのcases.json>` は期待値を計算せず入力計画だけを再生成します。固定 `cases.json` とbytes比較できます。`capture.py --bootstrap` は初回のGit原本採取のための非通常経路で、出力は外部Tempに限定し固定goldenを上書きしません。通常の再現は必ず固定ZIPとgoldenを照合します。

## adapterと未検証の境界

- 旧parser全体、whole optimizer、LineTransform／provider／CSV全体は実行していません。AST、FilterEvalContext、caseSensitive（公開式の@cs／@ciに相当する値）、typed RegexのcaseSensitive、TraceNode／DynamicNode／ThrowNodeをadapterが供給します。公開式の構文・型付きRegexのliteral構文の検証は製品E2Eの担当です。caseSensitive設定の効果は原典Poco／PCRE2が実行しています。
- Pocoのクラス宣言と例外型の接続を最小化しています。`poco_assert` は原典と同じ条件で `AssertionViolationException` を投げますが、Debuggerの副作用・ファイル名／行番号を含むPocoの例外本文全体は再現しません。assertionの `errorUtf8Hex` は条件文字列です。`std::bad_variant_access` の本文も実装依存を除くため型名へ正規化します（今回の432件には該当なし）。その他のPCRE2エラー本文は原典Pocoの実生成bytesです。
- typed Regex literalはrun呼出し前に構築します。このfixtureのtyped patternは正常patternに限定しています。不正typed literalのparser／compile段階は未検証です。不正な文字列patternでは原典createRegexが失敗をnoneへ変換することを採取しています。Timestamp／FileContentの実構築、null共有Regex／null共有配列、allocation failure、病的patternの時間上限は採取していません。
- マッピングは原典の一階層です。配列内の配列は再帰マッピングされず、原典getAsString／ToStringValueで文字列化した値にregexを適用します。原典のFind位置はUTF-8 byte位置です。Extractの第4引数は原典でoffsetという名前ですが、処理は0起点の発生回数です。
- 原典はゼロ幅後に1 byte進めます。UTF-8継続byteへ進んだCount／Find／ExtractはPCRE2例外になり、末尾一致後さらに次を要求したFind／ExtractはPoco assertionになります。Countは末尾超過を自分で打ち切ります。これらを空一致の成功や一致なしへ読み替えません。
- 最適化のExtract分岐は第4引数のliteral判定を行わないため、adapter DynamicNodeの発生回数も評価して折り畳む原典挙動を含みます。Findの同条件は折り畳みません。`regexNeedle` は実行後ASTの第2引数がtyped Regexか、折り畳みが完了したことを示すadapter診断です。Pocoの全例外階層・debugger・実アプリ終了の挙動は主張しません。
- macOS compiler、Native AOT、GUI、製品予算／取消はこのfixture作成では実行していません。固定原典の一致と製品側の安全上限・parser拒否・E2E結果は分けて評価します。

このfixtureは固定原本と再生成手順です。検証用native binary／object／展開ソース／compileログは外部Tempにだけ出力し、Gitへ登録しません。

RegexReplace追加前の325行（85,221 bytes）のSHA-256は `edff508973ae736fba73d6ad44cb76228524e9d99445a6107d693551b6a44ced` のままです。readerはこのprefixを独立して検査します。`contract.json` は今回の失敗条件と照合契約を記録します。中間の未参加capture参照はnative `std::out_of_range`、末尾の未参加capture参照は空文字となり、通常評価と最適化の失敗段階を区別します。既存ThrowNodeの `std::exception` labelを保ちつつ、標準例外 `std::out_of_range` と `bad_variant_access` は個別に採取します。

Unicodeゼロ幅の同一入力 `RegexReplace("é😀a", "(?:)", "X")` は、通常評価では型付き `none`（serializer `none`）、選択optimizerでは `std::get<std::string>` により `bad_variant_access`（phase `optimize`、serializer未実行）となることをnativeで採取しています。

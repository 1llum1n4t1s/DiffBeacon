# DiffBeacon.Core

一般行変換式の`ReplaceWithList(source,listOrPath)`はcompiled regexと文字列replacementの二要素pairだけを適用し、inlineの文字列pattern pairを無視します。`RegexReplaceWithList`はcompiled regexと文字列patternの両方を受理します。ルールは逐次適用し、引数はsource・listの順に先行評価します。sourceの直下配列要素を一段だけ変換し、noneは保持、ほかの要素は既存serializerで文字列化します。正規表現は既存の有限時間backend・case directive・capture展開・zero-width進行を使います。任意PCRE互換や、公開できないtyped regex原典の直接観測は主張しません。

pathは`ILineTransformListEnvironment`でホストへ委譲し、Coreはfilesystemへアクセスしません。最適化後に文字列literalとなるpathはcompile時に一度だけ読み、動的pathと`@noopt`は評価ごとに読みます。リストはCR/LF/CRLF単位で、空行・先頭文字が`#`の行・TABなしの行を飛ばし、最初のTABを区切り、3列目以降を無視します。通常loaderはregex metacharacterをescapeしてcompileし、regex loaderは不正ruleを診断して飛ばします。固定原本とOS/encoding adapterの境界は[置換リストfixture](../../tests/Fixtures/LineTransformReplaceLists/README.md)を参照してください。

`ToHalfWidth(value)`／`ToFullWidth(value)`／`ToHiragana(value)`／`ToKatakana(value)`／`ToSimplifiedChinese(value)`／`ToTraditionalChinese(value)`は`ILineTransformStringEnvironment.MapCharacters`へ個別変換を渡します。各一引数で、noneを保持し、配列は直下だけに適用します。入れ子配列を含む非null値は既存の旧形式で文字列化し、定数化せず共有作業・出力予算と取消を適用します。AppはWindowsのLCMapStringEx、macOSのCFStringTransformを提供します。互換正規化で代用せず、OSのUnicode／変換データ差を許容します。

.NET 11 のマネージド比較・マージライブラリです。外部パッケージ、ネイティブ DLL、サブモジュール、実行時の反射・動的読込みを使用しません。`IsAotCompatible=true` により NativeAOT の解析対象になります。

## テキスト比較と保存

```csharp
var left = await TextDocument.LoadAsync(leftPath, cancellationToken);
var right = await TextDocument.LoadAsync(rightPath, cancellationToken);
var options = new ComparisonOptions { IgnoreCase = true, IgnoreWhitespace = true };
var comparison = TextDiffer.Compare(left.Text, right.Text, options, cancellationToken);
if (comparison.Blocks.Count > 0)
{
    var merged = TextMerger.CopyLeftToRight(comparison, comparison.Blocks[0]);
    await right.SaveAsync(rightPath, merged, cancellationToken);
}
```

- `TextDocument`: `Path`, `Text`, `EncodingName`, `NewLine`, `HasBom`, `HasFinalNewLine`。UTF-8/16/32 の BOM とバイト順を検出し、保存時に保持します。BOM なし UTF-16 はゼロバイト分布で推定します。UTF-8 として不正な入力の既定フォールバックは Windows-1252 です。`TextLoadOptions.FallbackEncodingName` で Shift-JIS 等へ変更できます。BOM なしの文字コード推定は完全ではありません。
- テキスト読込みは既定 64 MiB 上限 (`TextLoadOptions.MaxFileSize` で変更可能)。不正なエンコーディング、NUL を含むテキスト、保存先の文字コードで表せない文字は例外を返し、元ファイルを上書きしません。同じディレクトリの一時ファイルへ全内容をフラッシュしてから rename 置換し、一時ファイルを片付けます。
- 既存ファイルの Unix モード (実行権限を含む)、Windows の Hidden/System/Archive/NotContentIndexed 属性を保持し、読み取り専用ファイルへの保存は拒否します。
- CRLF/LF/CR、混在する行末、最終改行の有無を文字列のまま保持します。`NewLine` は最初に現れる行末、行末がなければ OS の既定値です。
- `ComparisonOptions`: `IgnoreCase`, `IgnoreNumbers`, `CommentSyntax`, `SubstitutionRules`, `IgnoreWhitespace`, `Whitespace` (`None`, `Trim`, `IgnoreChanges`, `IgnoreAll`), `IgnoreBlankLines`, `IgnoreLinePattern`, `IgnoreFinalNewLine`, `CompareLineEndings`, `InlineCharacterLevel`, `BreakOnSeparators`, `WordBreakSeparators`。`InlineCharacterLevel` は既定で `true`（文字単位）です。`false`（単語単位）では変更した単語全体を区間にし、文字単位の絞込みを行いません。分割は既定で有効で、区切り文字の既定値は WinMerge の `HiliteBreakSeparators` に合わせます。文字列は最大256 UTF-16単位で、U+00FF以下に適用し、それより大きい文字は固定した `WordCharacterProfile` で分類します。CLIの `--word-level` と `--no-separators`／`--separators TEXT` でも指定できます。`Normalize(string, cancellationToken = default)` は公開ヘルパーです。文書全体のコメント状態を共有し、以前と同じく空白無視を改行にも適用します。`TextDiffer` は改行を保持した行ごとのキーを使います。
- `DiffResult`: `Rows`, `Blocks`, `HasDifferences`, `LeftText`, `RightText`。`DiffRow` の左右行番号は 1 始まり・欠落側は null、`LeftText`/`RightText` は行末を含みません。`Kind` は `Equal`, `Added`, `Deleted`, `Modified`。変更行の `LeftSpans`/`RightSpans` は UTF-16 インデックスで、変更ブロック全体の WordDiff 区間を各行本文へ投影した複数の範囲です。中央の一致文字は強調しません。`InlineWorkUsed` と `InlineFallbackCount` は全ブロックで共有する詳細比較予算の使用量と詳細比較を省略した変更行数を示します。
- `WordDiffer.Compare` は原文の UTF-16 区間を保持し、単語の O(NP) 対応を文字区間へ絞り込みます。`WordDiffOptions` は文字／単語、大文字小文字、数字、空白、EOL、区切り文字を指定します。文字分類は元ソースで採取した Windows CTYPE1 / CRT C の固定値、大文字小文字の折畳みは ASCII です。旧 GUI の全設定・全 ICU 字素規則との完全互換は未確認です。外部 DLL は呼びません。
- WordDiff の既定予算は4,000,000、最大8,000,000、入力は各64 Mi文字、token vectorは20,480未満、traceは32 MiB以下です。上限超過と不正UTF-16は全原文を変更区間とし、`Fallback`・`FallbackReason` を返します。`WorkUsed` は実行した処理の予算消費です。キャンセルは例外で伝播します。TextDifferでは詳細比較予算を全ブロックで共有し、行対応の予算と分けます。原文を省略・変更しません。
- `DiffBlock`: `Index`, `RowStart`, `RowCount`, `LeftStart`, `LeftCount`, `RightStart`, `RightCount`。開始位置はすべて 0 始まり。コピー後は再比較し、更新前のブロックを再利用しないでください。除外行は `Equal` の表示行として残ります。改行なしの最終行を保持行の前後へ移す場合は、対象文書の改行を境界へ補い、行の連結を防ぎます。
- 行対応は同梱GNU diffのdiscard・diagonal探索・境界調整をmanagedへ移植した算法を使います。原文の共通端と比較キーを照合し、本文だけを共有同値クラスへ分類します。`MaxFallbackComparisons` は既定4,000,000・最大8,000,000で、分類の文字ハッシュ・衝突比較・配列確保・算法に共有します。本文が片側262,144行を超える場合も未解決本文を変更扱いにし、共通端と全元行を保持します。線形の共通端走査と全文同値の確認は予算0でも行い、文字ごとに取消を確認します。N×Mのテーブルは作りません。退避時の最小編集列は保証しません。`DiffResult.LineWorkUsed`・`LineFallback`・`LineFallbackReason` とCLI `--compare` の同名camelCase項目で使用量・退避を確認できます。行内WordDiffの予算とは独立しています。原本照合の範囲は [GNU fixture](../../tests/Fixtures/GnuLines/README.md) に記載します。

### 数字・コメント・置換フィルター

```csharp
var options = new ComparisonOptions
{
    IgnoreNumbers = true,
    CommentSyntax = CommentSyntax.CStyle,
    SubstitutionRules = [new SubstitutionRule(@"build-(\w+)", @"release-\1", MatchCase: false)]
};
var comparison = TextDiffer.Compare(left.Text, right.Text, options, cancellationToken);
```

| 設定 | 対応仕様 |
| --- | --- |
| `IgnoreNumbers` | ASCII `0`〜`9` を除去。小数点・符号・英字・Unicode 数字は保持。 |
| `CommentSyntax.None` | コメントフィルターを無効化。既定値。 |
| `CommentSyntax.CStyle` | `//`、行をまたぐ `/* ... */`。通常の文字列・文字リテラルとそのエスケープ、`R"delimiter(...)delimiter"` の C++ raw 文字列を保持。正式なエンコーディング接頭辞 `u8R` / `uR` / `UR` / `LR` も接頭辞前の識別子境界を確認して扱う。末尾 `\` による行コメント継続を扱う。 |
| `CommentSyntax.CSharp` | `//` と `/* ... */`。通常の引用符に加え、`@"..."` の二重引用符エスケープと複数行 raw 文字列を保持。 |
| `CommentSyntax.Python` | `#`。通常の引用符・エスケープ・三重引用符の文字列を保持。docstring は文字列として比較。 |
| `CommentSyntax.Xml` | `<!-- ... -->`。属性の引用符、CDATA、処理命令の内容を保持。 |
| `SubstitutionRule(Pattern, Replacement, MatchCase = true)` | 登録順に全一致を置換。`UseRegex = false` でリテラル指定、リテラル指定の `WholeWord = true` で単語境界、`Enabled = false` で無効化。 |

コメント→置換→空白→数字→大文字小文字の順に比較キーを生成します。入力文字列、表示行、UTF-16 の inline 座標、コピー・マージに使う原文行の位置は変更しません。コメント除去後に文字が残らない行は `Equal` の行として保持し、追加・削除差分から除外します。コメント周囲の空白は残るため、その違いも無視する場合は空白オプションを指定します。`IgnoreLinePattern` は従来通り原文の行に適用します。

置換は物理的な各行の内容に適用し、行末・別の行には一致しません。置換結果に改行を入れても比較キーの一部となり、原文の行対応は変わりません。旧 `SubstitutionList.cpp` の `\1` 形式のキャプチャ参照、`\a` / `\b` / `\f` / `\n` / `\r` / `\t` / `\v` / `\xNN` を引き継ぎ、.NET の `$1` / `${name}` 等も使えます。旧 PCRE の全構文互換、ハンク全体をまたぐ置換、1300 種類以上の旧言語エンジンは対象外です。JavaScript のテンプレート文字列・正規表現リテラル、補間文字列内の式の再解析、プリプロセッサ展開は行いません。

置換規則は 256 個、規則のパターンは 16,384 文字、置換文字列は 65,536 文字までです。正規表現の入力と置換後の出力は行ごとに 1,048,576 文字までです。各正規表現は 250 ms タイムアウトを持ち、置換の一致処理全体にも同じ時間上限があります。不正パターンは `ArgumentException`、時間超過は `RegexMatchTimeoutException`、サイズ超過は `ArgumentException`、キャンセルは `OperationCanceledException` として呼び出し元へ返します。無視したエラーを同値扱いしません。`RegexOptions.Compiled` とコード生成は使いません。

旧実装の根拠は `Src/DiffWrapper.cpp:402`（構文パーサーのコメント除去）、`:432`（置換）、`:438`（空白）、`:451`（ASCII 数字）、`Src/SyntaxParserHelper.cpp:64`（コメント色の範囲だけを除去）、`Src/SubstitutionList.cpp:14`（置換エスケープ）と `:154`（登録順の全置換）です。引用符と跨行状態は `Externals/crystaledit/editlib/parsers/cplusplus.cpp:399`、`python.cpp:513`、`xml.cpp:166` を参照しました。旧版のハンク後処理を、元行を保持した比較キーの生成に移しました。三者マージは `TextDiffer.Compare` の元行ブロックを通してこの設定を受け取ります。

### 一般行変換

`Now()`・`Today()`は引数なしのTimestampを返します。式のcompileごとに時計を一度捕捉し、その式の全行で同じ値を使います。別の式と再compileは新しく捕捉します。Todayは捕捉したNowのローカル日付の0時をUTCへ変換した値で、隣り合う日の差はDSTにより23／25時間にもなります。通常はTimestamp originのliteralへfoldし、`@noopt`でも同じ捕捉値を保持します。時計環境はruntime注入し、保存DTOへ含めません。共有予算・取消・原文保持を維持します。

`LineTransformRule`／`LineTransformPlan` は外部ライブラリなしで式を解析し、`TextDiffer`と表の比較キーを変換します。原文区間・編集／保存本文・外側EOLは変換しません。表では引用符と二重引用符を残したRAW列を選び、callerが引用内改行とESCをescapeした行を渡します。通常のCSV解析の復号セルとは別の入力です。プレーンテキストは区切り文字NULの1列で、`ColumnRange`が列を選べば行全体、選択が空なら空文字列を返します。

対応値は文字列・32 bit範囲の整数literal・double・bool・`none`・入れ子の配列です。`ToInt`による文字列変換は64 bit整数です。括弧、単項`-`、`+ - * / %`、`= != < <= > >=`、`contains`／`not contains`、`matches`／`not matches`、`and or not`を扱います。`If(condition, yes, no)`はscalar boolのtrue、または配列の直下にtrueがある場合にyesを選び、評価時は選択枝だけを実行します。既定のOptimizeは全引数を先に訪れ、literalな`ColumnRange`の不正範囲は未選択枝でも解析時に拒否します。`@noopt`はこの最適化を止め、動的範囲と同様に評価時に解析します。`@cs`／`@ci`は文字列比較の設定で、case-insensitive比較のfoldはASCIIに限ります。

対応getterは`Line`、`LineNumber`、`LineLength`、`ColumnCount`、`ColumnN`、`Column(numberOrHeader)`、`LineAt`／`LineOffsetAt`、`ColumnAt`／`ColumnOffsetAt`、`ColumnRange`と`Content`です。行・列番号は1始まりです。`Left`／`Middle`／`Right` prefix付きprovider getterも現在の一文書を参照し、適用対象のpane maskとは別です。`LineCount`は関数としてのみ対応します。

`Content`は比較前の原文の不変snapshotで、filesystemや変換済みの行を読みません。`LineCount(Content)`は文字またはEOLがある物理行の数（空fileは0、末尾EOLの後の合成行なし）、`Sublines(Content,start[,len])`は0始まりで選択行の正確なEOLも残す文字列です。負startは末尾から数えて0へclampし、len省略／非整数は-1、負lenは`remaining + len + 1`、残りを超える正lenは残りへclampします。content・start・lenを順に評価してからstartを型検査し、不正startと非Contentはnone、配列は直下一段だけに適用します。Sublinesを含む式の複数行結果は一つのopaque比較キーとし、実際に複数行キーを受け取った後続式でも元行を分割しません。二者・三者・四者比較はopaqueキーを元の物理行境界のまま扱います。原文・表示行数・保存本文は変更しません。Content／LineCountだけでは既存の生成CR/LF拒否を緩めません。内容値はstringと異なる型です。実在するText／Table入力の文字列化は渡された元の絶対pathを使う`<FileContent:{path}>`、pathlessな公開string APIや仮想入力は`<Content>`です。いずれもsnapshotのbytesを使い、pathからfileを読みません。pathは比較中だけの値で、保存DTOへ含めません。Prop・旧ファイル属性比較／contains・pathへのLineCount・任意file filter／`.flt`の完全移植は含みません。公開string行配列APIはLF結合、製品Text／Tableは元の全文字列からsnapshotを作ります。[固定契約と検証範囲](../../tests/Fixtures/LineTransformContent/README.md)を参照してください。

`Trim`／`TrimLeft`／`TrimRight`は空白・TAB・CR・LFを除去します。`Strlen`／`Len`は文字列のUnicode scalar数を数え、`Len(none)`は0、配列は直下の要素数、文字列以外のscalarは1です。`Substr`はUTF-8 byte単位で、文字の途中を切る境界を拒否します。`ToInt`／`ToDouble`／`ToNumber`の基本数値変換、`Abs`にも対応します。`ToLower`／`ToUpper`は文字列化したscalarまたは第一引数の直下配列要素を変換し、`none`は維持します。`NormalizeUnicode(value[, form])`は同じ一段の配列適用と文字列化でUnicode正規化を行います。formのASCII大小は区別せず、NFC／C／1、NFD／D／2、NFKC／KC／5、NFKD／KD／6を受け付け、空白は除去しません。不正なstring／整数formはvalueの評価を省略してnone、その他の型や省略時はNFCです。`@ci`／`@cs`によらず、定数化せず実行環境へ渡します。Windowsは`NormalizeString`、他OSは.NETのUnicodeデータを使用します。PCREのtyped pattern・未移植のtyped literal等の旧言語の残る関数／構文は明示エラーです。生成CR/LFによる行数変更と原行写像、非ASCIIの区切り文字／引用符も一般行変換では拒否します。

`Array`／`At`、`AnyOf`／`AllOf`／`AllEqual`、`AndEach`／`OrEach`／`NotEach`、`IfEach`／`Choose`／`ChooseEach`に対応します。`At`は負のindexを末尾から数え、範囲外はnoneです。配列の算術・比較はscalarを各要素へ適用し、入れ子を再帰処理します。両辺配列の等値判定は型も含めて比較します。`IfEach`は両枝を評価し、`If`／`Choose`は選択枝だけを評価します。文字列・数値関数は第一引数の直下の配列要素へ適用します。最終の比較キーは角括弧とコンマ区切りの文字列になり、原文へ保存しません。

`InRange(value, lower, upper)`は下限を含み上限を含まない判定です。下限・上限・値の順で先に評価し、値はscalar／直下arrayに適用します。整数の辺はexact比較を保ち、doubleの辺だけ値を変換します。文字列はUTF-8順で比較し、`@ci`はPocoと同じASCII foldです。型不一致はnone、引数数は3件に限り解析時に拒否します。共有作業予算・取消を使い、キーだけを変換して原文を変更しません。[範囲fixture](../../tests/Fixtures/LineTransformRanges/README.md)の72原典は59公開serialized-key一致候補・3公開引数数拒否・10表現不能の未実行に分けます。別の四引数probeと、12件のToInt decimal bridgeのvalue／keyだけの境界を保持します。

`IsWithin(value, lower, upper)`は下限と上限を両方含みます。評価順・型・配列・文字列比較・共有予算と取消は`InRange`と共通です。両端が等しい場合も値がその端と等しければtrueです。[包含範囲fixture](../../tests/Fixtures/LineTransformWithin/README.md)の72原典と別の四引数probeを、59公開serialized-key一致・3解析拒否・10表現不能の未実行へ分けて照合します。12件のToInt bridgeは最終value／keyだけの一致で、typed AST／Timestamp／全parser互換の証拠には含めません。

`B`・`KB`・`MB`・`GB`・`TB`を大小文字を問わず数値へ直接続ける容量literalを使えます。整数または両側に数字のある小数だけを認め、原典通りdoubleを単位ごとに1024倍してから64bit整数へ切り捨てます。単項負号は変換後に適用します。数値解析の失敗は原典constructorの初期値0を保ち、有限な倍率後値が2^63以上なら未定義castへ進まず拒否します。指数付き・空白付き単位・未知単位は解析時に拒否します。既知unitだけを消費して後続のAND／OR等を次tokenへ渡し、比較キーだけを生成します。[容量fixture](../../tests/Fixtures/LineTransformSizeLiterals/README.md)は95原典・77公開serialized一致・18未実行を保持し、別18probeと隣接token／丸めのengineering観測を分けます。typed AST・Native全parserの互換を意味しません。

期間literalは、数値に直結する`w/week/weeks`・`d/day/days`・`h/hr/hour/hours`・`m/min/minute/minutes`・`s/sec/second/seconds`・`ms/msec/millisecond/milliseconds`をASCII大小文字を問わず認識します。原典と同じdoubleの段階的な秒倍率→1,000,000倍（ミリ秒は1,000倍）から64bitのmicrosecond整数へ切り捨て、単項負号は変換後に適用します。数値解析のoverflow／underflow失敗時0と、倍率後の64bit範囲外の拒否を区別します。容量unitを先に読み、既知期間unitの最長prefixだけを消費するため、後続AND／ORは既存parserへ渡ります。指数・空白・未知unitは拒否し、原文と既存共有予算・取消を保ちます。[期間fixture](../../tests/Fixtures/LineTransformDurationLiterals/README.md)の106原典は92公開serialized候補・10constructor専用・4platform丸めの未実行を分け、別21解析probeと24engineering観測・共有規則予算を保持します。Native全parser・typed AST・cache互換は未検証です。

Version literalは引用符へ直接続く`v"…"`／`V"…"`を認識し、通常文字列と同じ二重引用符の重ね書きを復号します。backslashはescapeにせず、NUL・未終端・不正UTF-16は拒否します。原典の`sscanf_s("%d.%d.%d.%d")`に合わせ、ASCIIの符号付き整数を先頭から部分変換し、未変換部品は0、残りのtailは数値へ含めません。非負のmajorは65535まで、minor／buildはInt32の非負範囲、revisionはInt32を認め、各段階の加算がInt64を超えたら拒否します。16bitマスクやORへ置き換えず、majorの符号bitは原MSVCのC++17実測に合わせます。Version／容量／期間constructorのoriginを保持し、IntLiteral専用のArray／Abs／At／Substr／StrFind最適化と、演算後の通常整数literalへの復帰を区別します。[Version fixture](../../tests/Fixtures/LineTransformVersionLiterals/README.md)は原典60件・引用／整数文字列化128観測・C++17境界36観測を分けます。Native全lexer／parser・typed AST・公開内部型の同等性は未検証です。

日時literalは`d"…"`／`D"…"`を認識し、日時値を符号付き64bitのepoch microsecondsとして保持します。日時同士の比較・差、日時へ整数の加減算、`InRange`／`IsWithin`、`Minimum`／`Maximum`に対応します。差と整数の単位はマイクロ秒で、日時を数値や文字列の型へ暗黙に変更しません。配列表示では各日時を同じ文字列化操作で一度だけ変換し、生成文字数と共有予算を確認します。

`ToDateTime(value)`は文字列を日時値へ変換し、空文字列・解釈できない文字列・日時値を含む文字列以外の型はnoneを返します。`ToDateStr(value)`は文字列または日時値を対象日時のローカルな`YYYY-MM-DD`へ変換し、不正文字列やローカル時刻の取得失敗は候補全体を拒否します。どちらも配列の直下要素へ順番に適用し、入れ子配列はnoneとして扱います。`ToDateTime("2000-01-02T00:00:00Z")`と`ToDateStr(Line)`をテキスト・表の規則に使えます。定数最適化では成功した文字列literalの`ToDateTime`だけを日時literalに置換し、`ToDateStr`は実行時に評価します。共有予算・取消・原文保持を使います。[日時変換fixture](../../tests/Fixtures/LineTransformTimestampFunctions/README.md)では原典bodyの測定と公開式の検証を分けます。

`StartOfWeek(value)`・`StartOfMonth(value)`・`StartOfYear(value)`は文字列または日時値から、ローカルな週（日曜始まり）・月・年の初めを日時値として返します。直下配列の順序、非対応型のnone、途中失敗時の候補拒否を保ちます。週はローカル0時のUTC値から24時間単位で曜日分を引き、再びローカル時刻を検査します。月・年の戻り値は表示時までローカル時刻へ再変換しません。Coreへ環境を注入するときは任意拡張`ILineTransformTimestampCalendarEnvironment`でローカル0時の時差を提供します。日時originのliteralだけを定数最適化し、文字列literalの3関数は評価時に実行します。[暦関数fixture](../../tests/Fixtures/LineTransformTimestampCalendarFunctions/README.md)に測定範囲と独立readerを記録しています。

Coreを直接使う場合、日時関数には`LineTransformPlan.Compile`の`timestampEnvironment`または`ComparisonOptions.LineTransformTimestampEnvironment`へ`ILineTransformTimestampEnvironment`を渡し、文字列大小変換／Unicode正規化には`stringEnvironment`または`ComparisonOptions.LineTransformStringEnvironment`へ`ILineTransformStringEnvironment`を渡します。Appの文字列環境はWindowsで大小変換に旧実装と同じユーザー既定ロケールの`LCMapStringEx`、正規化に`normaliz.dll!NormalizeString`を使い、その他のOSでは大小変換に捕捉した`TextInfo`、正規化に.NETのUnicodeデータを使います。いずれもworkspace JSONへ保存しません。日時環境はWindowsの地域形式・対象日時のDST、macOSの標準時差・対象日時のDSTを別々に扱います。原典の符号付き演算が未定義になる値は保持・比較できても文字列化を明示的に拒否します。[日時fixture](../../tests/Fixtures/LineTransformTimestampLiterals/README.md)は原典のconstructor／serializer観測と公開API・型・全parserの検証範囲を分けています。

`Average(value[, condition])`・`Sum`・`Maximum`・`Minimum`は、同じ変換呼出しの各ルールで最初に参照した入力全行を集計します。条件がtrueの行だけ値を評価し、外側の配列は直下要素だけ処理します。最初の有効scalarが返す型を決め、後続の異なるscalarも原典通り件数へ含めます。ASTの値・条件の参照ペアごとに成功した統計だけを保持し、行を書き換えた後も同じルール内では再利用します。別ルール・入力・変換呼出しへ共有しません。子のOptimizeは維持し、集計自体は定数化しません。有限でない数値と整数化が64bit範囲を外れる結果は明示拒否し、入力と比較候補の公開を保護します。集計を使わない旧式には状態確保の追加課金を行いません。[集計fixture](../../tests/Fixtures/LineTransformAggregates/README.md)は141原典・104公開keyの派生照合・37表現不能の未実行と、別の引数数probe8件を区別します。

`Replace(text, needle, replacement)`は文字列needleの重ならない一致を置換します。三引数を先に評価し、needle／replacementが文字列でない場合はnoneです。第一引数のscalarを文字列化し、直下arrayにも適用します。置換結果を再検索せず、`$0`は元の一致文字列、`$1`～`$9`は空文字列、その他の`$`表記は原典Pocoの展開を保ちます。`@ci`はこの関数だけ固定PCRE2 10.47／Unicode 16.0.0のcaseless集合を使い、一般比較と検索関数のASCII foldとは独立です。外部runtimeやDLL探索は追加しません。生成表のBSD告知を`PcreCaseFold.LICENSE.txt`として通常／AOT出力へ同梱します。

空needleは`@noopt`ではnone、既定Optimizeで定数needleを処理した場合は原典empty-match分岐を保ちます。ASCII subjectでは各元byteの手前で置換文字列だけを生成し、末尾の一致は追加しません。非ASCII subjectの同分岐はnoneで、三定数を畳み込む場合は解析エラーです。scalar走査・KMP・二回の出力走査は共有workを消費し、確保量は先に共有生成予算へ予約します。取消・拒否では完成キーを公開しません。[置換fixture](../../tests/Fixtures/LineTransformReplace/README.md)の100原典は、公開式の78一致・10拒否と、typed regex／throw／不正UTF-8等の表現不能12件を区別します。

`StrCount(text, needle)`は重ならない一致を数え、`StrFind(text, needle, occurrence=0)`は0始まりの一致回の開始位置をUTF-8 byte offsetで返します。見つからない位置・負の一致回・型違いはnoneです。needleは空でない文字列、一致回は整数に限ります。第一引数はnone以外のscalarを文字列化し、配列は直下の各要素へ適用します。`@ci`のfoldは原典Pocoと同じASCIIの範囲です。引数は第三引数を含め先に評価し、関数の引数数は解析時に拒否します。既定Optimizeの定数StrCountがnoneを返す場合は解析エラー、StrFindのnoneは定数化せずに残します。

検索はUTF-8 bytesのKMPで行い、一つの関数評価内の配列要素は同じpatternを共有します。patternのbyte配列とint prefixは合計5×needle byte数、source byte配列はそのbyte数を確保前に共有生成量へ予約します。none・空配列・全要素noneではpatternを生成しません。引数評価の予算は引き続き消費します。長いprefixや多数規則も共有上限を超えれば候補全体を拒否し、入力と既存出力を保ちます。[検索fixture](../../tests/Fixtures/LineTransformSearch/README.md)の68原典ケースは62件を直接照合し、残る6件は公開コンストラクターの引数数拒否と原文保持を別の境界として照合します。旧parser／Optimize全体を実行した証拠とは区別します。

同じpane maskの連続規則は各行に全式を適用して即時更新し、前行は変換済み・後行は未変換として参照します。maskが変わる境界で前groupの全行を確定してから次groupを処理します。解析・全pane・全groupで取消と累積予算を共有します。規則256件、式65,536 UTF-16文字・合計1,048,576文字、深さ128・token 16,384、処理量134,217,728 work単位（走査文字数・評価等の累積）・生成量カウンター67,108,864を上限とし、上限は候補全体の拒否理由として返します。生成量は最終出力だけでなく中間コピーを累積計上し、通常の文字列はUTF-16文字数、`Substr`のUTF-8中間結果はbyte数で課金するため、保存fileのbyte上限とは異なります。

配列の生成は確保前に共有カウンターへ課金し、1値16,384要素・値の深さ128・比較候補全体で累積1,048,576 slotsを上限にします。既存のtoken上限により、単一literalの要素数上限を独立に到達することはできません。定数配列は再利用し、動的な128要素の配列は8192行で成功・8193行で候補全体を拒否します。上限時は原文と前回の確定表示を保持します。

配列の固定90ケースは [配列fixture](../../tests/Fixtures/LineTransformArrays/README.md) に記録し、公開Planでは89件の原典一致と物理CR/LFを生成する1件の明示拒否を照合します。旧C++のOptimize実行はこの採取範囲に含みません。

旧関数本体の固定86ケースとAST接続adapterの境界は [fixture契約](../../tests/Fixtures/LineTransforms/README.md) を参照してください。旧parser全体・callerのfile I/Oを移植完了／原典検証済みとは扱いません。

#### `matches`／`not matches`

`matches` は文字列subjectと文字列または型付きregex patternを受け取り、先頭位置の最初の非空一致がsubject全体を覆う場合だけtrueを返します。終端anchorへbacktrackさせないため、`"ab" matches "a|ab"` はfalseです。`not matches` は既存のbool集約結果に論理否定を適用します。`@cs`／`@ci`はCultureInvariant regexのcase設定へ反映します。既定Optimizeでは文字列literal patternをcompile時に準備し、不正patternを拒否します。動的patternまたは `@noopt` の不正patternは `matches` をfalseにします。regex互換構文変換、100 ms timeout、共有予算、取消を維持します。

#### `like`／`not like`

`like` は文字列全体をPoco Glob規則で照合します。`*` は0文字以上、`?` はUnicode scalar 1文字、`[...]` は文字集合、`[!...]` は否定集合、backslashは次の文字をescapeします。ファイル名glob固有のslash／dot規則や正規表現の構文は適用しません。既定はcase-insensitiveで、`@cs`／`@ci`を指定できます。case-insensitive時は原典のUnicode小文字変換を使います。`not like` は比較式を否定します。scalar／arrayのoperandは既存のarray演算規則に従い、文字列以外のoperand（`Content`を含む）は `none` を返します。不正な集合・escapeは照合が到達した時点で拒否し、共有作業予算と取消を適用します。

## 三者マージ

通常App／CLIの結果生成は [FourPaneMaterialization](FourPaneMaterialization.cs) のfactoryを使います。次の3methodはすべて `FourPaneMaterializedResult` を返します。

```csharp
FourPaneMaterialization.CreateTwoWay(string left, string right,
    ComparisonOptions? options = null, CancellationToken token = default);
FourPaneMaterialization.CreateThreeWay(string ancestor, string left, string right,
    ComparisonOptions? options = null, bool autoResolve = false,
    CancellationToken token = default);
FourPaneMaterialization.CreateIndependentThreeWay(string left, string middle, string right,
    ComparisonOptions? options = null, CancellationToken token = default);
```

設定付きoverloadは二者・独立三者では `options, FourPaneMaterializationSettings settings, token`、祖先三者では `options, autoResolve, FourPaneMaterializationSettings settings, token` の順です。二者・独立三者・祖先三者を分け、独立三者の中央を祖先として扱いません。祖先三者は `autoResolve: true` で独立変更を自動採用し、未解決箇所を保持します。生成・EOF／ghost同期・元diffと原文の対応・初期結果は共有作業／保持予算で準備します。

戻り値の `InitialBuffer` は [ResultLineBuffer](ResultLineBuffer.cs)、`InitialSession` は [ResultEditSession](ResultEditSession.cs) です。通常入口ではこのsessionを使い、`Current` の本文・物理行の由来と共有Undo/Redo・保存点を管理します。bufferから直接sessionを作る場合の公開constructorは `ResultEditSession(ResultLineBuffer initial, int maximumGroups = 100, long maximumCharacters = 64L * 1024 * 1024)` です。採用候補は `Choices` と `PrepareChoice` を使い、保存本文は `FourPaneMaterializedResult.ExpandedText(ResultLineBuffer buffer, CancellationToken token = default)` で未解決箇所を展開します。CLIは `FourPaneCliSerialization` を通し、LEFT／BASE／RIGHTマーカーと原文改行の保存契約を適用します。GUIの採用・編集境界は [結果編集の契約](../../Docs/MERGE-SESSION.md) を参照してください。

旧 `MergeSession`／`ThreeWayMerger` は新方式の4RID移行検証まで参照用に保持し、通常App／CLIからは呼び出しません。4RID検証後、共有型の整理と合わせて旧2classを除去します。外部consumerやNuGet配布の互換性は未確認で、公開済みAPIとの互換を検証済みとは扱いません。現役の共有型 `MergeSource`／`MergeLineProvenance` と、二者差分コピーの `TextMerger.CopyLeftToRight`／`CopyRightToLeft` はこの旧2classの除去対象に含めません。

## ファイル・ディレクトリ・バイナリ

- `FileComparer.CompareAsync(leftPath, rightPath, options?, cancellationToken)` → `FileComparisonResult`: `IsBinary`, `TextDiff`, `BinaryDiff`, `LeftDocument`, `RightDocument`, `HasDifferences`。比較入力は各 256 MiB 上限。テキスト扱いの入力には追加で 64 MiB 上限があります。
- `BinaryDiffer.Compare(leftBytes, rightBytes, maxDifferenceRanges = 65536, cancellationToken)` → `BinaryDiffResult`: 左右サイズと差分範囲 (`Offset`, `LeftLength`, `RightLength`)。同じオフセットの異なるバイトを比較し、挿入の再整列はしません。範囲数上限では残りを一つの範囲へまとめ、`IsApproximate=true` にします。
- `DirectoryComparer.CompareAsync(leftPath, rightPath, options?, cancellationToken)` → `DirectoryComparisonResult.Entries` (`DirectoryEntry.RelativePath`, `LeftPath`, `RightPath`, `EntryKind`, `Kind` / `Status`, `LeftSize`, `RightSize`, `Error`)。状態は `Equal`, `Modified`, `LeftOnly`, `RightOnly`, `TypeConflict`, `Error`。
- `DirectoryComparisonOptions`: `Recursive`, `Mode` (`Content`, `Hash`, `TimeAndSize`), `ExcludePatterns`, `TextOptions`。通常の内容・SHA-256 比較はストリームで実行します。`TextOptions` を指定した Content モードではテキスト除外設定を適用します。
- 除外パターンは `*`, `?` に対応し、区切りなしパターンは全階層の basename に適用します。パス付きの `*` は区切りも含めて一致します。リンク／junction は表示とリンク先文字列の比較だけを行い、再帰せず、ループやルート外への探索を避けます。Windows のみパス照合で大文字・小文字を無視し、macOS/Linux では区別します。アクセス失敗は `Error` エントリ、キャンセルは `OperationCanceledException` です。

## パッチ・構造化テキスト

表解析・JSON正規化にも末尾の任意`CancellationToken`を渡せます。`ParseDelimited`は解析ループ、`NormalizeJson`は正規化ノードごとに取消を確認します。JSONの同期構文解析自体は途中取消に対応せず、入力16 MiB上限と解析前後の取消確認を使います。

`UnifiedPatch.Create(leftText, rightText, leftPath, rightPath, contextLines = 3)` は一つのファイルの unified patch を作り、混在行末と最終改行なしマーカーを保持します。`Apply(text, patch)` は全 hunk の位置・行数・文脈を検証し、成功時だけ完成文字列を返します。不正／複数ファイルパッチは `FormatException`、文脈不一致は `InvalidOperationException`。`TryApply` は失敗時に元の文字列とエラーを返します。ファイルを直接書き換えないのでパッチ内パスを使ったファイルアクセスはありません。

`StructuredComparer.ParseDelimited(text, delimiter, quote?, allowNewlinesInQuotes?)`と`CompareDelimited(left, right, delimiter, options?, cancellationToken, quote?, allowNewlinesInQuotes?)`は任意の1文字区切り・引用符、その引用符の二重エスケープ、引用セル内の改行を扱います。既定はカンマ・ダブルクォート・引用内改行許可です。改行禁止時の引用内改行、区切りと引用符の一致、NUL・改行・単独サロゲートの指定は拒否します。比較は位置による行・列単位で、`CellDifference`の番号は1始まりです。キー列による行再整列は行いません。

`NormalizeJson(text, sortProperties = true)` / `CompareJson(left, right, options?)` はオブジェクトプロパティを ordinal ソートして整形します。配列の順番は保持し、数値は十進係数と指数で精度を失わずに正規化します (`1.0` と `1`、`1e2` と `100` は同じ結果)。巨大整数を double へ丸めません。構文不正／深さ 128 超／入力 16,777,216 文字超／数値 65,536 文字超／指数 512 文字超は `JsonException` です。

## 検証

`dotnet build Src/DiffBeacon.Core/DiffBeacon.Core.csproj` で AOT 互換性解析を含めてビルドします。アプリの E2E 検証は親プロジェクトが担当します。ライブラリ単体のビルド成功だけで全ファイルシステム／OS の互換性を保証しません。

### テキストフィルターの失敗条件（実装前の検証契約）

- コメントの除去で元の文字列・行番号・マージのコピー位置が変わること。コメントだけの追加行を保持しながら差分から除外できないこと。
- 行をまたぐ `/* ... */` / `<!-- ... -->` の状態が失われること。文字列内の `//`, `/*`, `#`、Python の三重引用符、XML の CDATA をコメントとして消してしまうこと。
- C++ raw 文字列の `u8R` / `uR` / `UR` / `LR` 接頭辞を拒否し、本文中の引用符・`//`・`/*`・改行を通常のコードとして解釈して差分を消すこと。識別子中の `nameu8R` を raw 文字列の開始と誤認すること。
- 数値無視が ASCII 数字以外の符号・小数点・英字・Unicode 数字まで消すこと。
- 置換の順序・大文字小文字・キャプチャ参照・リテラル指定が旧仕様と食い違うこと。不正パターン、破滅的バックトラック、出力増幅、キャンセルで比較処理が止まらないこと。
- 三者マージがフィルター適用後の短縮文字列をコピーし、原文を破壊すること。前処理が比較単位をまたぐ置換を無意識に許可して行対応を失うこと。

`MatchInside(beginPredicate, endPredicate)`は開始行と終了行を含む範囲でtrueを返します。全行で開始、終了の順に両条件を評価し、bool変換不能はfalseとします。終了のない開始は末尾まで続き、内側の開始は入れ子にしません。範囲内で開始と終了が同時成立すると、その行で閉じて同じ行から再開します。範囲外の同時成立は開始だけです。provider不在はnoneです。成功した範囲だけを専用のAST参照ペアcacheへ公開し、単一predicateのmatch cacheとは分離します。cacheは同じ変換の各ルール内だけで再利用し、共有予算と取消を維持します。関数自体の定数化は行いません。[固定原本と公開比較の境界](../../tests/Fixtures/LineTransformMatchInside/README.md)を参照してください。

## ファイルフィルター
フォルダー`.flt`の`RegexReplaceWithList(source,listOrPath)`は、sourceとlistをこの順で一度ずつ評価します。二要素pairのpattern（文字列またはcompiled regex）と文字列replacementを順に適用し、不正なpairや不正な文字列regexはそのruleを飛ばします。sourceはscalarまたは配列の直下へ適用し、Noneを保持します。pathの読込みはホストの`ILineTransformListEnvironment`へ委譲し、Coreからfilesystemへアクセスしません。リストのblank／先頭`#`／TABなしの行を飛ばし、最初のTABで分割して後続列を無視します。既存regex backend、共有作業予算、取消を使います。

Appは採用済みの外部リスト依存を比較結果とともに保持し、同size／mtimeの差替えも内容SHAで再検査します。全タブの入力・依存pathを、フォルダーコピー、HTML、workspace、包装と派生assetの出力から保護します。GUIの最終検査と公開は同じ同期dispatcher区間で実行し、公開前に採用された依存への上書きを拒否します。

フォルダー`.flt`は、provider不在時のAverage／Maximum／Minimum／SumとMatch系に加え、`LineAt`／`LineOffsetAt`／`Column`／`ColumnRange`／`ColumnAt`／`ColumnOffsetAt`を扱います。側別6関数は無prefixで二者／三者の側順配列、`Left`／`Middle`／`Right` prefixで指定側を返し、二者のMiddleも原典の側指定に従います。既存ASTの6種のOptimize overrideと部分foldを使い、default／`@opt`は子を先に最適化し、provider不在でも完了済みの子foldを保持します。`@noopt`はこの最適化を止めます。共有65,536作業予算・取消とリストcacheを維持します。providerのある本文取得、全parser／ASTと40 field全体は未移植です。補助平面case-foldは固定fixtureで確認したliteral経路の範囲とし、未採取のPCRE class／escape／backreferenceへ互換性を広げません。

`FileFilter.Load(path)` / `Parse(text, name)` / `ParseExpression(expression)`で旧`.flt`または式を読み、`DirectoryComparisonOptions.FileFilter`へ渡します。既定include/exclude、ファイル・ディレクトリ正規表現、名前・拡張子・サイズ・日時などの条件に対応します。各規則は入力上限・式の深さ上限・正規表現タイムアウトを持ちます。Windowsでは`Prop`／`LeftProp`／`MiddleProp`／`RightProp`を使えます。Coreの`IFileFilterPropertyService`へAppのShell／hash serviceを注入し、CoreにはWindows APIや外部packageを追加しません。フォルダー比較の`FullPath`系識別子は走査snapshotの各側絶対パスを使い、Shell serviceを介さず評価します。 `.flt`のside指定`LeftRelPath`／`MiddleRelPath`／`RightRelPath`は各側のrootからの相対パスを`/`区切りで返し、不在側はNoneです。二者・三者は一つの相対directory frontierで直接の子を収集してから、除外前の存在側の値を一度取得し、側順を保って評価します。除外／不在／走査失敗の親を越えて子のpropertyを取得したり再帰したりしません。Prop／FullPathの有無によらず、すべての条件式を全側DIFFITEMで一度評価します。正規表現`f:`／`d:`は側別のままです。property名は括弧付きのcanonical名の文字列literal一つに限定し、無効名／非Windowsの利用は明示拒否します。[使い方・値の型・検証範囲](../../Docs/FOLDER-COPY.md#プロパティフィルターwindows)を参照してください。存在属性はBoolを返し、`FileExists`／`FileMissing`／`Missing`と側prefix付き`IsFolder`を扱います。`DateStr`は既存formatterと通常のfixed-offset source contractを使います。年範囲は[-4713,9999]でyear 0を許容し、年10000を拒否します。実mtimeのpre-1970／FileTimestampのunsigned変換とnative year 0の実oracle一致は未認定です。式の二重引用符内では引用符を二重化し、backslashをそのまま扱います。単一引用符のescape拡張と正規表現`f:`／`d:`の従来quote規則は維持します。旧エンジン全体との互換性は未完了です。

`.flt`の`AllEqual(arg, ...)`は1個以上の引数を受け取ります。先頭の評価値が配列なら、その配列の要素を入れ子も含めて同じ型・値か順に比べ、後続引数を評価しません。先頭がscalarなら引数を左から評価し、最初の不一致で停止します。文字列は大小文字指定に関係なくordinal比較し、日時リテラルとproperty timestampは同じtimestamp型として比べます。フォルダー比較では`Name`／`Size`や`Prop(...)`が返す全側配列を評価し、不在側の`None`も比較に含めます。`AllEqual`は全側contextを使いますが、scalar式と明示したproperty contextはrootを持たない`FileFilter.Matches()`でも評価できます。`FullPath`を含む式は引き続きフォルダー比較contextが必要です。

フォルダー`.flt`の`AnyOf(expression)`は引数を一度だけ評価します。配列の直下にboolean trueが一つでもあればtrue、空配列・全false・非booleanだけ・入れ子内のtrueだけならfalseです。入れ子と直下のtrueが混在する場合はtrueです。scalar booleanはそのまま返し、その他のscalarは`None`です。引数は正確に1個で、関数名の大小文字を区別しません。`AllOf`と同じ全側metadata contextを伝播し、`AnyOf(Exists)`は不在側があっても存在側を採用、`AnyOf(Size > 0)`は存在側の一つでも正なら一致します。`not`・`AllOf`・`AllEqual`・`Len`と合成できます。一般行変換のAnyOfとは別の関数です。[固定native oracle](../../tests/Fixtures/FolderAnyOfOracle/README.md)は62件で、独立readerと自己検証の注入property contextで型・直下配列・評価回数・例外伝播・arityを照合する構成です。CLIと実GUIは二者／三者・不在側・実Shell値を検証し、任意の空／入れ子配列は自己検証の注入範囲です。今回の追加は検証実行前であり、過去のAllOf／Lenの成功件数には含めません。

フォルダー`.flt`の`NotEach(expression)`は引数を正確に1個、一度だけ評価します。scalar booleanは反転、その他のscalarは`None`です。配列は直下の各slotへ同じ変換を適用し、順序・長さ・未定義slotを保ちます。slot自身が配列の場合は直下にboolean trueがあるかを判定して反転します。したがって`[true,false,1] → [false,true,None]`、`[[true],[],[[true]]] → [false,true,true]`で、再帰mapでも再帰true探索でもありません。結果全体を浅い真偽値へ縮約する論理`not`とは別です。関数名は大小文字を区別せず、全側metadataと引数のproperty／side／folder-context依存を伝播します。`NotEach(Exists)`・`AnyOf(NotEach(Exists))`は不在側のある項目、`AllOf(NotEach(Size > 0))`は全側が存在して0 byteの項目に一致します。不在側のSize比較は`None`なので、そのslotは`None`のままです。

`NotEach`は配列リテラルや任意配列を作る公開APIを追加しません。通常の二者／三者metadata配列は不在側を含む2／3 slot、Windowsの`Prop`は既存Shell変換の値だけを使います。任意の空／入れ子／混在配列は既存property-context注入で照合します。式16,384文字・入れ子64・規則512・フィルターファイル1 MiBの既存上限を維持します。[固定native oracle](../../tests/Fixtures/FolderNotEachOracle/README.md)の67件はdirect functionの63件と独立arity blockの4件で、parserやGUIを実行した原典証拠ではありません。製品headlessはplan／goldenの全行をparserと注入contextへ対応付け、配列等値で全slotと型、spyで評価一回と例外伝播を照合する構成です。実CLI／GUIは二者／三者・不在側・実Shell比較・不正構文と表示保持を扱います。追加コードのbuild／E2E／自己検証は親工程での実行待ちです。

フォルダー`.flt`の`AllOf(expression)`は引数を一つだけ受け取ります。引数が配列なら直下の要素がすべてboolean trueのときだけ一致し、空配列は一致、false・非boolean・入れ子配列を含む配列は不一致です。scalar booleanはその値を返し、その他のscalarは`None`です。`AllOf`を含む規則は全側metadata配列を一度評価します。一般行変換の`AllOf`とは別の旧`.flt`関数です。

フォルダー`.flt`の`Len(expression)`は引数を一つ評価して整数を返します。配列は直下のslot数（入れ子を展開しない）、文字列は有効なUnicodeのcode point数、`None`は0、その他のscalarは1です。文字列のUTF-16 code unit数や見た目の文字数ではないため、`Len('😀') == 1`、`Len('é') == 2`です。全側metadata配列を一度評価するので、`Len(Name)`は不在側も含めた二者／三者の側数、`Len(LeftName)`は左の名前の文字数、不在側の`Len(RightName)`などは0です。`Prop(...)`配列、`AllOf`／`AllEqual`との合成にも対応します。関数名の大小文字と引数数の拒否は既存文法に従い、不正Unicodeの旧UTF-8 helperとの互換は対象外です。[固定native oracle](../../tests/Fixtures/FolderLenOracle/README.md)は38件を保持します。

## 表文書とセル編集

`StructuredComparer.ParseTable` は原文区間付き文書、`CompareTables` は二者／三者の共有行対応とセル判定、`ReplaceCell` は原文の単一区間を置換する編集を返します。旧 `ParseDelimited`・`CompareDelimited` は同じモデルのadapterです。行対応・容量・取消・比較設定の詳細は[表エディターの契約](../../Docs/TABLE-EDITOR.md#比較と原文の契約)を参照してください。

初期行対応は、比較設定を適用した復号セル値を長さ付きの行キーにまとめ、GNU行算法で一致組を求めます。三者の右→左は各統合変更区間だけを比較し、範囲外の一致を流用しません。変更ブロック内はraw WordDiffの一致文字量でbest-pairを選び、三者は01/12/20のmapを合成します。GNU分類・算法とraw処理は全ブロックで予算を共有し、候補数も制限します。`AlignmentWorkUsed`・`AlignmentFallbackReason` を返し、全文同値と共通端の線形確認は予算0でも行います。`ComparisonOptions.InlineCharacterLevel=false` は文字絞込みを省略し、テキスト行内区間と表行対応で単語区間を使います。外側の表行末は `CompareLineEndings=true` のとき差分になります。旧callerのraw CSV入力変換・引用内改行escapeを含む全行対応の完全互換は未確認です。

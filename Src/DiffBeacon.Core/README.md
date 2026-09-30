# DiffBeacon.Core

.NET 10 のマネージド比較・マージライブラリです。外部パッケージ、ネイティブ DLL、サブモジュール、実行時の反射・動的読込みを使用しません。`IsAotCompatible=true` により NativeAOT の解析対象になります。

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
- `ComparisonOptions`: `IgnoreCase`, `IgnoreWhitespace`, `Whitespace` (`None`, `Trim`, `IgnoreChanges`, `IgnoreAll`), `IgnoreBlankLines`, `IgnoreLinePattern`, `IgnoreFinalNewLine`, `CompareLineEndings`。`Normalize(string)` は公開ヘルパーです。正規表現は 250 ms タイムアウト付きで、不正パターン／タイムアウトを例外として返します。
- `DiffResult`: `Rows`, `Blocks`, `HasDifferences`, `LeftText`, `RightText`。`DiffRow` の左右行番号は 1 始まり・欠落側は null、`LeftText`/`RightText` は行末を含みません。`Kind` は `Equal`, `Added`, `Deleted`, `Modified`。変更行の `LeftSpans`/`RightSpans` は UTF-16 インデックスで、共通接頭辞・接尾辞を除いた変更範囲です。
- `DiffBlock`: `Index`, `RowStart`, `RowCount`, `LeftStart`, `LeftCount`, `RightStart`, `RightCount`。開始位置はすべて 0 始まり。コピー後は再比較し、更新前のブロックを再利用しないでください。除外行は `Equal` の表示行として残ります。改行なしの最終行を保持行の前後へ移す場合は、対象文書の改行を境界へ補い、行の連結を防ぎます。
- 一意な行を patience アンカーにし、残りは線形メモリの Hirschberg LCS で比較します。`MaxFallbackComparisons` (既定 4,000,000) を超える未解決区間は大きな変更ブロックになります。巨大な無関連文書でも N×M のテーブルは作りません。このフォールバックでは最小編集列を保証しません。

## 三者マージ

`ThreeWayMerger.Merge(baseText, left, right, options?, cancellationToken)` は `MergeResult` (`Text` / `MergedText`, `Conflicts`, `HasConflicts`) を返します。片側だけの変更と同じ変更は自動マージし、重なる異なる変更は `<<<<<<< LEFT` / `||||||| BASE` / `=======` / `>>>>>>> RIGHT` で明示します。`MergeConflict` には元の開始行・行数と BASE/LEFT/RIGHT の文字列があります。隣接する別の行への変更は競合にしません。

## ファイル・ディレクトリ・バイナリ

- `FileComparer.CompareAsync(leftPath, rightPath, options?, cancellationToken)` → `FileComparisonResult`: `IsBinary`, `TextDiff`, `BinaryDiff`, `LeftDocument`, `RightDocument`, `HasDifferences`。比較入力は各 256 MiB 上限。テキスト扱いの入力には追加で 64 MiB 上限があります。
- `BinaryDiffer.Compare(leftBytes, rightBytes, maxDifferenceRanges = 65536, cancellationToken)` → `BinaryDiffResult`: 左右サイズと差分範囲 (`Offset`, `LeftLength`, `RightLength`)。同じオフセットの異なるバイトを比較し、挿入の再整列はしません。範囲数上限では残りを一つの範囲へまとめ、`IsApproximate=true` にします。
- `DirectoryComparer.CompareAsync(leftPath, rightPath, options?, cancellationToken)` → `DirectoryComparisonResult.Entries` (`DirectoryEntry.RelativePath`, `LeftPath`, `RightPath`, `EntryKind`, `Kind` / `Status`, `LeftSize`, `RightSize`, `Error`)。状態は `Equal`, `Modified`, `LeftOnly`, `RightOnly`, `TypeConflict`, `Error`。
- `DirectoryComparisonOptions`: `Recursive`, `Mode` (`Content`, `Hash`, `TimeAndSize`), `ExcludePatterns`, `TextOptions`。通常の内容・SHA-256 比較はストリームで実行します。`TextOptions` を指定した Content モードではテキスト除外設定を適用します。
- 除外パターンは `*`, `?` に対応し、区切りなしパターンは全階層の basename に適用します。パス付きの `*` は区切りも含めて一致します。リンク／junction は表示とリンク先文字列の比較だけを行い、再帰せず、ループやルート外への探索を避けます。Windows のみパス照合で大文字・小文字を無視し、macOS/Linux では区別します。アクセス失敗は `Error` エントリ、キャンセルは `OperationCanceledException` です。

## パッチ・構造化テキスト

`UnifiedPatch.Create(leftText, rightText, leftPath, rightPath, contextLines = 3)` は一つのファイルの unified patch を作り、混在行末と最終改行なしマーカーを保持します。`Apply(text, patch)` は全 hunk の位置・行数・文脈を検証し、成功時だけ完成文字列を返します。不正／複数ファイルパッチは `FormatException`、文脈不一致は `InvalidOperationException`。`TryApply` は失敗時に元の文字列とエラーを返します。ファイルを直接書き換えないのでパッチ内パスを使ったファイルアクセスはありません。

`StructuredComparer.ParseDelimited(text, delimiter)` と `CompareDelimited(left, right, delimiter, options?, cancellationToken)` は CSV/TSV の引用符、二重引用符エスケープ、引用セル内の改行を扱います。比較は位置による行・列単位で、`CellDifference` の番号は 1 始まりです。キー列による行再整列は行いません。

`NormalizeJson(text, sortProperties = true)` / `CompareJson(left, right, options?)` はオブジェクトプロパティを ordinal ソートして整形します。配列の順番は保持し、数値は十進係数と指数で精度を失わずに正規化します (`1.0` と `1`、`1e2` と `100` は同じ結果)。巨大整数を double へ丸めません。構文不正／深さ 128 超／入力 16,777,216 文字超／数値 65,536 文字超／指数 512 文字超は `JsonException` です。

## 検証

`dotnet build Src/DiffBeacon.Core/DiffBeacon.Core.csproj` で AOT 互換性解析を含めてビルドします。アプリの E2E 検証は親プロジェクトが担当します。ライブラリ単体のビルド成功だけで全ファイルシステム／OS の互換性を保証しません。

## ファイルフィルター

`FileFilter.Load(path)` / `Parse(text, name)` / `ParseExpression(expression)`で旧`.flt`または式を読み、`DirectoryComparisonOptions.FileFilter`へ渡します。既定include/exclude、ファイル・ディレクトリ正規表現、名前・拡張子・サイズ・日時などの条件に対応します。各規則は入力上限・式の深さ上限・正規表現タイムアウトを持ちます。内容検索・関数・算術・左右の属性別評価やPCRE固有構文を含む旧エンジン全体との互換性は未完了です。

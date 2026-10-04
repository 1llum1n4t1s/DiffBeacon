using DiffBeacon.Core;

namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    private string TableSource(int side) => _baseText is null
        ? side == 0 ? LeftEditor.Text ?? "" : RightEditor.Text ?? ""
        : side switch { 0 => LeftEditor.Text ?? "", 1 => _baseText, _ => RightEditor.Text ?? "" };

    private bool TableReadOnly(int side) => _baseText is not null && side == 1 ||
        (side == 0 ? LeftEditor.IsReadOnly : RightEditor.IsReadOnly);

    private void WriteTableSource(int side, string text)
    {
        if (_tableSyntax is null) throw new InvalidOperationException("表設定の変更後は再比較してください。");
        if (TableReadOnly(side)) throw new InvalidOperationException("読取り専用のペインは編集できません。");
        if (side == 0) LeftEditor.Text = text; else RightEditor.Text = text;
    }

    // 直前に操作自身が書いた内容との一致を TablePanel が確認してから呼ぶ。
    // 途中の readonly 切替は新規編集を拒否するが、取消による原文復元は妨げない。
    private void RestoreTableSource(int side, string text)
    {
        if (side == 0) LeftEditor.Text = text; else RightEditor.Text = text;
    }

    private void EnsureNoPendingTableEdit()
    {
        if (_specialTab.Content is TablePanel table) table.EnsureNoPendingCellEdit();
    }

    private DelimitedSyntax? _tableSyntax;

    private async Task<TableComparisonResult> CompareTableAsync()
    {
        var count = _baseText is null ? 2 : 3;
        var sources = Enumerable.Range(0, count).Select(TableSource).ToArray();
        var syntax = _tableSyntax ?? new DelimitedSyntax(_projectMetadata.TableDelimiter ?? SpecializedViews.DetectSeparator(sources[0], sources[^1]),
            _projectMetadata.TableQuote ?? '"', _projectMetadata.TableAllowNewlinesInQuotes ?? true);
        var options = Options(); var token = _operation?.Token ?? CancellationToken.None;
        var result = await Task.Run(() => StructuredComparer.CompareTables(sources, syntax, options, token), token);
        if (sources.Where((source, side) => source != TableSource(side)).Any())
            throw new OperationCanceledException("表の比較中に本文が変更されました。");
        _tableSyntax = syntax;
        return result;
    }

    private async Task<(Avalonia.Controls.Control View, DelimitedSyntax? Syntax)> CreateTableViewAsync(
        TextDocument left, TextDocument right, TextDocument? ancestor, CancellationToken token)
    {
        try
        {
            var sources = ancestor is null ? new[] { left.Text, right.Text } : new[] { left.Text, ancestor.Text, right.Text };
            var syntax = new DelimitedSyntax(_projectMetadata.TableDelimiter ?? SpecializedViews.DetectSeparator(sources[0], sources[^1]),
                _projectMetadata.TableQuote ?? '"', _projectMetadata.TableAllowNewlinesInQuotes ?? true);
            var options = Options();
            var comparison = await Task.Run(() => StructuredComparer.CompareTables(sources, syntax, options, token), token);
            TableReadyForAdoption?.Invoke();
            string Caption(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
            var names = ancestor is null
                ? new[] { Caption(_projectMetadata.LeftDescription, "左"), Caption(_projectMetadata.RightDescription, "右") }
                : new[] { Caption(_projectMetadata.LeftDescription, "左"), Caption(_projectMetadata.BaseDescription, "祖先（読取り専用）"), Caption(_projectMetadata.RightDescription, "右") };
            return (new TablePanel(comparison, TableSource, TableReadOnly, WriteTableSource, RestoreTableSource, CompareTableAsync, names,
                () => _operation?.Token ?? CancellationToken.None), comparison.Documents[0].Syntax);
        }
        catch (FormatException exception)
        { return (new Avalonia.Controls.TextBlock { Text = exception.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, null); }
    }
}

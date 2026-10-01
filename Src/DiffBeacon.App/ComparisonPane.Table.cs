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
        if (TableReadOnly(side)) throw new InvalidOperationException("読取り専用のペインは編集できません。");
        if (side == 0) LeftEditor.Text = text; else RightEditor.Text = text;
    }

    private async Task<TableComparisonResult> CompareTableAsync()
    {
        var count = _baseText is null ? 2 : 3;
        var sources = Enumerable.Range(0, count).Select(TableSource).ToArray();
        var syntax = new DelimitedSyntax(_projectMetadata.TableDelimiter ?? SpecializedViews.DetectSeparator(sources[0], sources[^1]),
            _projectMetadata.TableQuote ?? '"', _projectMetadata.TableAllowNewlinesInQuotes ?? true);
        var options = Options(); var token = _operation?.Token ?? CancellationToken.None;
        var result = await Task.Run(() => StructuredComparer.CompareTables(sources, syntax, options, token), token);
        if (sources.Where((source, side) => source != TableSource(side)).Any())
            throw new OperationCanceledException("表の比較中に本文が変更されました。");
        return result;
    }

    private async Task OpenTableAsync()
    {
        try
        {
            var comparison = await CompareTableAsync();
            string Caption(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
            var names = _baseText is null
                ? new[] { Caption(_projectMetadata.LeftDescription, "左"), Caption(_projectMetadata.RightDescription, "右") }
                : new[] { Caption(_projectMetadata.LeftDescription, "左"), Caption(_projectMetadata.BaseDescription, "祖先（読取り専用）"), Caption(_projectMetadata.RightDescription, "右") };
            SetSpecialView(new TablePanel(comparison, TableSource, TableReadOnly, WriteTableSource, CompareTableAsync, names,
                () => _operation?.Token ?? CancellationToken.None));
        }
        catch (FormatException exception)
        { SetSpecialView(new Avalonia.Controls.TextBlock { Text = exception.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }); }
        _views.SelectedItem = _specialTab;
    }
}

using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

public sealed partial class MainWindow
{
    private bool _openingWorkspace;
    public IReadOnlyList<ComparisonPane> SessionPanes => _sessions.Select(item => (ComparisonPane)item.Content!).ToArray();
    public void SelectSession(int index) => _tabs.SelectedItem = _sessions[index];

    public Task SaveWorkspaceAsync(string path, CancellationToken token = default)
    {
        foreach (var pane in SessionPanes) pane.EnsureProjectOutputWritable(path);
        return WorkspaceStore.SaveWorkspaceAsync(path,
            new ComparisonWorkspace { Entries = SessionPanes.Select(pane => pane.CaptureProject()).ToArray(), ActiveEntryIndex = _sessions.IndexOf((TabItem)_tabs.SelectedItem!) }, token);
    }

    public async Task<bool> OpenWorkspaceAsync(string path, bool discardChanges = false, CancellationToken token = default)
    {
        if (_openingWorkspace) throw new InvalidOperationException("プロジェクトを開いています。");
        _openingWorkspace = true;
        var prepared = new List<ComparisonPane>();
        try
        {
            // 全設定を検証してから既存タブを置換し、読込み失敗では編集内容を残す。
            var workspace = await WorkspaceStore.LoadWorkspaceAsync(path, token);
            foreach (var project in workspace.Entries)
            {
                var pane = new ComparisonPane(this);
                prepared.Add(pane); pane.ApplyProject(project);
            }
            token.ThrowIfCancellationRequested();
            if (!discardChanges && SessionPanes.Any(pane => pane.HasUnsavedChanges)
                && !await Dialogs.ConfirmAsync(this, "未保存の変更", "すべての比較タブをプロジェクトで置き換えます。編集内容を破棄しますか？")) return false;
            token.ThrowIfCancellationRequested();
            foreach (var old in SessionPanes) old.Dispose();
            _sessions.Clear(); _tabs.ItemsSource = null;
            foreach (var pane in prepared) AttachProjectSession(pane);
            prepared.Clear();
            _tabs.SelectedItem = _sessions[workspace.ActiveEntryIndex];
            foreach (var pane in SessionPanes) await pane.CompareProjectAsync();
            return true;
        }
        finally { foreach (var pane in prepared) pane.Dispose(); _openingWorkspace = false; }
    }

    private void AttachProjectSession(ComparisonPane pane)
    {
        var project = pane.CaptureProject();
        var title = ProjectTitle(project, _sessions.Count);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(new TextBlock { Text = title, MaxWidth = 220, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "×", Padding = new Thickness(5, 0) }; header.Children.Add(close);
        var item = new TabItem { Header = header, Content = pane };
        close.Click += async (_, _) =>
        {
            if (pane.HasUnsavedChanges && !await Dialogs.ConfirmAsync(this, "未保存の変更", "変更を保存せずにタブを閉じますか？")) return;
            _sessions.Remove(item); pane.Dispose(); _tabs.ItemsSource = _sessions.ToArray();
            if (_sessions.Count == 0) AddSession(); else _tabs.SelectedItem = _sessions[^1];
        };
        _sessions.Add(item); _tabs.ItemsSource = _sessions.ToArray();
    }
    private static string ProjectTitle(ComparisonProject project, int index) => !string.IsNullOrWhiteSpace(project.LeftDescription) ? project.LeftDescription
        : !string.IsNullOrWhiteSpace(project.RightDescription) ? project.RightDescription : $"比較 {index + 1}";
    internal void RefreshSessionHeaders()
    {
        for (var index = 0; index < _sessions.Count; index++)
            if (_sessions[index].Header is StackPanel panel)
                panel.Children.OfType<TextBlock>().Single().Text = ProjectTitle(((ComparisonPane)_sessions[index].Content!).CaptureProject(), index);
    }
}

public sealed partial class ComparisonPane
{
    private ComparisonProject _projectMetadata = new();
    private static readonly string[] ModeNames = ["Auto", "Text", "Folder", "Binary", "Image", "Json", "Table", "Archive", "Provider"];

    public ComparisonProject CaptureProject() => _projectMetadata with
    {
        LeftPath = LeftPath.Text ?? "", BasePath = BasePath.Text ?? "", RightPath = RightPath.Text ?? "",
        Mode = _mode.SelectedIndex == 8 && (_provider.SelectedItem as string == "web-text" || (_provider.SelectedItem is null && _projectMetadata.Mode.Equals("Web", StringComparison.OrdinalIgnoreCase))) ? "Web" : ModeNames[Math.Clamp(_mode.SelectedIndex, 0, ModeNames.Length - 1)],
        TableDelimiter = _mode.SelectedIndex == 6 ? _tableSyntax?.Delimiter ?? _projectMetadata.TableDelimiter : _projectMetadata.TableDelimiter,
        ProviderId = _provider.SelectedItem as string ?? _projectMetadata.ProviderId, FileFilterPath = _fileFilter.Text,
        Recursive = _recursive.IsChecked == true, FolderMode = _folderMode.SelectedIndex switch { 1 => "Hash", 2 => "TimestampAndSize", _ => "Content" },
        ExcludedPaths = _excludes.Text, IgnoreCase = _ignoreCase.IsChecked == true, IgnoreWhitespace = _ignoreSpace.IsChecked == true,
        IgnoreBlankLines = _ignoreBlank.IsChecked == true, IgnoreLinePattern = _ignoreRegex.Text,
        IgnoreNumbers = _ignoreNumbers.IsChecked == true, CommentSyntax = (CommentSyntax)_comments.SelectedIndex,
        Whitespace = (WhitespaceMode)_whitespace.SelectedIndex, SubstitutionRules = _substitutions.ToArray()
    };

    public void ApplyProject(ComparisonProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _tableSyntax = null;
        _projectMetadata = project with { LegacySettings = new(project.LegacySettings), SubstitutionRules = project.SubstitutionRules.ToArray() };
        LeftPath.Text = project.LeftPath; BasePath.Text = project.BasePath; RightPath.Text = project.RightPath;
        var mode = Array.FindIndex(ModeNames, name => name.Equals(project.Mode, StringComparison.OrdinalIgnoreCase));
        if (project.Mode.Equals("Web", StringComparison.OrdinalIgnoreCase)) mode = 8;
        if (mode < 0 && int.TryParse(project.Mode, out var oldIndex) && oldIndex is >= 0 and <= 8) mode = oldIndex;
        _mode.SelectedIndex = mode >= 0 ? mode : throw new InvalidDataException($"未対応の比較形式です: {project.Mode}");
        if (project.ProviderId is not null)
        {
            if (_providers.Providers.Any(provider => provider.Id == project.ProviderId)) _provider.SelectedItem = project.ProviderId;
            else { _provider.SelectedIndex = -1; _status.Text = $"比較ツール {project.ProviderId} は未登録です。利用するツールを明示選択してください。"; }
        }
        else if (project.Mode.Equals("Web", StringComparison.OrdinalIgnoreCase)) _provider.SelectedItem = "web-text";
        _fileFilter.Text = project.FileFilterPath; _recursive.IsChecked = project.Recursive; _excludes.Text = project.ExcludedPaths;
        _folderMode.SelectedIndex = project.FolderMode switch { "Content" => 0, "Hash" => 1, "TimestampAndSize" => 2, _ => throw new InvalidDataException("フォルダー比較方式が不正です。") };
        _ignoreCase.IsChecked = project.IgnoreCase; _ignoreSpace.IsChecked = project.IgnoreWhitespace;
        _ignoreBlank.IsChecked = project.IgnoreBlankLines; _ignoreRegex.Text = project.IgnoreLinePattern;
        SetAdvancedFilters(project.IgnoreNumbers, project.CommentSyntax, project.Whitespace, project.SubstitutionRules);
        LeftEditor.IsReadOnly = project.LeftReadOnly; RightEditor.IsReadOnly = project.RightReadOnly;
        _leftCaption.Text = ProjectCaption(false); _rightCaption.Text = ProjectCaption(true);
        ToolTip.SetTip(LeftPath, ProjectCaption(false)); ToolTip.SetTip(RightPath, ProjectCaption(true));
        UpdateEditorLayout(!string.IsNullOrWhiteSpace(project.BasePath));
    }

    public async Task CompareProjectAsync()
    {
        try
        {
            if (_mode.SelectedIndex == 8 && _provider.SelectedItem is not string)
                throw new InvalidOperationException($"比較ツール {_projectMetadata.ProviderId} は未登録です。実行するツールを選択してください。");
            await ComparePathsAsync();
        }
        catch (OperationCanceledException) { _status.Text = "比較を中止しました。"; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { _status.Text = exception.Message; }
        if (_projectMetadata.LegacySettings.Count > 0)
            _status.Text += " · 未適用の旧設定を保持しています: " + string.Join(", ", _projectMetadata.LegacySettings.Keys);
    }

    private void EnsureSideWritable(bool right)
    {
        if (right ? _projectMetadata.RightReadOnly : _projectMetadata.LeftReadOnly)
            throw new InvalidOperationException($"{(right ? "右" : "左")}はプロジェクトで読取り専用に指定されています。");
    }

    private string ProjectCaption(bool right)
    {
        var description = right ? _projectMetadata.RightDescription : _projectMetadata.LeftDescription;
        var readOnly = right ? _projectMetadata.RightReadOnly : _projectMetadata.LeftReadOnly;
        return (string.IsNullOrWhiteSpace(description) ? right ? "右" : "左" : description) + (readOnly ? "（読取り専用）" : "");
    }

    internal void EnsureProjectOutputWritable(string path)
    {
        foreach (var (source, readOnly) in new[] { (LeftPath.Text, _projectMetadata.LeftReadOnly), (BasePath.Text, _projectMetadata.BaseReadOnly), (RightPath.Text, _projectMetadata.RightReadOnly) })
        {
            if (!readOnly || string.IsNullOrWhiteSpace(source)) continue;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile) continue;
            if (ArchivePaths.SameFile(source, path))
                throw new InvalidOperationException("読取り専用に指定された入力を上書きできません。");
            if (Directory.Exists(source))
                for (var current = Path.GetDirectoryName(Path.GetFullPath(path)); current is not null; current = Path.GetDirectoryName(current))
                    if (ArchivePaths.SameFile(source, current)) throw new InvalidOperationException("読取り専用に指定されたフォルダー内へ保存できません。");
        }
    }

    private FileFilter? ResolveProjectFilter()
    {
        if (!string.IsNullOrWhiteSpace(_fileFilter.Text)) return FileFilter.Load(_fileFilter.Text);
        var legacy = _projectMetadata.LegacyFilter?.Trim();
        if (string.IsNullOrWhiteSpace(legacy)) return null;
        if (legacy.Length > 16_384) throw new FormatException("ファイルフィルターは16,384文字以下にしてください。");
        if (new[] { "fe:", "de:", "e:", "fe!:", "de!:", "e!:" }.Any(prefix => legacy.StartsWith(prefix, StringComparison.Ordinal)))
            return FileFilter.Parse(legacy);
        if (legacy.Contains(':') || legacy.Contains('|') || legacy.Contains('!') || legacy.Contains('/') || legacy.Contains('\\'))
            throw new FormatException("この旧ファイルフィルターは未対応です。比較の設定から対応する .flt を選択してください。");
        var masks = legacy.Split([';', ' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (masks.Length is 0 or > 512) throw new FormatException("ファイルマスクは1〜512個にしてください。");
        var pattern = string.Join('|', masks.Select(mask => mask is "*" or "*.*" ? ".*" : Regex.Escape(mask).Replace("\\*", ".*").Replace("\\?", ".")));
        return FileFilter.Parse("def: exclude\nd: .*\nf: ^(?:" + pattern + ")$");
    }

    private async Task EditProjectOptionsAsync()
    {
        var project = CaptureProject();
        var dialog = new Window { Title = "比較の設定", Width = 540, Height = 590, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        TextBox Field(string title, string? value) { panel.Children.Add(new TextBlock { Text = title }); var box = new TextBox { Text = value }; panel.Children.Add(box); return box; }
        var left = Field("左の説明", project.LeftDescription); var middle = Field("祖先の説明", project.BaseDescription); var right = Field("右の説明", project.RightDescription);
        var leftRo = new CheckBox { Content = "左を読取り専用にする", IsChecked = project.LeftReadOnly };
        var baseRo = new CheckBox { Content = "祖先ファイルへの上書きを禁止する", IsChecked = project.BaseReadOnly };
        var rightRo = new CheckBox { Content = "右を読取り専用にする", IsChecked = project.RightReadOnly };
        panel.Children.Add(leftRo); panel.Children.Add(baseRo); panel.Children.Add(rightRo);
        var filter = Field("ファイルマスク／式（.flt指定がある場合はそちらを使用）", project.LegacyFilter);
        var delimiter = Field("表の区切り文字（空欄: 自動、\\t: タブ）", project.TableDelimiter?.ToString());
        var quote = Field("表の引用符（空欄: ダブルクォート）", project.TableQuote?.ToString());
        var multiline = new CheckBox { Content = "引用符内の改行を許可する", IsChecked = project.TableAllowNewlinesInQuotes ?? true }; panel.Children.Add(multiline);
        if (project.LegacySettings.Count > 0) panel.Children.Add(new TextBlock { Text = "未適用の旧設定: " + string.Join(", ", project.LegacySettings.Keys), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "キャンセル" }; cancel.Click += (_, _) => dialog.Close(); buttons.Children.Add(cancel);
        var save = new Button { Content = "適用" }; buttons.Children.Add(save); panel.Children.Add(buttons);
        save.Click += (_, _) =>
        {
            try
            {
                char? Character(string? value) => string.IsNullOrEmpty(value) ? null : value == "\\t" ? '\t' : value.Length == 1 ? value[0] : throw new FormatException("区切り文字と引用符は1文字で指定してください。");
                var delimiterValue = Character(delimiter.Text); var quoteValue = Character(quote.Text);
                StructuredComparer.ParseDelimited("", delimiterValue ?? ',', quoteValue ?? '"', multiline.IsChecked == true);
                _tableSyntax = null;
                _projectMetadata = project with { LeftDescription = left.Text, BaseDescription = middle.Text, RightDescription = right.Text,
                    LeftReadOnly = leftRo.IsChecked == true, BaseReadOnly = baseRo.IsChecked == true, RightReadOnly = rightRo.IsChecked == true,
                    LegacyFilter = filter.Text, TableDelimiter = delimiterValue, TableQuote = quoteValue, TableAllowNewlinesInQuotes = multiline.IsChecked == true };
                LeftEditor.IsReadOnly = !_textSaveAllowed || _projectMetadata.LeftReadOnly; RightEditor.IsReadOnly = !_textSaveAllowed || _projectMetadata.RightReadOnly;
                SpecializedViews.SetProjectReadOnly(_specialTab.Content as Control, _projectMetadata.LeftReadOnly, _projectMetadata.RightReadOnly);
                _leftCaption.Text = ProjectCaption(false); _rightCaption.Text = ProjectCaption(true);
                if (_owner is MainWindow owner) owner.RefreshSessionHeaders();
                UpdateEditorLayout(_baseText is not null); _status.Text = "比較の設定を適用しました。表・フィルターの変更は再比較で反映されます。";
                dialog.Close();
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { error.Text = ex.Message; }
        };
        dialog.Content = new ScrollViewer { Content = panel }; await dialog.ShowDialog(_owner);
    }
}

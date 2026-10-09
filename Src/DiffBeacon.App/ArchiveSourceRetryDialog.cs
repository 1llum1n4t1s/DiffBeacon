using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DiffBeacon.App;

// 保存データには含めず、利用者が再試行した階層だけへ秘密値を渡す。
internal sealed class ArchiveSourceRetryDialog : Window
{
    private readonly TextBox[][] _passwords;
    private readonly ComboBox[][] _nameCodePages;
    private readonly ComboBox[][] _payloadKinds;
    private readonly ComboBox[][] _compressionPayloadKinds;
    internal IReadOnlyList<ComboBox> LeftCompressionPayloadKinds => _compressionPayloadKinds[0];
    internal IReadOnlyList<ComboBox> BaseCompressionPayloadKinds => _compressionPayloadKinds[1];
    internal IReadOnlyList<ComboBox> RightCompressionPayloadKinds => _compressionPayloadKinds[2];
    internal IReadOnlyList<ComboBox> LeftPayloadKinds => _payloadKinds[0];
    internal IReadOnlyList<ComboBox> BasePayloadKinds => _payloadKinds[1];
    internal IReadOnlyList<ComboBox> RightPayloadKinds => _payloadKinds[2];
    internal ComparisonProject RequestedProject { get; }
    internal IReadOnlyList<TextBox> LeftPasswords => _passwords[0];
    internal IReadOnlyList<TextBox> BasePasswords => _passwords[1];
    internal IReadOnlyList<TextBox> RightPasswords => _passwords[2];
    internal Button Retry { get; } = new() { Content = "再試行", Margin = new Thickness(4) };
    internal Button Cancel { get; } = new() { Content = "キャンセル", Margin = new Thickness(4) };
    internal ArchiveSourceRetryDialog(ComparisonProject project, string?[][] passwords, bool allowNameRetry = false, bool allowPayloadRetry = true)
    {
        RequestedProject = WorkspaceStore.CloneProject(project);
        Title = "内包項目を開けませんでした"; Width = 650; Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(16) };
        var status = new TextBlock { Text = "読込みを検証できませんでした。原本・格納名・形式・パスワードを確認して再試行してください。パスワードはプロジェクトに保存されません。", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Cancel); buttons.Children.Add(Retry); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var fields = new StackPanel { Spacing = 6 }; _passwords = new TextBox[3][];
        _nameCodePages = new ComboBox[3][]; _payloadKinds = new ComboBox[3][]; _compressionPayloadKinds = new ComboBox[3][];
        foreach (var side in Enumerable.Range(0, 3))
        {
            var input = ProjectInputs.Archive(project, side);
            var names = input is null ? new[] { System.IO.Path.GetFileName(ProjectInputs.PathFor(project, side)) }
                : new[] { System.IO.Path.GetFileName(input.RootPath) }.Concat(input.EntryChain).ToArray();
            _passwords[side] = new TextBox[names.Length];
            _nameCodePages[side] = new ComboBox[input is null || !allowNameRetry ? 0 : names.Length];
            _payloadKinds[side] = new ComboBox[input is null || !allowPayloadRetry ? 0 : names.Length];
            _compressionPayloadKinds[side] = new ComboBox[input is null || !allowPayloadRetry ? 0 : names.Length];
            for (var layer = 0; layer < names.Length; layer++)
            {
                var box = new TextBox { PasswordChar = '●', MaxLength = 4096, Text = passwords[side][layer] };
                _passwords[side][layer] = box;
                if (string.IsNullOrWhiteSpace(ProjectInputs.PathFor(project, side))) continue;
                fields.Children.Add(new TextBlock { Text = $"{(side == 0 ? "左" : side == 1 ? project.Mode == "Binary" || ProjectInputs.IsIndependentText(project) ? "中央" : "祖先" : "右")} · {names[layer]}" }); fields.Children.Add(box);
                if (input is not null && allowPayloadRetry)
                {
                    var payloadPicker = ArchivePayloadSettings.Picker(ArchivePayloadSettings.Choices(input.ContainerGZipPayloadKinds, names.Length).ElementAt(layer));
                    _payloadKinds[side][layer] = payloadPicker;
                    fields.Children.Add(new TextBlock { Text = "gzip本文の形式（このコンテナーだけ）" }); fields.Children.Add(payloadPicker);
                    var compressionPicker = ArchivePayloadSettings.CompressionPicker(ArchivePayloadSettings.CompressionChoices(input.ContainerCompressionPayloadKinds, names.Length).ElementAt(layer));
                    _compressionPayloadKinds[side][layer] = compressionPicker;
                    fields.Children.Add(new TextBlock { Text = "BZip2／Z本文の形式（このコンテナーだけ）" }); fields.Children.Add(compressionPicker);
                }
                if (input is not null && allowNameRetry)
                {
                    var picker = ArchiveNameSettings.Picker(input.ContainerNameCodePages?[layer] ?? 28591);
                    _nameCodePages[side][layer] = picker;
                    fields.Children.Add(new TextBlock { Text = "gzip格納名の文字コード (28591 Latin1 / 65001 UTF-8 / 932 日本語)" }); fields.Children.Add(picker);
                }
            }
        }
        root.Children.Add(new ScrollViewer { Content = fields }); Content = root;
        Cancel.Click += (_, _) => Close();
        Retry.Click += (_, _) =>
        {
            if (_passwords.SelectMany(side => side).Any(box => box.Text?.Length > 4096)) { status.Text = "各パスワードは4096文字以下にしてください。"; return; }
            Close(_passwords.Select(side => side.Select(box => string.IsNullOrEmpty(box.Text) ? null : box.Text).ToArray()).ToArray());
        };
        Closed += (_, _) => ClearPasswords();
    }
    internal void ClearPasswords() { foreach (var box in _passwords.SelectMany(side => side)) box.Text = ""; }
    internal void ApplyNameChoices(ComparisonProject project)
    {
        foreach (var side in Enumerable.Range(0, 3))
            if (ProjectInputs.Archive(project, side) is { } input)
            {
                if (_nameCodePages[side].Length != 0) input.ContainerNameCodePages = ArchiveNameSettings.Capture(_nameCodePages[side].Select(ArchiveNameSettings.Selected).ToArray());
                if (_payloadKinds[side].Length != 0) input.ContainerGZipPayloadKinds = ArchivePayloadSettings.Capture(_payloadKinds[side].Select(ArchivePayloadSettings.Selected).ToArray());
                if (_compressionPayloadKinds[side].Length != 0) input.ContainerCompressionPayloadKinds = ArchivePayloadSettings.Capture(_compressionPayloadKinds[side].Select(ArchivePayloadSettings.SelectedCompression).ToArray());
            }
    }
}

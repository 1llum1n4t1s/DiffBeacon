namespace DiffBeacon.App;

public sealed partial class ComparisonPane
{
    private IEnumerable<ComparisonPane> ImageProtectionPanes() => ((_owner as MainWindow)?.SessionPanes ?? []).Append(this).Distinct();
    private IEnumerable<string> CurrentImageProtectedPaths()
    {
        var window = _owner as MainWindow;
        foreach (var pane in ImageProtectionPanes())
        {
            var project = pane.CaptureProject();
            var comparedPaths = pane._lastPackageComparison is { } compared ? new[] { compared.Left, compared.Base, compared.Right } : [];
            var origins = (pane._specialTab.Content as SpecializedViews.ImagePanel)?.SourceProtectionPaths ?? [];
            foreach (var source in new[] { project.LeftPath, project.BasePath, project.RightPath, project.FileFilterPath }.Concat(comparedPaths).Concat(origins))
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
                {
                    if (!uri.IsFile) continue;
                    yield return uri.LocalPath;
                }
                else yield return source;
            }
        }
        if (!string.IsNullOrWhiteSpace(window?.WorkspaceSourcePath)) yield return window.WorkspaceSourcePath;
    }

    private void ConfigureImageEditing(SpecializedViews.ImagePanel image)
    {
        var project = CaptureProject();
        var three = image.MiddleFrameCount.HasValue;
        image.ConfigureEditing(three ? [project.LeftPath, project.BasePath!, project.RightPath] : [project.LeftPath, project.RightPath],
            three ? [project.LeftReadOnly, project.BaseReadOnly, project.RightReadOnly] : [project.LeftReadOnly, project.RightReadOnly],
            CurrentImageProtectedPaths, path =>
            {
                foreach (var pane in ImageProtectionPanes()) pane.EnsureProjectOutputWritable(path);
            }, (pane, path) =>
            {
                // 比較済み状態は保存した側だけ切り替え、他側の未比較の手入力は認証しない。
                if (pane == 0) LeftPath.Text = path;
                else if (three && pane == 1) BasePath.Text = path;
                else RightPath.Text = path;
                if (_lastPackageComparison is { } comparison)
                    _lastPackageComparison = (pane == 0 ? path : comparison.Left,
                        three && pane == 1 ? path : comparison.Base,
                        pane == (three ? 2 : 1) ? path : comparison.Right, comparison.Mode, comparison.Provider);
                (_owner as MainWindow)?.RefreshSessionHeaders();
            });
    }
}

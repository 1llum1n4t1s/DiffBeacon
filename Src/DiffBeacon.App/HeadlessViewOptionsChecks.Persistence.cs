using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DiffBeacon.App;

/// <summary>
/// 表示メニューの設定の保存と復元（TextViewOptionsStore、view-options.json）の自己検証。
/// 保存先はすべてこの run の出力の中に作り、利用者の AppData には触れない。期待値は実装の関数を使わずに書いた JSON の値と照合する。
/// </summary>
internal static partial class HeadlessViewOptionsChecks
{
    private static readonly string[] ViewOptionNames = ["view-whitespace", "view-eol", "view-line-differences", "view-line-numbers", "view-margins", "view-toolbar"];

    private static void CheckPersistence(string work, Action<string, bool, string> check)
    {
        var folder = Path.Combine(work, "persistence"); Directory.CreateDirectory(folder);
        CheckRoundTrip(folder, check);
        CheckDefaultsAndPartialFiles(folder, check);
        CheckInvalidFiles(folder, check);
        CheckSaveFailures(folder, check);
    }

    /// <summary>
    /// 一つ目のウィンドウでメニュー・キーから設定を変えて閉じ、同じ保存先で開いた二つ目のウィンドウの最初のタブ・メニュー・ツールバーが
    /// その設定で始まること。コンストラクターの中で作られる最初のタブへ届くか（読込みの順序）を確かめる唯一の経路。
    /// </summary>
    private static void CheckRoundTrip(string folder, Action<string, bool, string> check)
    {
        var path = Path.Combine(folder, "view-options.json");
        var first = OpenWindow(new TextViewOptionsStore(path));
        try
        {
            var options = first.TextViewOptions;
            check("view settings start from defaults when no file exists", !File.Exists(path) && options.ZoomSteps == 0 && first.FrameToolbarVisible
                && first.TextViewOptionsStorage.Diagnostic is null && first.TextViewOptionsStorage.SaveAttemptCount == 0,
                $"exists={File.Exists(path)} zoom={options.ZoomSteps} toolbar={first.FrameToolbarVisible} saves={first.TextViewOptionsStorage.SaveAttemptCount}");
            options.Zoom(1); options.Zoom(1); options.Zoom(1);
            foreach (var name in ViewOptionNames) Click(MenuOption(first, name));
            check("view settings are saved on every change", first.TextViewOptionsStorage.SaveAttemptCount == 9 && first.TextViewOptionsStorage.LastSaveResult == true
                && !first.FrameToolbarVisible && File.Exists(path), $"saves={first.TextViewOptionsStorage.SaveAttemptCount} diagnostic={first.TextViewOptionsStorage.Diagnostic}");
        }
        finally { Close(first); }

        // 保存した内容は JSON の値として確かめる（実装の写し方に頼らない）。
        var text = File.ReadAllText(path);
        using (var json = JsonDocument.Parse(text))
        {
            var root = json.RootElement;
            check("view settings file holds the WinMerge view options", root.GetProperty("zoomSteps").GetInt32() == 3
                && root.GetProperty("viewWhitespace").GetBoolean() && root.GetProperty("viewEndOfLine").GetBoolean()
                && !root.GetProperty("viewLineDifferences").GetBoolean() && !root.GetProperty("viewLineNumbers").GetBoolean()
                && !root.GetProperty("viewMargins").GetBoolean() && !root.GetProperty("showToolbar").GetBoolean() && root.EnumerateObject().Count() == 7, text);
        }

        var bytes = File.ReadAllBytes(path);
        var store = new TextViewOptionsStore(path);
        var second = OpenWindow(store);
        try
        {
            var pane = second.SessionPanes[0];
            var checks = ViewOptionNames.Select(name => MenuOption(second, name).IsChecked).ToArray();
            check("view settings restore in the first tab, menu and toolbar of a new window", Near(pane.DiffEditor(0).FontSize, 13 + 4)
                && Near(pane.DiffEditor(2).FontSize, 13 + 4) && pane.DiffEditor(0).Options.ShowSpaces && pane.DiffEditor(2).Options.ShowTabs
                && !pane.DiffEditorMarginSeparatorVisible(0) && checks.SequenceEqual([true, true, false, false, false, false]) && !second.FrameToolbarVisible,
                $"font={pane.DiffEditor(0).FontSize} spaces={pane.DiffEditor(0).Options.ShowSpaces} checks={string.Join(",", checks)} toolbar={second.FrameToolbarVisible}");
            check("view settings loading does not write the file", store.SaveAttemptCount == 0 && bytes.SequenceEqual(File.ReadAllBytes(path)), $"saves={store.SaveAttemptCount}");
            // 復元した後も、メニューから戻すとその値で保存し直す。
            Click(MenuOption(second, "view-toolbar"));
            check("view settings restored toolbar can be shown again and saved", second.FrameToolbarVisible && new TextViewOptionsStore(path).Current.ShowToolbar,
                File.ReadAllText(path));
        }
        finally { Close(second); }

        // 保存先の場所は ForDesktop へ渡した AppData の下の Kagayoi/DiffBeacon（実際の利用者のフォルダーではなく run の中で確かめる）。
        var appData = Path.Combine(folder, "appdata");
        var desktop = TextViewOptionsStore.ForDesktop(appData);
        check("view settings desktop path is under Kagayoi DiffBeacon", desktop.Path == Path.Combine(appData, "Kagayoi", "DiffBeacon", "view-options.json")
            && desktop.Diagnostic is null && !Directory.Exists(appData), desktop.Path ?? "null");
    }

    /// <summary>{} と一部だけの設定は、書かれていない項目を既定値（行内差異・行番号・マージン・ツールバーは表示）で補う。範囲の端は受け付ける。</summary>
    private static void CheckDefaultsAndPartialFiles(string folder, Action<string, bool, string> check)
    {
        var empty = Seed(folder, "empty.json", "{}");
        var defaults = new TextViewOptionsStore(empty).Current;
        check("view settings empty object uses defaults", defaults is { ZoomSteps: 0, ViewWhitespace: false, ViewEndOfLine: false, ViewLineDifferences: true,
            ViewLineNumbers: true, ViewMargins: true, ShowToolbar: true } && new TextViewOptionsStore(empty).Diagnostic is null, defaults.ToString());
        var partial = new TextViewOptionsStore(Seed(folder, "partial.json", "{\"viewWhitespace\":true,\"zoomSteps\":-7}")).Current;
        var maximum = new TextViewOptionsStore(Seed(folder, "maximum.json", "{\"zoomSteps\":68}"));
        check("view settings partial file keeps other defaults and accepts the zoom limits", partial is { ViewWhitespace: true, ZoomSteps: -7, ViewLineDifferences: true,
            ViewLineNumbers: true, ViewMargins: true, ShowToolbar: true } && maximum.Current.ZoomSteps == 68 && maximum.Diagnostic is null,
            partial + " max=" + maximum.Current.ZoomSteps);
    }

    /// <summary>読めない設定は既定値で表示し、ファイルは書き換えない（ウィンドウを開いても書き戻さない）。</summary>
    private static void CheckInvalidFiles(string folder, Action<string, bool, string> check)
    {
        var invalid = new[]
        {
            "null", "{", "[]", "{\"zoomSteps\":69}", "{\"zoomSteps\":-8}", "{\"other\":1}", "{\"viewWhitespace\":null}", "{\"zoomSteps\":\"1\"}",
            "{\"zoomSteps\":1.5}", new string(' ', TextViewOptionsStore.MaximumBytes + 1),
        };
        var index = 0;
        foreach (var text in invalid)
        {
            var path = Seed(folder, $"invalid-{index++}.json", text);
            var before = File.ReadAllBytes(path);
            var store = new TextViewOptionsStore(path);
            check($"view settings invalid file {index} falls back to defaults and keeps the file", store.Diagnostic is not null && store.Current == new TextViewSettings()
                && before.SequenceEqual(File.ReadAllBytes(path)), (text.Length > 60 ? text[..60] : text) + " => " + store.Diagnostic);
        }
        var directory = Path.Combine(folder, "directory.json"); Directory.CreateDirectory(directory);
        var directoryStore = new TextViewOptionsStore(directory);
        check("view settings directory at the path falls back to defaults and refuses to save", directoryStore.Diagnostic is not null
            && !directoryStore.Save(new TextViewSettings { ViewWhitespace = true }) && Directory.Exists(directory), directoryStore.Diagnostic ?? "");

        // 壊れた設定でウィンドウを開くと既定の表示で始まり、開いただけではファイルを上書きしない。
        var broken = Seed(folder, "broken-window.json", "{\"zoomSteps\":999,\"viewWhitespace\":true}");
        var brokenBytes = File.ReadAllBytes(broken);
        var window = OpenWindow(new TextViewOptionsStore(broken));
        try
        {
            var pane = window.SessionPanes[0];
            check("view settings broken file opens a window with defaults without rewriting", Near(pane.DiffEditor(0).FontSize, 13) && !pane.DiffEditor(0).Options.ShowSpaces
                && window.FrameToolbarVisible && window.TextViewOptionsStorage.SaveAttemptCount == 0 && brokenBytes.SequenceEqual(File.ReadAllBytes(broken)),
                $"font={pane.DiffEditor(0).FontSize} saves={window.TextViewOptionsStorage.SaveAttemptCount}");
        }
        finally { Close(window); }
    }

    /// <summary>
    /// 保存できないとき（読取り専用・出力保護）は理由を状態欄へ出し、その起動の間は変えた表示を使い続ける。ファイルは変えない。
    /// </summary>
    private static void CheckSaveFailures(string folder, Action<string, bool, string> check)
    {
        var path = Seed(folder, "readonly.json", "{\"zoomSteps\":1}");
        var before = File.ReadAllBytes(path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        var window = OpenWindow(new TextViewOptionsStore(path));
        try
        {
            var pane = window.SessionPanes[0];
            Click(MenuOption(window, "view-whitespace"));
            check("view settings read-only file reports the failure and keeps the shown change", window.TextViewOptionsStorage.LastSaveResult == false
                && pane.DiffEditor(0).Options.ShowSpaces && Near(pane.DiffEditor(0).FontSize, 13 + 4.0 / 3)
                && pane.FolderCopyStatusText.Contains("表示メニューの設定を保存できません", StringComparison.Ordinal)
                && before.SequenceEqual(File.ReadAllBytes(path)), pane.FolderCopyStatusText);
        }
        finally
        {
            Close(window);
            // 自作 fixture の読取り専用は、片付けの前に原状へ戻す。
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        var guarded = new TextViewOptionsStore(Path.Combine(folder, "guarded.json"));
        guarded.AddOutputGuard(_ => throw new InvalidOperationException("protected"));
        check("view settings store refuses a protected output without writing", !guarded.Save(new TextViewSettings { ViewWhitespace = true })
            && guarded.Diagnostic is not null && !File.Exists(Path.Combine(folder, "guarded.json")) && !guarded.Current.ViewWhitespace, guarded.Diagnostic ?? "");
        var memory = new TextViewOptionsStore();
        check("view settings store rejects an out of range zoom before saving", !memory.Save(new TextViewSettings { ZoomSteps = 69 }) && memory.Current.ZoomSteps == 0,
            memory.Diagnostic ?? "");
    }

    private static MainWindow OpenWindow(TextViewOptionsStore store)
    {
        var window = new MainWindow(null, new ImageApplicationOptionsStore(), new FolderApplicationOptionsStore(), new TableApplicationOptionsStore(),
            new FolderColumnOptionsStore(), store) { Width = 1280, Height = 850 };
        window.Show(); Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Close(MainWindow window)
    {
        foreach (var session in window.SessionPanes) session.DiscardChanges();
        window.Close(); Dispatcher.UIThread.RunJobs();
    }

    private static MenuItem MenuOption(MainWindow window, string name)
    {
        var view = window.FrameMenu!.Items.OfType<MenuItem>().First(item => item.Header as string == "表示(_V)");
        return view.Items.OfType<MenuItem>().First(item => item.Name == "frame-menu-" + name);
    }

    private static string Seed(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }
}

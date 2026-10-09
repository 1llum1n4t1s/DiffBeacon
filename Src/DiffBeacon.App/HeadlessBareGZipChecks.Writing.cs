using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static partial class HeadlessBareGZipChecks
{
    private static void CheckWriting(MainWindow window, ComparisonPane rootPane, ComparisonPane displayedPane,
        string folder, Action<Task> pump, Action<string, bool, string> check, Action<string> screenshot)
    {
        var work = Path.Combine(folder, "writing"); Directory.CreateDirectory(work);
        var displayed = Panel(displayedPane); var rows = displayed.Rows; var preview = displayed.PreviewText;
        var left = displayed.ConfirmedLeft; var right = displayed.ConfirmedRight;
        var active = window.ActivePane; var tabs = window.SessionPanes.ToArray();
        var paths = new[] { displayedPane.LeftPath.Text, displayedPane.RightPath.Text };
        var captured = WorkspaceStore.SerializeWorkspace(new() { Entries = new[] { displayedPane.CaptureProject() } });
        var initialModals = window.OwnedWindows.Count(item => item.IsVisible);
        var modals = new List<WriterModalProof>(); var outputs = new List<WriterOutputProof>();
        var originalHook = GZipWriteOptionsDialog.Shown;
        var cancelPath = Path.Combine(work, "cancel-protected.gz"); File.WriteAllText(cancelPath, "cancel must retain existing output");
        var cancelSha = Hash(File.ReadAllBytes(cancelPath));
        screenshot("gzip-write-parent-before.png");
        try
        {
            var canceled = Dialog("cancel", false, 28591);
            check("gzip writer modal cancel returns null", canceled is null && Hash(File.ReadAllBytes(cancelPath)) == cancelSha,
                "actual pointer Cancel; no writer action invoked after null result");
            var utf8 = Dialog("utf8", true, 65001);
            var standard = Dialog("default", true, 28591);
            check("gzip writer modal default and UTF8 options", utf8?.GZipNameCodePage == 65001 && standard?.GZipNameCodePage == 28591,
                "fresh dialog resets to28591; picker keyboard selection and actual pointer Accept");
            foreach (var (tag, options) in new[] { ("default", standard!), ("utf8", utf8!) })
            foreach (var empty in new[] { false, true })
            {
                var name = tag == "utf8" ? (empty ? "日本-empty.bin" : "日本-payload.bin") : (empty ? "empty.bin" : "payload.bin");
                var payload = empty ? Array.Empty<byte>() : Enumerable.Range(0, 8193).Select(i => (byte)((i * 31 + i / 256) % 256)).ToArray();
                var input = Path.Combine(work, name); File.WriteAllBytes(input, payload); var sourceSha = Hash(payload);
                var destination = Path.Combine(work, tag + "-" + empty + (empty ? ".gzip" : ".gz"));
                File.WriteAllText(destination, "old output must be replaced completely");
                pump(ArchiveActions.CreateAsync(input, destination, CancellationToken.None, options));
                check("gzip writer GUI action retains input " + tag + " " + empty, Hash(File.ReadAllBytes(input)) == sourceSha,
                    "real single-file CreateAsync; native input/save picker unverified");
                outputs.Add(new(tag + "-" + empty, input, destination, name, empty, options.GZipNameCodePage, sourceSha));
            }
            var rootPanel = Panel(rootPane); var rootRows = rootPanel.Rows; var rootPreview = rootPanel.PreviewText;
            var rootLeft = rootPanel.ConfirmedLeft; var rootRight = rootPanel.ConfirmedRight;
            var rootPath = rootLeft.RootPath; var rootSha = Hash(File.ReadAllBytes(rootPath));
            var repacked = Path.Combine(work, "panel-root-utf8.gz");
            pump(rootPanel.RepackToAsync(false, repacked, CancellationToken.None, utf8));
            check("gzip writer root panel retains confirmed input and display", Hash(File.ReadAllBytes(rootPath)) == rootSha
                && ReferenceEquals(rootPanel.Rows, rootRows) && rootPanel.PreviewText == rootPreview
                && ReferenceEquals(rootPanel.ConfirmedLeft, rootLeft) && ReferenceEquals(rootPanel.ConfirmedRight, rootRight),
                "root RepackToAsync read932/write65001; nested repack remains unsupported");
            check("gzip writer modal parent display preserved", Preserved(), "confirmed rows/preview/project/paths/active tab/session list unchanged");
            screenshot("gzip-write-parent-after.png");
            using (var file = File.Create(Path.Combine(work, "ui-proof.json")))
            using (var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("scope", "actual owned modal pointer buttons + picker keyboard + real CreateAsync/root RepackToAsync; native pickers unverified");
                json.WriteStartArray("modals");
                foreach (var row in modals)
                {
                    json.WriteStartObject(); json.WriteString("id", row.Id);
                    json.WriteBoolean("ownerMatches", row.OwnerMatches); json.WriteBoolean("initiallyDefault", row.InitiallyDefault);
                    json.WriteNumber("selected", row.Selected); json.WriteBoolean("closed", row.Closed);
                    json.WriteBoolean("modalEnded", row.ModalEnded); json.WriteBoolean("parentPreserved", row.ParentPreserved);
                    json.WriteBoolean("accepted", row.Accepted); json.WriteString("png", row.Png); json.WriteEndObject();
                }
                json.WriteEndArray(); json.WriteStartArray("outputs");
                foreach (var row in outputs)
                {
                    json.WriteStartObject(); json.WriteString("id", row.Id); json.WriteString("input", row.Input);
                    json.WriteString("output", row.Output); json.WriteString("name", row.Name); json.WriteBoolean("empty", row.Empty);
                    json.WriteNumber("codePage", row.CodePage); json.WriteString("sourceSha", row.SourceSha); json.WriteEndObject();
                }
                json.WriteEndArray(); json.WriteStartObject("rootRepack");
                json.WriteString("input", rootPath); json.WriteString("output", repacked); json.WriteString("name", "階層.gz");
                json.WriteNumber("codePage", 65001); json.WriteString("rootSha", rootSha); json.WriteEndObject();
                json.WriteBoolean("parentPreserved", Preserved()); json.WriteNumber("modalCountBefore", initialModals);
                json.WriteNumber("modalCountAfter", window.OwnedWindows.Count(item => item.IsVisible));
                json.WriteStartArray("unqualified");
                foreach (var item in new[] { "native file/save pickers", "CreateArchiveAsync full picker route", "desktop OS pointer", "deterministic cancellation/limits" })
                    json.WriteStringValue(item);
                json.WriteEndArray(); json.WriteEndObject();
            }
        }
        finally { GZipWriteOptionsDialog.Shown = originalHook; }

        ManagedArchiveWriteOptions? Dialog(string id, bool accept, int codePage)
        {
            ManagedArchiveWriteOptions? result = null; GZipWriteOptionsDialog? shown = null;
            var ownerMatches = false; var initiallyDefault = false; var selected = 0; var closed = false;
            GZipWriteOptionsDialog.Shown = dialog =>
            {
                shown = dialog; dialog.Closed += (_, _) => closed = true; Jobs();
                ownerMatches = ReferenceEquals(dialog.Owner, window) && window.OwnedWindows.Contains(dialog) && dialog.IsVisible;
                initiallyDefault = ArchiveNameSettings.Selected(dialog.NameCodePage) == 28591;
                if (codePage == 65001)
                {
                    Hit(dialog, dialog.NameCodePage); Stroke(dialog, Key.Home, PhysicalKey.Home);
                    Stroke(dialog, Key.Down, PhysicalKey.ArrowDown); Stroke(dialog, Key.Enter, PhysicalKey.Enter);
                }
                selected = ArchiveNameSettings.Selected(dialog.NameCodePage); Jobs();
                using var frame = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("gzip dialog frame missing");
                frame.Save(Path.Combine(work, "modal-" + id + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                Hit(dialog, accept ? dialog.Accept : dialog.Cancel);
            };
            async Task Show() => result = await GZipWriteOptionsDialog.ShowAsync(window);
            try { pump(Show()); Jobs(); }
            finally { GZipWriteOptionsDialog.Shown = originalHook; }
            var modalEnded = shown is not null && closed && !shown.IsVisible && window.OwnedWindows.Count(item => item.IsVisible) == initialModals;
            var preserved = Preserved();
            check("gzip writer actual owned modal " + id, ownerMatches && initiallyDefault && selected == codePage && modalEnded && preserved,
                "owned ShowDialog; actual pointer and keyboard; Closed observed and parent retained");
            modals.Add(new(id, ownerMatches, initiallyDefault, selected, closed, modalEnded, preserved, result is not null, "modal-" + id + ".png"));
            return result;
        }
        bool Preserved() => window.IsVisible && ReferenceEquals(window.ActivePane, active) && window.SessionPanes.SequenceEqual(tabs)
            && ReferenceEquals(Panel(displayedPane), displayed) && ReferenceEquals(displayed.Rows, rows) && displayed.PreviewText == preview
            && ReferenceEquals(displayed.ConfirmedLeft, left) && ReferenceEquals(displayed.ConfirmedRight, right)
            && displayedPane.LeftPath.Text == paths[0] && displayedPane.RightPath.Text == paths[1]
            && WorkspaceStore.SerializeWorkspace(new() { Entries = new[] { displayedPane.CaptureProject() } }).SequenceEqual(captured);
        static void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        static void Hit(Window owner, Control control)
        {
            Jobs(); owner.UpdateLayout();
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), owner)
                ?? throw new InvalidOperationException("gzip writer control missing layout");
            owner.MouseDown(point, MouseButton.Left); owner.MouseUp(point, MouseButton.Left); Jobs();
        }
        static void Stroke(Window owner, Key key, PhysicalKey physical)
        { owner.KeyPress(key, RawInputModifiers.None, physical, null); owner.KeyRelease(key, RawInputModifiers.None, physical, null); Jobs(); }
    }
    private sealed record WriterModalProof(string Id, bool OwnerMatches, bool InitiallyDefault, int Selected,
        bool Closed, bool ModalEnded, bool ParentPreserved, bool Accepted, string Png);
    private sealed record WriterOutputProof(string Id, string Input, string Output, string Name, bool Empty, int CodePage, string SourceSha);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Core;

namespace DiffBeacon.App;

// 配置草案。実Buttonから観測した元TaskだけをPumpする。直接Submit/read呼出しは禁止。
internal static class HeadlessIndependentTextInputSelectionChecks
{
    internal static void Run(MainWindow unusedWindow, string output, Action<Task> pump,
        Action<string, bool, string> check, Action<string> unusedScreenshot)
    {
        var folder = Path.Combine(output, "independent-text-input-selection");
        if (Directory.Exists(folder)) throw new IOException("クリーンなrun内出力が必要です。");
        Directory.CreateDirectory(folder);
        var fixture = Path.GetFullPath("tests/Fixtures/IndependentTextInputSelection");
        foreach (var source in Directory.EnumerateFiles(Path.Combine(fixture, "inputs")))
            CopyFixedRunInput(source, Path.Combine(folder, Path.GetFileName(source)));
        CopyFixedRunInput(Path.Combine(fixture, "manifest.json"), Path.Combine(folder, "manifest.json"));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "manifest.json")));
        var cases = manifest.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone()).ToArray();
        var receipts = new List<Action<Utf8JsonWriter>>();
        var layouts = new List<Action<Utf8JsonWriter>>();
        void Verify(string id, bool condition, string detail = "") => check("Independent Text Input Selection " + id, condition, detail);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        string Resolve(string relative) => relative == "" ? "" : Path.Combine(folder, Path.GetFileName(relative));
        var physical = cases.Single(c => c.GetProperty("id").GetString() == "physical-0");

        ComparisonProject ParentProject() => new()
        {
            Mode = "Text", LeftPath = Resolve("inputs/left.txt"), BasePath = Resolve("inputs/middle.txt"), RightPath = Resolve("inputs/right.txt"),
            TextInputs = new() { Semantics = "Independent", Left = new() { Kind = "Physical" }, Middle = new() { Kind = "Physical" }, Right = new() { Kind = "Physical" } },
            TextComparisonPair = "LeftMiddle"
        };

        void Execute(JsonElement spec, string mode, bool geometry = false)
        {
            var id = spec.GetProperty("id").GetString()! + ":" + mode;
            var host = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(folder, "options-" + id.Replace(':', '-') + ".json"))) { Width = 1000, Height = 680 };
            host.Show(); Jobs();
            var parent = host.ActivePane;
            parent.ApplyProject(ParentProject()); pump(parent.ComparePathsAsync()); Jobs();
            parent.TextEditor(0).Text += "\nparent-dirty";
            parent.DiffList.SelectedIndex = 0;
            Jobs();
            var existingOutput = Path.Combine(folder, "existing-" + id.Replace(':', '-') + ".out");
            File.WriteAllBytes(existingOutput, "existing-output\r\n"u8.ToArray());
            SetFixedRunInputTime(existingOutput);
            var before = parent.CaptureInputSelectionTestEvidence();
            var beforeIdentity = parent.CaptureIndependentTextState();
            var beforeStore = host.ArchiveTexts.Generation;
            var storeReference = host.ArchiveTexts;
            var initialTabs = host.SessionPanes.Count();
            var initialActive = host.SessionPanes.ToList().IndexOf(parent);
            IndependentTextInputDialog? dialog = null;
            Task<bool>? opening = null;
            Task? submission = null;
            var candidates = 0;
            var clicks = new List<string>();
            var pending = new Dictionary<string, Task>();
            var pickerCalls = new List<(int Side, bool Archive, string Path)>();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gateTokenCancelled = false;
            Action<Utf8JsonWriter>? candidateBeforeGate = null;
            InputTamperCapture? tamper = null;
            var showed = false;
            var observedFailure = false;
            bool? accepted = null;
            var gate = mode is not ("accept" or "cancel-before" or "close-before" or "manifest-cancel" or "manifest-root-change" or "manifest-retry" or "tab-limit");
            host.IndependentTextInputDialogShown = shown =>
            {
                dialog = shown; showed = true;
                shown.SubmissionTaskObserved = task => submission = task;
                for (var side = 0; side < 3; side++)
                {
                    var browser = shown.Side(side);
                    // loop変数を共有しない。
                    var capturedSide = side;
                    browser.OperationTaskObserved = (name, task) => pending[$"{capturedSide}:{name}"] = task;
                    browser.PickerPathProvider = (which, archive, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        var path = Resolve(spec.GetProperty("sides")[which].GetProperty("source").GetString()!);
                        pickerCalls.Add((which, archive, path));
                        return Task.FromResult<string?>(path);
                    };
                }
            };
            host.IndependentTextInputOperationObserved = task => opening = task;
            host.IndependentTextInputCandidateCreated = _ => candidates++;
            if (gate) host.IndependentTextInputAdoptionGate = async (candidate, token) =>
            {
                candidateBeforeGate = candidate.CaptureInputSelectionTestEvidence();
                ready.TrySetResult();
                // 故意にtokenに応じず遅れて完了する外部仕事を模擬する。
                await release.Task;
                gateTokenCancelled = token.IsCancellationRequested;
            };
            try
            {
                if (mode == "tab-limit") while (host.SessionPanes.Count() < 255) host.AddSession();
                initialTabs = host.SessionPanes.Count();
                host.SelectSession(initialActive); Jobs();
                Click(parent.IndependentTextInputsButton); clicks.Add("entry");
                if (dialog is null || opening is null) throw new InvalidOperationException("実入口のmodal/task観測がありません。");
                Verify(id + " dialog shown by entry", showed && dialog.IsVisible);

                void BrowserClick(int side, string operation, Button button)
                {
                    pending.Remove($"{side}:{operation}");
                    Click(button); clicks.Add($"{side}:{operation}");
                    if (!pending.Remove($"{side}:{operation}", out var task)) throw new InvalidOperationException("実Buttonの元Taskを観測できません。");
                    pump(task); Jobs();
                }
                void SelectRow(int side, string path)
                {
                    var browser = dialog.Side(side);
                    // itemはbrowserが実manifestから作った非公開row。経路名とsuffixだけで実ListBox rowを選ぶ。
                    var rows = browser.Entries.ItemsSource?.Cast<object>().ToArray() ?? [];
                    var matching = rows.Where(item => item.ToString()!.StartsWith(path + " （", StringComparison.Ordinal)).ToArray();
                    if (matching.Length != 1)
                        throw new InvalidOperationException($"case={id}; side={side}; expected={path}; rows={string.Join(" | ", rows.Select(item => item.ToString()))}; summary={browser.Summary}; openEnabled={browser.OpenContainer.IsEnabled}; backEnabled={browser.Back.IsEnabled}");
                    var row = matching[0];
                    browser.Entries.SelectedItem = row; Jobs(); clicks.Add($"{side}:row:{path}");
                }

                for (var side = 0; side < 3; side++)
                {
                    dialog.SideTabs.SelectedIndex = side; Jobs();
                    var item = spec.GetProperty("sides")[side];
                    var browser = dialog.Side(side);
                    var kind = item.GetProperty("kind").GetString();
                    browser.Kind.SelectedIndex = kind == "Physical" ? 0 : kind == "Untitled" ? 1 : 2;
                    browser.ReadOnly.IsChecked = false;
                    if (kind != "Untitled") BrowserClick(side, "pick", browser.Pick);
                    if (kind != "Archive") continue;
                    if (side == 0 && mode.StartsWith("manifest-", StringComparison.Ordinal))
                    {
                        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        browser.ManifestReadGate = async (_, _, token) => { entered.TrySetResult(); await resume.Task; gateTokenCancelled = token.IsCancellationRequested; };
                        pending.Remove($"{side}:load"); Click(browser.Load); clicks.Add($"{side}:load");
                        if (!pending.Remove($"{side}:load", out var readTask)) throw new InvalidOperationException("Load task未観測です。");
                        pump(entered.Task);
                        if (mode == "manifest-root-change") browser.RootPath.Text = Resolve("inputs/right.zip");
                        else Click(dialog.Abort);
                        clicks.Add(mode == "manifest-root-change" ? "root-change" : "abort");
                        resume.TrySetResult(); pump(readTask); Jobs();
                        browser.ManifestReadGate = null;
                        if (mode != "manifest-retry")
                        {
                            Click(dialog.Compare); clicks.Add("compare-unverified");
                            if (submission is null) throw new InvalidOperationException("Compare task未観測です。");
                            pump(submission); Jobs();
                            Verify(id + " stale manifest cannot submit", candidates == 0 && host.SessionPanes.Count() == initialTabs);
                            Click(dialog.Cancel); clicks.Add("cancel"); pump(opening);
                            accepted = opening.Result; observedFailure = true;
                            break;
                        }
                        // Abort後の同routeを実Loadで再検証する。
                    }
                    BrowserClick(side, "load", browser.Load);
                    var chain = item.GetProperty("chain").EnumerateArray().Select(v => v.GetString()!).ToArray();
                    foreach (var container in chain) { SelectRow(side, container); BrowserClick(side, "open", browser.OpenContainer); }
                    if (chain.Length > 0)
                    {
                        BrowserClick(side, "back", browser.Back);
                        SelectRow(side, chain[^1]); BrowserClick(side, "open", browser.OpenContainer);
                    }
                    SelectRow(side, item.GetProperty("leaf").GetString()!);
                    browser.AllowWorkingEdit.IsChecked = true;
                }

                if (!observedFailure)
                {
                    dialog.Pair.SelectedIndex = spec.GetProperty("pairIndex").GetInt32(); Jobs();
                    if (geometry) DialogGeometry(dialog, id);
                    if (mode == "cancel-before") { Click(dialog.Cancel); clicks.Add("cancel"); }
                    else if (mode == "close-before") { dialog.Close(); clicks.Add("close"); }
                    else
                    {
                        if (mode == "tab-limit") { host.AttachInputSelectionTestBoundaryTab(); initialTabs++; clicks.Add("fixture-reaches-256"); }
                        Click(dialog.Compare); clicks.Add("compare");
                        if (submission is null) throw new InvalidOperationException("実Compareの元Taskを観測できません。");
                        var firstSubmission = submission;
                        if (gate)
                        {
                            pump(ready.Task);
                            switch (mode)
                            {
                                case "double-submit": Click(dialog.Compare); clicks.Add("compare-second"); break;
                                case "cancel-gate": Click(dialog.Cancel); clicks.Add("cancel"); break;
                                case "close-gate": dialog.Close(); clicks.Add("close"); break;
                                case "abort-gate": Click(dialog.Abort); clicks.Add("abort"); break;
                                case "kind-change": dialog.Side(0).Kind.SelectedIndex = 1; clicks.Add("kind-change"); break;
                                case "root-change": dialog.Side(0).RootPath.Text = Resolve("inputs/right.txt"); clicks.Add("root-change"); break;
                                case "readonly-change": dialog.Side(0).ReadOnly.IsChecked = true; clicks.Add("readonly-change"); break;
                                case "pair-change": dialog.Pair.SelectedIndex = (dialog.Pair.SelectedIndex + 1) % 3; clicks.Add("pair-change"); break;
                                case "tab-away-return":
                                    var other = host.AddSession(); host.SelectSession(host.SessionPanes.ToList().IndexOf(other)); host.SelectSession(initialActive); clicks.Add("tab-away-return");
                                    // one deliberately opened user tab is preserved; it is not an adopted candidate.
                                    initialTabs++; break;
                                case "physical-sha-change":
                                    tamper = ReplacePreservingSizeAndTime(Resolve("inputs/left.txt"), Resolve("inputs/left-alternate.txt"), Path.Combine(folder, "tamper", id.Replace(':', '-'))); clicks.Add("physical-sha-change"); break;
                                case "archive-sha-change":
                                    tamper = ReplacePreservingSizeAndTime(Resolve("inputs/left.zip"), Resolve("inputs/left-alternate.zip"), Path.Combine(folder, "tamper", id.Replace(':', '-'))); clicks.Add("archive-sha-change"); break;
                                default: throw new InvalidOperationException("未実装gate modeです。");
                            }
                            release.TrySetResult();
                        }
                        pump(firstSubmission); Jobs();
                        // false/rejected compare leaves dialog open. Completion of its real opener is observed after actual Cancel.
                        if (dialog.IsVisible) { Click(dialog.Cancel); clicks.Add("cancel-after-refusal"); }
                    }
                    pump(opening); accepted = opening.Result; Jobs();
                }
                var success = mode is "accept" or "double-submit" or "manifest-retry";
                var finalTabs = host.SessionPanes.Count();
                var actual = success ? host.ActivePane : null;
                Verify(id + " modal result", accepted == success);
                Verify(id + " adopted tab count", finalTabs == initialTabs + (success ? 1 : 0));
                Verify(id + " candidate count", candidates == (mode.StartsWith("manifest-", StringComparison.Ordinal) && mode != "manifest-retry" || mode is "cancel-before" or "close-before" or "tab-limit" ? 0 : 1));
                Verify(id + " parent payload identity retained", Equals(beforeIdentity, parent.CaptureIndependentTextState()));
                Verify(id + " store reference and generation retained", ReferenceEquals(storeReference, host.ArchiveTexts) && beforeStore == host.ArchiveTexts.Generation);
                Verify(id + " protected existing output", File.ReadAllBytes(existingOutput).AsSpan().SequenceEqual("existing-output\r\n"u8));
                Verify(id + " selected tab", success ? !ReferenceEquals(actual, parent) : ReferenceEquals(host.ActivePane, parent));
                if (actual is not null)
                {
                    for (var side = 0; side < 3; side++) Verify(id + " body " + side,
                        actual.TextEditor(side).Text == spec.GetProperty("sides")[side].GetProperty("text").GetString());
                    if (geometry) PaneGeometry(host, actual, id);
                }
                var after = parent.CaptureInputSelectionTestEvidence();
                var finalActive = host.SessionPanes.ToList().IndexOf(host.ActivePane);
                var finalStore = host.ArchiveTexts.Generation;
                var payload = actual?.CaptureInputSelectionTestEvidence();
                var project = actual?.CaptureProject();
                var oldOutput = File.ReadAllBytes(existingOutput);
                var parentSameIdentity = Equals(beforeIdentity, parent.CaptureIndependentTextState());
                receipts.Add(w =>
                {
                    w.WriteStartObject(); w.WriteString("id", id); w.WriteString("fixtureCase", spec.GetProperty("id").GetString()); w.WriteString("mode", mode);
                    w.WriteBoolean("accepted", accepted == true); w.WriteNumber("candidateCount", candidates); w.WriteNumber("tabsBefore", initialTabs); w.WriteNumber("tabsAfter", finalTabs);
                    w.WriteNumber("activeBefore", initialActive); w.WriteNumber("activeAfter", finalActive); w.WriteNumber("storeBefore", beforeStore); w.WriteNumber("storeAfter", finalStore);
                    w.WriteBoolean("sameStoreReference", ReferenceEquals(storeReference, host.ArchiveTexts)); w.WriteBoolean("sameParentIdentity", parentSameIdentity);
                    w.WriteBoolean("gateTokenCancelled", gateTokenCancelled); w.WriteBase64String("existingOutput", oldOutput);
                    if (candidateBeforeGate is not null) { w.WritePropertyName("candidatePayloadBeforeGate"); candidateBeforeGate(w); }
                    if (tamper is { } changed)
                    {
                        w.WriteStartObject("tamper"); w.WriteNumber("sizeBefore", changed.SizeBefore); w.WriteNumber("sizeAfter", changed.SizeAfter);
                        w.WriteNumber("mtimeBeforeTicks", changed.TimeBeforeTicks); w.WriteNumber("mtimeAfterTicks", changed.TimeAfterTicks);
                        w.WriteString("beforePath", changed.BeforePath); w.WriteString("afterPath", changed.AfterPath);
                        w.WriteString("shaBefore", changed.BeforeSha); w.WriteString("shaAfter", changed.AfterSha);
                        w.WriteString("provenance", changed.Provenance); w.WriteString("fixedUtc", "2024-01-01T00:00:00.1234567Z");
                        w.WriteString("timing", "after-candidate-read-before-final-rehash"); w.WriteEndObject();
                    }
                    w.WritePropertyName("before"); before(w); w.WritePropertyName("after"); after(w);
                    w.WriteStartArray("clicks"); foreach (var click in clicks) w.WriteStringValue(click); w.WriteEndArray();
                    w.WriteStartArray("pickerCalls"); foreach (var call in pickerCalls) { w.WriteStartObject(); w.WriteNumber("side", call.Side); w.WriteBoolean("archive", call.Archive); w.WriteString("path", call.Path); w.WriteEndObject(); } w.WriteEndArray();
                    if (project is not null && payload is not null)
                    {
                        w.WriteString("pair", project.TextComparisonPair); w.WriteString("semantics", project.TextInputs!.Semantics);
                        w.WritePropertyName("payload"); payload(w); w.WriteStartArray("routes");
                        for (var side = 0; side < 3; side++)
                        {
                            var input = ProjectInputs.Archive(project, side);
                            w.WriteStartObject(); w.WriteString("kind", project.TextInputs.Side(side).Kind);
                            w.WriteString("path", side == 0 ? project.LeftPath : side == 1 ? project.BasePath : project.RightPath);
                            if (input is not null) { w.WriteString("root", input.RootPath); w.WriteString("rootSha256", input.RootSha256); w.WriteString("leaf", input.LeafEntry); w.WriteStartArray("chain"); foreach (var entry in input.EntryChain) w.WriteStringValue(entry); w.WriteEndArray(); if (input.InheritedReadOnly is bool ro) w.WriteBoolean("inheritedReadOnly", ro); else w.WriteNull("inheritedReadOnly"); }
                            w.WriteBoolean("readOnly", side == 0 ? project.LeftReadOnly : side == 1 ? project.BaseReadOnly : project.RightReadOnly); w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                });
            }
            finally
            {
                release.TrySetResult();
                if (dialog?.IsVisible == true) { Click(dialog.Cancel); if (opening is not null) pump(opening); }
                host.IndependentTextInputDialogShown = null; host.IndependentTextInputOperationObserved = null;
                host.IndependentTextInputCandidateCreated = null; host.IndependentTextInputAdoptionGate = null;
                foreach (var pane in host.SessionPanes) pane.DiscardChanges();
                host.Close(); Jobs();
                // 次caseへ原本差替えを持ち越さない。元fixtureをrun内fileへ戻す。
                foreach (var source in Directory.EnumerateFiles(Path.Combine(fixture, "inputs")))
                    CopyFixedRunInput(source, Path.Combine(folder, Path.GetFileName(source)), overwrite: true);
            }
        }

        foreach (var spec in cases) Execute(spec, "accept", spec.GetProperty("id").GetString() == "archives-0");
        foreach (var mode in new[] { "double-submit", "cancel-before", "close-before", "cancel-gate", "close-gate", "abort-gate", "kind-change", "root-change", "readonly-change", "pair-change", "tab-away-return", "physical-sha-change", "tab-limit" }) Execute(physical, mode);
        var archiveCase = cases.Single(c => c.GetProperty("id").GetString() == "archives-0");
        foreach (var mode in new[] { "manifest-cancel", "manifest-root-change", "manifest-retry", "archive-sha-change" }) Execute(archiveCase, mode);
        using (var file = File.Create(Path.Combine(folder, "observations.json")))
        using (var writer = new Utf8JsonWriter(file, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("schema", "input-selection-observations-v1"); writer.WriteString("status", "completed-minimum-set");
            writer.WriteStartArray("cases"); foreach (var receipt in receipts) receipt(writer); writer.WriteEndArray();
            writer.WriteStartArray("layouts"); foreach (var layout in layouts) layout(writer); writer.WriteEndArray(); writer.WriteEndObject();
        }

        void DialogGeometry(IndependentTextInputDialog dialog, string id)
        {
            foreach (var size in new[] { (1000, 680), (850, 550) })
            {
                dialog.Width = size.Item1; dialog.Height = size.Item2;
                for (var side = 0; side < 3; side++)
                {
                    dialog.SideTabs.SelectedIndex = side; Jobs(); var browser = dialog.Side(side);
                    Capture(dialog, id + "-dialog-" + size.Item1 + "-" + side + "-list", [("list", browser.Entries), ("pair", dialog.Pair), ("compare", dialog.Compare), ("abort", dialog.Abort), ("cancel", dialog.Cancel)]);
                    foreach (var item in new[] { ("kind", (Control)browser.Kind), ("root", browser.RootPath), ("pick", browser.Pick), ("load", browser.Load), ("open", browser.OpenContainer), ("back", browser.Back), ("edit", browser.AllowWorkingEdit) })
                    {
                        item.Item2.BringIntoView(); Jobs(); Capture(dialog, id + "-dialog-" + size.Item1 + "-" + side + "-" + item.Item1, [item]);
                    }
                }
            }
        }
        void PaneGeometry(MainWindow host, ComparisonPane pane, string id)
        {
            for (var extra = host.SessionPanes.Count(); extra < 20; extra++) host.AddSession();
            foreach (var target in new[] { host.SessionPanes.First(), pane })
            foreach (var size in new[] { (1000, 680), (850, 550) })
            {
                host.Width = size.Item1; host.Height = size.Item2; host.SelectSession(host.SessionPanes.ToList().IndexOf(target));
                target.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1; Jobs();
                Capture(host, id + "-pane-" + host.SessionPanes.ToList().IndexOf(target) + "-" + size.Item1, Enumerable.Range(0, 3).Select(side => ("editor" + side, (Control)target.TextEditor(side))).ToArray());
                foreach (var button in target.GetVisualDescendants().OfType<Button>().Where(b => b.Content is string s && s.Contains("保存", StringComparison.Ordinal)))
                {
                    button.BringIntoView(); Jobs(); Capture(host, id + "-save-" + layouts.Count, [("save", button)]);
                }
            }
            host.SelectSession(host.SessionPanes.ToList().IndexOf(pane)); Jobs();
        }
        void Capture(Window owner, string name, (string Name, Control Control)[] controls)
        {
            Jobs(); var safe = name.Replace(':', '-'); var png = safe + ".png";
            using (var frame = owner.CaptureRenderedFrame() ?? throw new InvalidOperationException("PNGを取得できません。"))
            using (var file = File.Create(Path.Combine(folder, png))) frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            var width = owner.Bounds.Width; var height = owner.Bounds.Height;
            var bounds = controls.Select(item =>
            {
                var point = item.Control.TranslatePoint(new Point(), owner) ?? throw new InvalidOperationException("visual未接続です。");
                var rect = new Rect(point, item.Control.Bounds.Size);
                var clip = new Rect(0, 0, width, height);
                foreach (var ancestor in item.Control.GetVisualAncestors().OfType<Control>().Where(a => a.ClipToBounds))
                    if (ancestor.TranslatePoint(new Point(), owner) is { } position) clip = clip.Intersect(new Rect(position, ancestor.Bounds.Size));
                var visible = item.Control.IsVisible && item.Control.GetVisualAncestors().OfType<Control>().All(a => a.IsVisible);
                Verify(safe + " " + item.Name, visible && rect.Width > 0 && rect.Height > 0 && clip.Contains(rect) && (item.Name != "list" || rect.Height >= 100));
                return (item.Name, rect, clip, visible);
            }).ToArray();
            layouts.Add(w =>
            {
                w.WriteStartObject(); w.WriteString("name", safe); w.WriteString("png", png); w.WriteNumber("width", width); w.WriteNumber("height", height); w.WriteStartArray("controls");
                foreach (var item in bounds) { w.WriteStartObject(); w.WriteString("name", item.Name); w.WriteBoolean("visible", item.visible); WriteRect(w, "bounds", item.rect); WriteRect(w, "clip", item.clip); w.WriteEndObject(); }
                w.WriteEndArray(); w.WriteEndObject();
            });
        }
    }
    private static DateTime FixedRunInputUtc => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
    private static void SetFixedRunInputTime(string path)
    {
        // 新規run copyは初回読込みより前に100ns表現可能な時刻へ固定する。
        // DateTimeの一致だけではsub100nsを証明できないため、独立readerでstat.st_mtime_nsも照合する。
        File.SetLastWriteTimeUtc(path, FixedRunInputUtc);
        if (File.GetLastWriteTimeUtc(path) != FixedRunInputUtc) throw new IOException("固定run入力時刻を設定できません。");
    }
    private static void CopyFixedRunInput(string source, string target, bool overwrite = false)
    {
        File.Copy(source, target, overwrite); SetFixedRunInputTime(target);
    }
    private sealed record InputTamperCapture(long SizeBefore, long SizeAfter, long TimeBeforeTicks, long TimeAfterTicks,
        string BeforePath, string AfterPath, string BeforeSha, string AfterSha, string Provenance);
    private static InputTamperCapture ReplacePreservingSizeAndTime(string original, string alternate, string captureDirectory)
    {
        // case別の原本backupと差替え後captureは次caseの共有入力復元で上書きしない。
        var directory = Path.GetFullPath(captureDirectory);
        if (Directory.Exists(directory)) throw new IOException("case別raw保存先は新規である必要があります。");
        Directory.CreateDirectory(directory);
        var beforePath = Path.Combine(directory, "before.raw"); var afterPath = Path.Combine(directory, "after.raw");
        if (File.GetLastWriteTimeUtc(original) != FixedRunInputUtc) throw new IOException("初回読込み前の固定run時刻が必要です。");
        File.Copy(original, beforePath);
        var beforeBytes = File.ReadAllBytes(beforePath); var length = beforeBytes.LongLength;
        var bytes = File.ReadAllBytes(alternate); if (bytes.LongLength != length) throw new InvalidOperationException("同size差替えfixtureが必要です。");
        File.WriteAllBytes(original, bytes); SetFixedRunInputTime(original);
        if (new FileInfo(original).Length != length) throw new IOException("同size差替えを確定できません。");
        File.Copy(original, afterPath);
        var afterBytes = File.ReadAllBytes(afterPath);
        if (!beforeBytes.AsSpan().SequenceEqual(File.ReadAllBytes(beforePath)) || !afterBytes.AsSpan().SequenceEqual(bytes)) throw new IOException("raw保存の全byte照合が失敗しました。");
        return new(length, afterBytes.LongLength, File.GetLastWriteTimeUtc(beforePath).Ticks, File.GetLastWriteTimeUtc(afterPath).Ticks,
            beforePath, afterPath, Convert.ToHexString(SHA256.HashData(beforeBytes)), Convert.ToHexString(SHA256.HashData(afterBytes)),
            "synthetic-input266: immutable fixture original and alternate; case-local before.raw backup and after.raw capture; shared run input restored after case");
    }
    private static void WriteRect(Utf8JsonWriter w, string key, Rect rect)
    {
        w.WriteStartObject(key); w.WriteNumber("x", rect.X); w.WriteNumber("y", rect.Y); w.WriteNumber("width", rect.Width); w.WriteNumber("height", rect.Height); w.WriteEndObject();
    }
}

public sealed partial class ComparisonPane
{
    // 検証fileだけの読取り接点。reflection/Serializerを使わずAOT経路を維持する。
    internal Action<Utf8JsonWriter> CaptureInputSelectionTestEvidence()
    {
        var texts = Enumerable.Range(0, 3).Select(side => TextEditor(side).Text ?? "").ToArray();
        var bytes = Enumerable.Range(0, 3).Select(side => TextInputDocument(side)?.CaptureBytes(texts[side]) ?? new UTF8Encoding(false).GetBytes(texts[side])).ToArray();
        var dirty = Enumerable.Range(0, 3).Select(TextDirty).ToArray();
        var readonlyFlags = Enumerable.Range(0, 3).Select(side => TextEditor(side).IsReadOnly).ToArray();
        var revisions = _textRevisions.ToArray(); var saveGeneration = _textSaveGeneration; var pair = _textPair.SelectedIndex; var selectedDiff = DiffList.SelectedIndex;
        var diffLeft = CurrentDiff?.LeftText; var diffRight = CurrentDiff?.RightText; var blocks = CurrentDiff?.Blocks.Count;
        return w =>
        {
            w.WriteStartObject(); w.WriteNumber("saveGeneration", saveGeneration); w.WriteNumber("pairIndex", pair); w.WriteNumber("selectedDiff", selectedDiff);
            w.WriteString("diffLeft", diffLeft); w.WriteString("diffRight", diffRight); if (blocks is int count) w.WriteNumber("diffBlocks", count); else w.WriteNull("diffBlocks");
            w.WriteStartArray("sides");
            for (var side = 0; side < 3; side++) { w.WriteStartObject(); w.WriteString("text", texts[side]); w.WriteBase64String("bytes", bytes[side]); w.WriteString("sha256", Convert.ToHexString(SHA256.HashData(bytes[side]))); w.WriteBoolean("dirty", dirty[side]); w.WriteBoolean("editorReadOnly", readonlyFlags[side]); w.WriteNumber("revision", revisions[side]); w.WriteEndObject(); }
            w.WriteEndArray(); w.WriteEndObject();
        };
    }
}

public sealed partial class MainWindow
{
    // 上限境界のfixture setup。選択移動の寿命取消と件数検査を別々に検証する。
    internal void AttachInputSelectionTestBoundaryTab() => AttachProjectSession(new ComparisonPane(this, ArchiveTexts));
}

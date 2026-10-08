using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

// Taskの完了順は元Taskの同期continuationで採取し、UIの参照はUIスレッドへ分ける。
internal static class HeadlessIndependentTextInputLifetimeChecks
{
    private sealed record Completion(long Sequence, long Timestamp, string Event, bool OpeningComplete,
        bool SubmissionComplete, bool BrowserComplete, int ArraysObserved, int NonNullSlots, bool GateReleased);

    internal static void Run(MainWindow unused, string output, Action<Task> pump, Action<string, bool, string> check, Action<string> unusedScreenshot)
    {
        var folder = Path.Combine(output, "independent-text-input-lifetime");
        if (Directory.Exists(folder)) throw new IOException("Lifetimeは新規run出力が必要です。");
        Directory.CreateDirectory(folder);
        var fixture = Path.GetFullPath("tests/Fixtures/IndependentTextInputLifetime");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture, "manifest.json")));
        var texts = manifest.RootElement.GetProperty("texts").EnumerateArray().Select(value => value.GetString()!).ToArray();
        var receipts = new List<Action<Utf8JsonWriter>>();
        var assertions = new List<(string Name, bool Passed, string Detail)>();
        void Jobs() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Verify(string name, bool passed, string detail = "") => assertions.Add(("Independent Text Input Lifetime " + name, passed, detail));

        void Execute(string id, string recipe)
        {
            var run = Path.Combine(folder, id); Directory.CreateDirectory(run);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(fixture, "inputs")))
            { var target = Path.Combine(run, Path.GetFileName(file)); File.Copy(file, target); File.SetAttributes(target, FileAttributes.Normal); }
            var host = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(run, "options.json"))) { Width = 1000, Height = 680 };
            host.Show(); Jobs();
            var parent = host.ActivePane;
            parent.ApplyProject(new() { Mode = "Text", LeftPath = Path.Combine(run, "left.txt"), BasePath = Path.Combine(run, "middle.txt"), RightPath = Path.Combine(run, "right.txt"),
                TextInputs = new() { Semantics = "Independent", Left = new() { Kind = "Physical" }, Middle = new() { Kind = "Physical" }, Right = new() { Kind = "Physical" } }, TextComparisonPair = "LeftMiddle" });
            pump(parent.ComparePathsAsync()); Jobs();
            for (var side = 0; side < 3; side++) parent.TextEditor(side).Text += "parent-dirty\n";
            var before = parent.CaptureInputSelectionTestEvidence(); var identity = parent.CaptureIndependentTextState();
            var store = host.ArchiveTexts; var generation = store.Generation; var tabsBefore = host.SessionPanes.Count();
            File.WriteAllBytes(Path.Combine(run, "existing.out"), "lifetime-existing-output\r\n"u8.ToArray());
            IndependentTextInputDialog? dialog = null; Task<bool>? opening = null; Task? submission = null; Task? pendingBrowser = null;
            var completedObservers = new List<Task>(); var browserOperations = new Dictionary<string, Task>();
            var arrays = new ConcurrentQueue<Array>(); var events = new ConcurrentQueue<Completion>();
            var fields = new List<TextBox>(); var clicks = new List<string>();
            long sequence = 0; var released = 0; var candidates = 0;
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gated = !id.StartsWith("success-", StringComparison.Ordinal);
            void ObserveArray(Array array)
            {
                arrays.Enqueue(array);
                if (array is string?[][] nested) foreach (var chain in nested) if (chain is not null) arrays.Enqueue(chain);
            }
            int NonNullSlots() => arrays.ToArray().Sum(array => array.Cast<object?>().Count(value => value is not null));
            void Capture(string name) => events.Enqueue(new(Interlocked.Increment(ref sequence), Stopwatch.GetTimestamp(), name,
                opening?.IsCompleted == true, submission?.IsCompleted == true, pendingBrowser?.IsCompleted == true,
                arrays.Count, NonNullSlots(), Volatile.Read(ref released) != 0));
            Task Observe(Task task, string name) => task.ContinueWith(_ => Capture(name), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            host.IndependentTextInputOperationObserved = task =>
            { opening = task; completedObservers.Add(Observe(task, "opening-complete")); };
            host.IndependentTextInputCandidateCreated = _ => candidates++;
            host.IndependentTextInputDialogShown = shown =>
            {
                dialog = shown;
                shown.SubmissionStateObserved = (selection, locals) => { ObserveArray(locals); ObserveArray(selection.Passwords); Capture("submission-state-observed"); };
                shown.SubmissionTaskObserved = task => { submission = task; completedObservers.Add(Observe(task, "submission-complete")); };
                for (var side = 0; side < 3; side++)
                {
                    var captured = side; var browser = shown.Side(side);
                    browser.PendingReadStateObserved = values => ObserveArray(values);
                    browser.OperationTaskObserved = (name, task) =>
                    {
                        browserOperations[captured + ":" + name] = task;
                        if ((id == "load-close" && name == "load") || (id == "picker-close" && name == "pick"))
                        { pendingBrowser = task; completedObservers.Add(Observe(task, "browser-complete")); }
                    };
                    browser.PickerPathProvider = async (_, archive, token) =>
                    {
                        if (id == "picker-close" && captured == 0)
                        {
                            reached.TrySetResult(); await release.Task; // OS picker同様、取消tokenを受けない返却窓。
                        }
                        return Path.Combine(run, new[] { "left", "middle", "right" }[captured] + (archive ? ".zip" : ".txt"));
                    };
                }
            };
            if (id == "compare-close") host.IndependentTextInputAdoptionGate = async (_, _) => { reached.TrySetResult(); await release.Task; };
            var nonEmptyFields = 0; var arraysBeforeClose = 0; bool accepted = false; bool fieldsCleared = false;
            Action<Utf8JsonWriter>? adopted = null;
            try
            {
                Click(parent.IndependentTextInputsButton); clicks.Add("entry");
                if (opening is null || dialog is null) throw new InvalidOperationException("実入口Task/dialogの観測なし");
                void BrowserClick(int side, string operation, Button button, bool wait = true)
                {
                    browserOperations.Remove(side + ":" + operation); Click(button); clicks.Add(side + ":" + operation);
                    if (!browserOperations.TryGetValue(side + ":" + operation, out var task)) throw new InvalidOperationException("実browserTask未観測");
                    if (wait) { pump(task); Jobs(); }
                }
                if (id is "load-close" or "picker-close")
                {
                    var browser = dialog.Side(0); browser.Kind.SelectedIndex = 2;
                    BrowserClick(0, "pick", browser.Pick, id != "picker-close");
                    if (id == "load-close")
                    {
                        foreach (var field in browser.PasswordFields) field.Text = "synthetic-lifetime-marker";
                        fields.AddRange(browser.PasswordFields);
                        browser.ManifestReadGate = async (_, _, _) => { reached.TrySetResult(); await release.Task; };
                        BrowserClick(0, "load", browser.Load, false);
                    }
                }
                else
                {
                    for (var side = 0; side < 3; side++)
                    {
                        dialog.SideTabs.SelectedIndex = side; Jobs(); var browser = dialog.Side(side); var kind = recipe[side];
                        browser.Kind.SelectedIndex = kind == 'P' ? 0 : kind == 'U' ? 1 : 2;
                        if (kind == 'U') continue;
                        BrowserClick(side, "pick", browser.Pick);
                        if (kind != 'A') continue;
                        foreach (var field in browser.PasswordFields) field.Text = "synthetic-lifetime-marker";
                        BrowserClick(side, "load", browser.Load); fields.AddRange(browser.PasswordFields);
                        browser.Entries.SelectedItem = browser.Entries.ItemsSource!.Cast<object>().Single(item => item.ToString()!.StartsWith("leaf.txt （", StringComparison.Ordinal));
                        Jobs(); clicks.Add(side + ":row:leaf.txt");
                    }
                    Jobs(); SavePng(dialog, Path.Combine(run, "dialog.png"));
                    nonEmptyFields = fields.Count(field => !string.IsNullOrEmpty(field.Text));
                    arraysBeforeClose = NonNullSlots();
                    Click(dialog.Compare); clicks.Add("compare");
                    if (submission is null) throw new InvalidOperationException("実CompareTask未観測");
                }
                if (gated)
                {
                    pump(reached.Task); Capture("gate-reached");
                    nonEmptyFields = fields.Count(field => !string.IsNullOrEmpty(field.Text)); arraysBeforeClose = NonNullSlots();
                    if (id is "load-close" or "picker-close") { Jobs(); SavePng(dialog, Path.Combine(run, "dialog.png")); }
                    Click(dialog.Cancel); clicks.Add("cancel-close"); Jobs(); Capture("closed-before-release");
                    // baselineの早期opener完了をそのまま記録し、成功へ読み替えない。
                    Verify(id + " opening waits before gate release", !opening.IsCompleted);
                    Verify(id + " original pending operation waits before release", (submission ?? pendingBrowser)?.IsCompleted == false);
                    Interlocked.Exchange(ref released, 1); Capture("gate-release"); release.TrySetResult();
                }
                pump(opening); Jobs();
                if (submission is not null) pump(submission);
                if (pendingBrowser is not null) pump(pendingBrowser);
                foreach (var observer in completedObservers) pump(observer);
                Jobs(); Capture("all-original-tasks-drained"); accepted = opening.Result;
                fieldsCleared = fields.All(field => string.IsNullOrEmpty(field.Text));
                var end = events.Single(value => value.Event == "opening-complete");
                Verify(id + " opener has drained original operation", id is "load-close" or "picker-close" ? end.BrowserComplete : end.SubmissionComplete);
                Verify(id + " opener credentials cleared", end.NonNullSlots == 0);
                Verify(id + " UI fields cleared separately", fieldsCleared);
                Verify(id + " acceptance", accepted == !gated);
                Verify(id + " parent store preserved", ReferenceEquals(store, host.ArchiveTexts) && store.Generation == generation);
                Verify(id + " parent identity preserved", Equals(identity, parent.CaptureIndependentTextState()));
                Verify(id + " protected output", File.ReadAllBytes(Path.Combine(run, "existing.out")).AsSpan().SequenceEqual("lifetime-existing-output\r\n"u8));
                if (!gated)
                {
                    var actual = host.ActivePane; adopted = actual.CaptureInputSelectionTestEvidence();
                    for (var side = 0; side < 3; side++) Verify(id + " adopted literal " + side, actual.TextEditor(side).Text == (recipe[side] == 'U' ? "" : texts[side]));
                    Jobs(); SavePng(host, Path.Combine(run, "adopted.png"));
                }
                var after = parent.CaptureInputSelectionTestEvidence(); var tabsAfter = host.SessionPanes.Count(); var finalGeneration = store.Generation; var capturedEvents = events.OrderBy(value => value.Sequence).ToArray();
                Verify(id + " only expected candidate tab", tabsAfter == tabsBefore + (gated ? 0 : 1));
                receipts.Add(writer =>
                {
                    writer.WriteStartObject(); writer.WriteString("id", id); writer.WriteString("recipe", recipe); writer.WriteBoolean("accepted", accepted);
                    writer.WriteNumber("tabsBefore", tabsBefore); writer.WriteNumber("tabsAfter", tabsAfter); writer.WriteNumber("storeBefore", generation); writer.WriteNumber("storeAfter", finalGeneration);
                    writer.WriteNumber("candidateCount", candidates); writer.WriteNumber("nonEmptyFieldsBeforeClose", nonEmptyFields); writer.WriteNumber("nonNullSlotsBeforeClose", arraysBeforeClose); writer.WriteBoolean("fieldsClearedAfterDrain", fieldsCleared);
                    writer.WriteStartArray("events"); foreach (var value in capturedEvents)
                    {
                        writer.WriteStartObject(); writer.WriteString("event", value.Event); writer.WriteNumber("sequence", value.Sequence); writer.WriteNumber("timestamp", value.Timestamp);
                        writer.WriteBoolean("openingComplete", value.OpeningComplete); writer.WriteBoolean("submissionComplete", value.SubmissionComplete); writer.WriteBoolean("browserComplete", value.BrowserComplete);
                        writer.WriteNumber("arraysObserved", value.ArraysObserved); writer.WriteNumber("nonNullSlots", value.NonNullSlots); writer.WriteBoolean("gateReleased", value.GateReleased); writer.WriteEndObject();
                    } writer.WriteEndArray(); writer.WriteStartArray("clicks"); foreach (var click in clicks) writer.WriteStringValue(click); writer.WriteEndArray();
                    writer.WritePropertyName("before"); before(writer); writer.WritePropertyName("after"); after(writer);
                    if (adopted is not null) { writer.WritePropertyName("adopted"); adopted(writer); } writer.WriteEndObject();
                });
            }
            finally
            {
                Interlocked.Exchange(ref released, 1); release.TrySetResult();
                if (dialog?.IsVisible == true) dialog.Close();
                if (submission is not null && !submission.IsCompleted) pump(submission);
                if (pendingBrowser is not null && !pendingBrowser.IsCompleted) pump(pendingBrowser);
                if (opening is not null && !opening.IsCompleted) pump(opening);
                using (var partialFile = File.Create(Path.Combine(run, "partial-events.json")))
                using (var partial = new Utf8JsonWriter(partialFile, new() { Indented = true }))
                {
                    partial.WriteStartArray(); foreach (var value in events.OrderBy(value => value.Sequence))
                    {
                        partial.WriteStartObject(); partial.WriteString("event", value.Event); partial.WriteNumber("sequence", value.Sequence); partial.WriteNumber("timestamp", value.Timestamp);
                        partial.WriteBoolean("openingComplete", value.OpeningComplete); partial.WriteBoolean("submissionComplete", value.SubmissionComplete); partial.WriteBoolean("browserComplete", value.BrowserComplete);
                        partial.WriteNumber("arraysObserved", value.ArraysObserved); partial.WriteNumber("nonNullSlots", value.NonNullSlots); partial.WriteBoolean("gateReleased", value.GateReleased); partial.WriteEndObject();
                    } partial.WriteEndArray();
                }
                foreach (var pane in host.SessionPanes) pane.DiscardChanges(); host.Close(); Jobs();
            }
        }
        try
        {
            Execute("compare-close", "AAA"); Execute("load-close", "AAA"); Execute("picker-close", "AAA");
            foreach (var recipe in new[] { "PPP", "UUU", "AAA", "APU", "UAP", "PUA" }) Execute("success-" + recipe, recipe);
        }
        finally
        {
            using var file = File.Create(Path.Combine(folder, "observations.json")); using var json = new Utf8JsonWriter(file, new() { Indented = true });
            using var process = Process.GetCurrentProcess();
            json.WriteStartObject(); json.WriteString("schema", "independent-text-input-lifetime-observations-v1"); json.WriteString("scope", "original-task-close-drain-regression-only");
            json.WriteNumber("pid", process.Id); json.WriteString("processCreationUtc", process.StartTime.ToUniversalTime()); json.WriteNumber("stopwatchFrequency", Stopwatch.Frequency);
            json.WriteNumber("completedCaseCount", receipts.Count); json.WriteNumber("expectedCaseCount", 9);
            json.WriteStartArray("cases"); foreach (var receipt in receipts) receipt(json); json.WriteEndArray();
            json.WriteStartArray("assertions"); foreach (var assertion in assertions) { json.WriteStartObject(); json.WriteString("name", assertion.Name); json.WriteBoolean("passed", assertion.Passed); json.WriteString("detail", assertion.Detail); json.WriteEndObject(); } json.WriteEndArray(); json.WriteEndObject();
        }
        foreach (var assertion in assertions) check(assertion.Name, assertion.Passed, assertion.Detail);
    }
    private static void SavePng(Window window, string path)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Lifetime実PNG未取得");
        using var file = File.Create(path); frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}

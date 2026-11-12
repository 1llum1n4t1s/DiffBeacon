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

internal static class HeadlessLineTransformTimestampClockChecks
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string Qualification = "engineering App draft; Native portable Update4/Now12/Today12 interval/snapshot/local-midnight only; natural Windows ja-JP/Tokyo observation; Mac/full AST/parser/copy constructor/Optimize/DST/clock anomalies unqualified";

    internal static void Run(MainWindow window, ComparisonPane pane, string output,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-timestamp-clock"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("日時関数検証には新規出力が必要です。");
        Directory.CreateDirectory(directory);
        using var evidence = NewFile(directory, "timestamp-clock-assertions.ndjson");
        var results = new List<(string Name, bool Passed, string Detail)>();
        void Verify(string name, bool ok, string detail = "")
        {
            Row(evidence, json => { json.WriteString("name", name); json.WriteBoolean("passed", ok);
                json.WriteString("detail", detail); json.WriteString("qualification", Qualification); });
            results.Add((name, ok, detail)); evidence.Flush(); check("line transform timestamp clock " + name, ok, detail);
        }
        T Wait<T>(Task<T> task) { pump(task); return task.GetAwaiter().GetResult(); }
        string Write(string name, string value)
        {
            using var file = NewFile(directory, name); file.Write(Utf8.GetBytes(value));
            return Path.Combine(directory, name);
        }
        TablePanel Table() => pane.GetVisualDescendants().OfType<TablePanel>().Single();
        void Compare() { pump(pane.CompareProjectAsync()); Settle(); }

        // 最初に失敗条件を固定する。時刻の固定goldenや別compiled式同士の一致は要求しない。
        // 空式・誤arity、rowごとの再捕捉、clock逆行/午夜跨ぎ/profile変化、snapshot永続化、
        // 取消/予算拒否で前回出力や原文が壊れる場合は拒否または未資格として記録する。
        RunNatural(directory, Verify);
        RunEngineering(directory, Verify);
        var textRule = new LineTransformRule("@cs @noopt array(now() - now(), today() - today())", TablePaneMask.All);
        var tableRule = new LineTransformRule("@cs array(now() - now(), today() - today())", TablePaneMask.All);
        var oldShown = LineTransformDialog.Shown; LineTransformDialog? shown = null;
        try
        {
            pane.SetLineTransforms([]); LineTransformDialog.Shown = dialog => shown = dialog;
            Click(Caption(pane, "行変換…")); Settle();
            var dialog = shown ?? throw new InvalidOperationException("日時関数dialogがありません。");
            dialog.Width = 720; dialog.Height = 550; Settle();
            foreach (var invalid in new[] { "", "now(1)", "today(1)", "now(1, 2)" })
            {
                dialog.Expression.Text = invalid; Click(Named(dialog, "LineTransformAdd"));
                Verify("dialog rejects " + Convert.ToHexString(Utf8.GetBytes(invalid)), dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text));
            }
            dialog.Expression.Text = "@noopt now()"; Click(Named(dialog, "LineTransformAdd"));
            Verify("dialog add functions", dialog.Rules.Count == 1);
            dialog.RuleList.SelectedIndex = 0; dialog.Expression.Text = textRule.Expression; Click(Named(dialog, "LineTransformChange"));
            Verify("dialog change dynamic Line", dialog.Rules.SequenceEqual([textRule]));
            dialog.Expression.Text = "today(1)"; Click(Named(dialog, "LineTransformChange"));
            Verify("invalid change retains candidate", dialog.Rules.SequenceEqual([textRule]) && !string.IsNullOrEmpty(dialog.Error.Text));
            Settle();
            using (var bitmap = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("日時関数dialog PNGがありません。"))
            using (var png = NewFile(directory, "timestamp-clock-dialog.png")) bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            using (var bounds = NewFile(directory, "timestamp-clock-dialog-bounds.ndjson"))
            foreach (var control in new Control[] { dialog.Expression, dialog.Target, dialog.RuleList, Named(dialog, "LineTransformAdd"), dialog.Accept, dialog.Cancel })
            {
                control.BringIntoView(); Settle(); var point = control.TranslatePoint(default, dialog);
                // ウィンドウ内でも内部viewportで隠れる部分を到達可能と扱わない。
                var clip = new Rect(default(Point), dialog.ClientSize); var viewportCount = 0;
                foreach (var viewport in control.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>())
                {
                    viewportCount++; var origin = viewport.TranslatePoint(default, dialog);
                    clip = origin is { } at ? clip.Intersect(new Rect(at, viewport.Bounds.Size)) : default;
                }
                var reachable = point is { } p && control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
                    && clip.Width > 0 && clip.Height > 0 && p.X >= clip.X - .01 && p.Y >= clip.Y - .01
                    && p.X + control.Bounds.Width <= clip.Right + .01 && p.Y + control.Bounds.Height <= clip.Bottom + .01;
                Row(bounds, json => { json.WriteString("control", control.Name); json.WriteNumber("x", point?.X ?? -1); json.WriteNumber("y", point?.Y ?? -1);
                    json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("height", control.Bounds.Height);
                    json.WriteNumber("clientWidth", dialog.ClientSize.Width); json.WriteNumber("clientHeight", dialog.ClientSize.Height);
                    json.WriteNumber("clipX", clip.X); json.WriteNumber("clipY", clip.Y); json.WriteNumber("clipWidth", clip.Width); json.WriteNumber("clipHeight", clip.Height);
                    json.WriteNumber("internalViewportCount", viewportCount); json.WriteBoolean("reachable", reachable); });
                Verify("dialog reachable " + control.Name, reachable, "timestamp-clock-dialog-bounds.ndjson");
                using var frame = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("dialog control PNG missing");
                using var frameFile = NewFile(directory, "dialog-control-" + control.Name + ".png");
                frame.Save(frameFile, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            Click(dialog.Accept); Click(dialog.Accept); Settle();
            Verify("duplicate apply adopts one rule", pane.CaptureProject().LineTransformRules.SequenceEqual([textRule]));
            shown = null; Click(Caption(pane, "行変換…")); Settle(); dialog = shown ?? throw new InvalidOperationException("日時関数dialog再開失敗です。");
            dialog.Expression.Text = "@noopt today()"; Click(Named(dialog, "LineTransformAdd")); Click(dialog.Cancel); Settle();
            Verify("cancel keeps adopted settings", pane.CaptureProject().LineTransformRules.SequenceEqual([textRule]));
        }
        finally { LineTransformDialog.Shown = oldShown; }

        foreach (var mode in new[] { "Text", "Table" })
        {
            var rule = mode == "Text" ? textRule : tableRule;
            var raw = mode == "Text" ? new[] { "header\r\n2000-01-02T03:04:05\r\nkeep\r\n", "header\r\n2000-01-02T10:11:12\r\nkeep\r\n", "header\r\n2000-01-02\r\nkeep\r\n" }
                : new[] { "date\r\n2000-01-02T03:04:05\r\n", "date\r\n2000-01-08T10:11:12\r\n", "date\r\n2000-01-09\r\n" };
            var paths = raw.Select((text, side) => Write("function-" + mode + "-" + side + (mode == "Table" ? ".csv" : ".txt"), text)).ToArray();
            var hashes = paths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
            var project = new ComparisonProject { Mode = mode, LeftPath = paths[0], BasePath = paths[1], RightPath = paths[2],
                TableDelimiter = ',', TableQuote = '"', LineTransformRules = [rule] };
            pane.DiscardChanges(); pane.ApplyProject(project); Compare();
            bool EqualKeys() => mode == "Text" ? pane.CurrentDiff is { HasDifferences: false } : !Table().Comparison.HasDifferences && Table().Comparison.Documents.Count == 3;
            Verify(mode + " comparison retains raw", EqualKeys() && pane.LeftEditor.Text == raw[0] && pane.RightEditor.Text == raw[2]);
            Compare(); Verify(mode + " recompare", EqualKeys() && pane.CompareButton.IsEnabled);
            screenshot("timestamp-clock-" + mode.ToLowerInvariant() + ".png");
            var workspace = Path.Combine(directory, "timestamp-clock-" + mode + "-workspace.json");
            pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
            var restored = Wait(WorkspaceStore.LoadWorkspaceAsync(workspace)); pane.ApplyProject(restored.Entries.Single()); Compare();
            Verify(mode + " workspace rules roundtrip", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]) && EqualKeys());
            var serialized = File.ReadAllText(workspace, Utf8);
            Verify(mode + " runtime host not persisted", !serialized.Contains("TimestampEnvironment", StringComparison.OrdinalIgnoreCase)
                && !serialized.Contains("CurrentTzdSeconds", StringComparison.OrdinalIgnoreCase) && !serialized.Contains("ProfileId", StringComparison.OrdinalIgnoreCase) && !serialized.Contains("CalendarEnvironment", StringComparison.OrdinalIgnoreCase) && !serialized.Contains("ClockSnapshot", StringComparison.OrdinalIgnoreCase) && !serialized.Contains("EpochMicroseconds", StringComparison.OrdinalIgnoreCase));
            var htmlPath = Path.Combine(directory, "timestamp-clock-" + mode + "-report.html"); pump(pane.SaveReportAsync(htmlPath));
            var html = File.ReadAllText(htmlPath, Utf8);
            Verify(mode + " report raw and functions", html.Contains("now", StringComparison.OrdinalIgnoreCase) && html.Contains("today", StringComparison.OrdinalIgnoreCase)
                && html.Contains(mode == "Text" ? "2000-01-02T03:04:05" : "2000-01-02T03:04:05", StringComparison.Ordinal)
                && html.Contains(mode == "Text" ? "2000-01-02" : "2000-01-09", StringComparison.Ordinal));

            var state = pane.CaptureAdoptionState(); var diff = pane.CurrentDiff;
            var adoptedTable = mode == "Table" ? Table() : null;
            pane.SetLineTransforms([new("@noopt todatestr(\"not a date\")", TablePaneMask.All)]); Compare();
            Verify(mode + " invalid clock-dependent comparison keeps view and restores controls", Equals(state, pane.CaptureAdoptionState())
                && (mode == "Text" ? ReferenceEquals(diff, pane.CurrentDiff) : ReferenceEquals(adoptedTable, Table()))
                && pane.LeftEditor.Text == raw[0] && pane.RightEditor.Text == raw[2] && pane.CompareButton.IsEnabled);
            Verify(mode + " failure message presented", pane.GetVisualDescendants().OfType<TextBlock>()
                .Any(block => block.IsEffectivelyVisible && (block.Text ?? "").Contains("not a date", StringComparison.Ordinal)));
            Layout(mode.ToLowerInvariant() + "-error", pane);
            screenshot("timestamp-clock-" + mode.ToLowerInvariant() + "-invalid-preserved.png");
            var oldReady = mode == "Table" ? pane.TableReadyForAdoption : pane.LegacyTextReadyForAdoption;
            try
            {
                pane.SetLineTransforms([rule]);
                if (mode == "Table") pane.TableReadyForAdoption = () => Click(Caption(pane, "中止"));
                else pane.LegacyTextReadyForAdoption = () => Click(Caption(pane, "中止"));
                Compare();
                Verify(mode + " cancel before adoption keeps view and restores controls",
                    (mode == "Text" ? ReferenceEquals(diff, pane.CurrentDiff) : ReferenceEquals(adoptedTable, Table()))
                    && Equals(state, pane.CaptureAdoptionState()) && pane.LeftEditor.Text == raw[0] && pane.RightEditor.Text == raw[2] && pane.CompareButton.IsEnabled);
                screenshot("timestamp-clock-" + mode.ToLowerInvariant() + "-cancel-preserved.png");
            }
            finally
            {
                if (mode == "Table") pane.TableReadyForAdoption = oldReady;
                else pane.LegacyTextReadyForAdoption = oldReady;
            }
            pane.SetLineTransforms([rule]); Compare();
            Verify(mode + " recovery comparison", EqualKeys() && pane.CompareButton.IsEnabled);
            Verify(mode + " input bytes preserved", paths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).SequenceEqual(hashes));
        }
        var shared = pane.CaptureProject(); window.AddSession(); var extra = window.ActivePane;
        extra.ApplyProject(shared); pump(extra.CompareProjectAsync()); Settle();
        Verify("second pane settings", extra.CaptureProject().LineTransformRules.SequenceEqual([tableRule])
            && !extra.GetVisualDescendants().OfType<TablePanel>().Single().Comparison.HasDifferences && extra.CompareButton.IsEnabled);
        screenshot("timestamp-clock-shared-settings.png");
        var oldWidth = window.Width; var oldHeight = window.Height;
        try
        {
            window.Width = 1280; window.Height = 850; Layout("normal", extra);
            window.Width = 850; window.Height = 550; Layout("minimum", extra);
            for (var index = 0; index < 12; index++) window.AddSession();
            var manyTabs = window.ActivePane; manyTabs.ApplyProject(shared); pump(manyTabs.CompareProjectAsync()); Settle();
            window.Width = 850; window.Height = 550; Layout("minimum-many-tabs", manyTabs);
        }
        finally { window.Width = oldWidth; window.Height = oldHeight; Settle(); }

        using (var summary = NewFile(directory, "clock-assertions.json"))
        using (var json = new Utf8JsonWriter(summary, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject(); json.WriteString("qualification", Qualification); json.WriteString("nativeStatus", "OBSERVATION_ONLY; see per-case evidence");
            json.WriteString("naturalEvidence", "clock-natural-observations.ndjson"); json.WriteBoolean("nativeFullAstExecuted", false); json.WriteString("harness", "App headless engineering checks; not Native full AST");
            json.WriteNumber("assertionCount", results.Count); json.WriteNumber("failed", results.Count(row => !row.Passed));
            json.WriteStartArray("assertions"); foreach (var row in results)
            { json.WriteStartObject(); json.WriteString("name", row.Name); json.WriteBoolean("passed", row.Passed); json.WriteString("detail", row.Detail); json.WriteEndObject(); }
            json.WriteEndArray(); json.WriteEndObject();
        }
        void Layout(string name, ComparisonPane current)
        {
            Settle();
            var toolbar = current.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Content is Control content
                && content.GetVisualDescendants().Contains(current.CompareButton));
            using var bounds = NewFile(directory, name + "-bounds.ndjson");
            foreach (var (label, control) in new (string, Control)[] { ("transform", Caption(current, "行変換…")),
                ("left input", current.LeftPath), ("middle input", current.BasePath), ("right input", current.RightPath), ("compare", current.CompareButton) })
            {
                control.BringIntoView(); Settle(); var point = control.TranslatePoint(default, toolbar);
                var reachable = point is { } p && control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
                    && p.X >= -.01 && p.Y >= -.01 && p.X + control.Bounds.Width <= toolbar.Viewport.Width + .01
                    && p.Y + control.Bounds.Height <= toolbar.Viewport.Height + .01;
                Row(bounds, json => { json.WriteString("control", label); json.WriteNumber("windowWidth", window.Width); json.WriteNumber("windowHeight", window.Height);
                    json.WriteNumber("x", point?.X ?? -1); json.WriteNumber("y", point?.Y ?? -1); json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("height", control.Bounds.Height);
                    json.WriteNumber("viewportWidth", toolbar.Viewport.Width); json.WriteNumber("viewportHeight", toolbar.Viewport.Height);
                    json.WriteBoolean("reachable", reachable); json.WriteBoolean("enabled", control.IsEnabled); });
                Verify(name + " reachable " + label, reachable, name + "-bounds.ndjson");
                screenshot("timestamp-clock-" + name + "-" + label.Replace(' ', '-') + ".png");
            }
            toolbar.Offset = default; Settle();
        }
    }

    private static void RunNatural(string directory, Action<string, bool, string> verify)
    {
        using var observations = NewFile(directory, "clock-natural-observations.ndjson");
        foreach (var expression in new[] { "@cs array(now() - d\"1970-01-01T00:00:00Z\", today() - d\"1970-01-01T00:00:00Z\")", "@cs @noopt array(now() - d\"1970-01-01T00:00:00Z\", today() - d\"1970-01-01T00:00:00Z\")" })
        {
            if (!OperatingSystem.IsWindows())
            {
                Row(observations, json => { json.WriteString("expression", expression); json.WriteString("status", "UNQUALIFIED"); json.WriteString("reason", "macOS Native observation not measured"); });
                continue;
            }
            var beforeHost = LineTransformWindowsTimestampEnvironment.Capture();
            var before = beforeHost.CaptureNowEpochMicroseconds();
            var input = new[] { "RAW first", "RAW second", "RAW third" }; var original = input.ToArray();
            var rules = new[] { new LineTransformRule(expression, TablePaneMask.All) };
            var plan = LineTransformPlan.Compile(rules, new LineTransformBudget(), timestampEnvironment: LineTransformTimestampEnvironments.ForRules(rules));
            var afterHost = LineTransformWindowsTimestampEnvironment.Capture();
            var after = afterHost.CaptureNowEpochMicroseconds();
            var keys = plan.Transform(input, TablePaneMask.Left, false);
            var repeated = plan.Transform(input, TablePaneMask.Right, false);
            var parts = keys[0].Trim('[', ']').Split(',');
            var parsed = parts.Length == 2 && long.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)
                && long.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _);
            long now = parsed ? long.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture) : 0;
            long today = parsed ? long.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture) : 0;
            var profileBefore = beforeHost.Snapshot; var profileAfter = afterHost.Snapshot;
            bool qualified = Tokyo(profileBefore) && Tokyo(profileAfter) && before <= after;
            long localBefore = before + 32_400_000_000L, localAfter = after + 32_400_000_000L;
            bool midnightCrossed = localBefore / 86_400_000_000L != localAfter / 86_400_000_000L;
            qualified &= !midnightCrossed;
            long expectedMidnight = parsed ? (now + 32_400_000_000L) / 86_400_000_000L * 86_400_000_000L - 32_400_000_000L : 0;
            bool stable = parsed && keys.All(key => key == keys[0]) && repeated.SequenceEqual(keys) && input.SequenceEqual(original);
            bool interval = parsed && now >= before && now <= after;
            bool midnight = parsed && today == expectedMidnight;
            Row(observations, json => {
                json.WriteString("expression", expression); json.WriteString("scope", "natural Windows observation; not Native full AST");
                json.WriteNumber("beforeEpochMicroseconds", before); json.WriteNumber("afterEpochMicroseconds", after);
                json.WriteNumber("nowEpochMicroseconds", now); json.WriteNumber("todayEpochMicroseconds", today);
                json.WriteNumber("expectedLocalMidnightUtc", expectedMidnight); json.WriteBoolean("clockJumpDetected", before > after);
                json.WriteBoolean("midnightCrossed", midnightCrossed); json.WriteBoolean("profileQualifiedBefore", Tokyo(profileBefore));
                json.WriteBoolean("profileQualifiedAfter", Tokyo(profileAfter)); json.WriteString("status", qualified ? "OBSERVATION_ONLY" : "UNQUALIFIED");
                Profile(json, "beforeProfile", profileBefore); Profile(json, "afterProfile", profileAfter);
                json.WriteStartArray("keys"); foreach (var key in keys) json.WriteStringValue(key); json.WriteEndArray();
                json.WriteBoolean("stableSnapshot", stable); json.WriteBoolean("withinClockCapture", interval); json.WriteBoolean("localMidnight", midnight);
            });
            verify("natural stable snapshot " + expression, stable, "clock-natural-observations.ndjson");
            if (qualified) verify("natural clock interval and local midnight " + expression, interval && midnight, "OBSERVATION_ONLY; clock-natural-observations.ndjson");
        }
    }
    private static bool Tokyo(WindowsTimestampProfile profile) => profile.UserLcid == 1041 && profile.UserLocale == "ja-JP"
        && profile.TimezoneState == 0 && profile.TimezoneStandardName == "東京 (標準時)" && profile.BiasMinutes == -540
        && profile.StandardBiasMinutes == 0 && profile.DaylightBiasMinutes == -60 && profile.CurrentTzdSeconds == 32400 && profile.AmbientTz is null;
    private static void Profile(Utf8JsonWriter json, string name, WindowsTimestampProfile profile)
    {
        json.WriteStartObject(name); json.WriteNumber("userLcid", profile.UserLcid); json.WriteString("userLocale", profile.UserLocale);
        json.WriteNumber("timezoneState", profile.TimezoneState); json.WriteString("timezoneStandardName", profile.TimezoneStandardName);
        json.WriteNumber("biasMinutes", profile.BiasMinutes); json.WriteNumber("standardBiasMinutes", profile.StandardBiasMinutes);
        json.WriteNumber("daylightBiasMinutes", profile.DaylightBiasMinutes); json.WriteNumber("currentTzdSeconds", profile.CurrentTzdSeconds);
        json.WriteString("ambientTz", profile.AmbientTz); json.WriteString("capturedUtc", profile.CapturedUtc); json.WriteEndObject();
    }
    private static void RunEngineering(string directory, Action<string, bool, string> verify)
    {
        using var observations = NewFile(directory, "clock-injected-engineering.ndjson");
        var cases = new (string Id, int CurrentOffset, int MidnightOffset, string Prefix, string Expression, string Expected)[] {
            ("same snapshot normal", 0, 0, "@cs ", "array(now() - now(), today() - today())", "[0, 0]"),
            ("same snapshot noopt", 0, 0, "@cs @noopt ", "array(now() - now(), today() - today())", "[0, 0]"),
            ("midnight offset independent of current offset", 3600, 0, "@cs @noopt ", "today() - d\"2000-01-02T00:00:00Z\"", "0"),
            ("midnight calendar offset engineering", 0, 3600, "@cs @noopt ", "today() - d\"2000-01-02T00:00:00Z\"", "-3600000000")
        };
        foreach (var item in cases)
        {
            using var cancellation = new CancellationTokenSource();
            var host = new ClockProbe(cancellation, "normal", item.CurrentOffset, item.MidnightOffset);
            var rules = new[] { new LineTransformRule(item.Prefix + item.Expression, TablePaneMask.All) };
            var input = new[] { "RAW first", "RAW second", "RAW third" }; var original = input.ToArray();
            var plan = LineTransformPlan.Compile(rules, new LineTransformBudget(), cancellation.Token, host);
            var left = plan.Transform(input, TablePaneMask.Left, false); var right = plan.Transform(input, TablePaneMask.Right, false);
            bool ok = host.ClockCalls == 1 && host.CalendarCalls == 1 && left.All(key => key == item.Expected) && right.SequenceEqual(left) && input.SequenceEqual(original);
            Row(observations, json => { json.WriteString("id", item.Id); json.WriteString("expression", rules[0].Expression);
                json.WriteString("scope", "injected engineering; not Native/OS/DST qualification"); json.WriteNumber("clockCalls", host.ClockCalls);
                json.WriteNumber("calendarCalls", host.CalendarCalls); json.WriteNumber("currentOffset", item.CurrentOffset);
                json.WriteNumber("midnightOffset", item.MidnightOffset); json.WriteBoolean("passed", ok); });
            verify("engineering " + item.Id, ok, "clock-injected-engineering.ndjson");
        }
        // 連続日付の午夜差を計算する。23/25時間は注入契約だけで、実OS DST測定ではない。
        foreach (var (id, firstOffset, secondOffset, expectedHours) in new (string Id, int FirstOffset, int SecondOffset, int ExpectedHours)[] {
            ("23h-day-engineering", 0, 3600, 23), ("25h-day-engineering", 3600, 0, 25) })
        {
            using var cancellation = new CancellationTokenSource();
            var first = new ClockProbe(cancellation, "normal", 0, firstOffset, 946_814_400_000_000L);
            var second = new ClockProbe(cancellation, "normal", 0, secondOffset, 946_900_800_000_000L);
            var rules = new[] { new LineTransformRule("@cs @noopt today() - d\"1970-01-01T00:00:00Z\"", TablePaneMask.All) };
            string a = LineTransformPlan.Compile(rules, new LineTransformBudget(), cancellation.Token, first).Transform(["RAW"], TablePaneMask.Left, false)[0];
            string b = LineTransformPlan.Compile(rules, new LineTransformBudget(), cancellation.Token, second).Transform(["RAW"], TablePaneMask.Left, false)[0];
            long delta = long.Parse(b, System.Globalization.CultureInfo.InvariantCulture) - long.Parse(a, System.Globalization.CultureInfo.InvariantCulture);
            bool ok = delta == expectedHours * 3_600_000_000L && first.ClockCalls == 1 && second.ClockCalls == 1 && first.CalendarCalls == 1 && second.CalendarCalls == 1;
            Row(observations, json => { json.WriteString("id", id); json.WriteString("scope", "injected engineering; not measured OS DST");
                json.WriteNumber("firstMidnightOffset", firstOffset); json.WriteNumber("secondMidnightOffset", secondOffset);
                json.WriteNumber("deltaMicroseconds", delta); json.WriteNumber("expectedHours", expectedHours); json.WriteBoolean("passed", ok); });
            verify("engineering " + id, ok, "clock-injected-engineering.ndjson");
        }
        foreach (var variant in new[] { "cancel-before", "cancel-clock", "cancel-calendar", "budget", "clock-failure" })
        {
            using var cancellation = new CancellationTokenSource(); if (variant == "cancel-before") cancellation.Cancel();
            var host = new ClockProbe(cancellation, variant, 0, 0); Exception? failure = null;
            string[] keys = ["previous-output-sentinel"]; var input = new[] { "RAW preserved" }; var original = input.ToArray();
            try {
                var plan = LineTransformPlan.Compile([new("@noopt now()", TablePaneMask.All)],
                    new LineTransformBudget(variant == "budget" ? 1 : 128L * 1024 * 1024), cancellation.Token, host);
                keys = plan.Transform(input, TablePaneMask.Left, false);
            } catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { failure = error; }
            bool ok = input.SequenceEqual(original) && keys.SequenceEqual(["previous-output-sentinel"])
                && (variant.StartsWith("cancel-", StringComparison.Ordinal) ? failure is OperationCanceledException : failure is InvalidOperationException)
                && host.ClockCalls == (variant is "cancel-before" or "budget" ? 0 : 1)
                && host.CalendarCalls == (variant == "cancel-calendar" ? 1 : 0);
            Row(observations, json => { json.WriteString("id", variant); json.WriteString("scope", "injected engineering; not Native qualification");
                json.WriteString("exceptionType", failure?.GetType().FullName); json.WriteNumber("clockCalls", host.ClockCalls);
                json.WriteNumber("calendarCalls", host.CalendarCalls); json.WriteBoolean("passed", ok); });
            verify("engineering " + variant, ok, "clock-injected-engineering.ndjson");
        }
    }
    private sealed class ClockProbe(CancellationTokenSource cancellation, string variant, int currentOffset, int midnightOffset, long nowEpoch = 946_814_400_000_000L) : ILineTransformTimestampClockEnvironment
    {
        public int ClockCalls { get; private set; }
        public int CalendarCalls { get; private set; }
        public string ProfileId => "engineering-clock-only";
        public int CurrentTzdSeconds => currentOffset;
        public long CaptureNowEpochMicroseconds() {
            ClockCalls++; if (variant == "cancel-clock") cancellation.Cancel();
            if (variant == "clock-failure") throw new InvalidOperationException("engineering clock failure");
            return nowEpoch; // 2000-01-02T12:00:00Z。時計観測のgoldenではない。
        }
        public int GetCalendarMidnightOffset(int year, int month, int day, long naiveEpochMicroseconds) {
            CalendarCalls++; if (variant == "cancel-calendar") cancellation.Cancel(); return midnightOffset;
        }
        public bool TryParseWindowsLocale(string text, out long epochMicroseconds) { epochMicroseconds = 0; return false; }
        public bool TryGetSerializationOffset(long epochMicroseconds, out int seconds, out string failureText) { seconds = currentOffset; failureText = ""; return true; }
    }
    private static FileStream NewFile(string directory, string name) => new(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write);
    private static void Row(Stream file, Action<Utf8JsonWriter> write)
    {
        using (var json = new Utf8JsonWriter(file)) { json.WriteStartObject(); write(json); json.WriteEndObject(); json.Flush(); }
        file.WriteByte((byte)'\n');
    }
    private static void Settle() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Button Caption(Control owner, string caption) => owner.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, caption));
    private static Button Named(LineTransformDialog dialog, string name) => dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
    private static void RequireExternal(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var forbidden in new[] { Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") })
        {
            var root = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new IOException("日時関数検証は外部出力が必要です。");
        }
        if (path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase))) throw new IOException("禁止名の出力です。");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンク経由の出力です。");
    }
}





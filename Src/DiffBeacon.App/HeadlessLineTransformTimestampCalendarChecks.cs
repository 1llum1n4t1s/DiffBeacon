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

internal static class HeadlessLineTransformTimestampCalendarChecks
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string Qualification = "engineering/public App path; selected 108 Native bodies/18 excluded boundaries; natural Windows ja-JP/Tokyo only; Mac/other profile/full AST/Optimize/minimal floating year unqualified; injection engineering separate";

    internal static void Run(MainWindow window, ComparisonPane pane, string output,
        Action<string, bool, string> check, Action<string> screenshot, Action<Task> pump)
    {
        var directory = Path.GetFullPath(Path.Combine(output, "line-transform-timestamp-calendar"));
        RequireExternal(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("日時関数検証には新規出力が必要です。");
        Directory.CreateDirectory(directory);
        using var evidence = NewFile(directory, "timestamp-calendar-assertions.ndjson");
        var results = new List<(string Name, bool Passed, string Detail)>();
        void Verify(string name, bool ok, string detail = "")
        {
            Row(evidence, json => { json.WriteString("name", name); json.WriteBoolean("passed", ok);
                json.WriteString("detail", detail); json.WriteString("qualification", Qualification); });
            results.Add((name, ok, detail)); evidence.Flush(); check("line transform timestamp calendar " + name, ok, detail);
        }
        T Wait<T>(Task<T> task) { pump(task); return task.GetAwaiter().GetResult(); }
        string Write(string name, string value)
        {
            using var file = NewFile(directory, name); file.Write(Utf8.GetBytes(value));
            return Path.Combine(directory, name);
        }
        TablePanel Table() => pane.GetVisualDescendants().OfType<TablePanel>().Single();
        void Compare() { pump(pane.CompareProjectAsync()); Settle(); }

        // 選択した自然host原本値だけを使う。別profileは未資格として記録し、合格数へ混ぜない。
        var nativeProfileQualified = false;
        using (var profileFile = NewFile(directory, "calendar-profile.ndjson"))
        {
            if (OperatingSystem.IsWindows())
            {
                var profile = LineTransformWindowsTimestampEnvironment.Capture().Snapshot;
                nativeProfileQualified = profile.UserLcid == 1041 && profile.UserLocale == "ja-JP"
                    && profile.TimezoneState == 0 && profile.TimezoneStandardName == "東京 (標準時)"
                    && profile.BiasMinutes == -540 && profile.StandardBiasMinutes == 0 && profile.DaylightBiasMinutes == -60
                    && profile.CurrentTzdSeconds == 32400 && profile.AmbientTz is null;
                Row(profileFile, json => { json.WriteNumber("userLcid", profile.UserLcid); json.WriteString("userLocale", profile.UserLocale);
                    json.WriteNumber("timezoneState", profile.TimezoneState); json.WriteString("timezoneStandardName", profile.TimezoneStandardName);
                    json.WriteNumber("biasMinutes", profile.BiasMinutes); json.WriteNumber("standardBiasMinutes", profile.StandardBiasMinutes);
                    json.WriteNumber("daylightBiasMinutes", profile.DaylightBiasMinutes); json.WriteNumber("currentTzdSeconds", profile.CurrentTzdSeconds);
                    json.WriteString("ambientTz", profile.AmbientTz); json.WriteString("capturedUtc", profile.CapturedUtc);
                    json.WriteBoolean("nativeProfileQualified", nativeProfileQualified); });
            }
            else Row(profileFile, json => { json.WriteBoolean("nativeProfileQualified", false);
                json.WriteString("reason", "Mac/other profile Native result not qualified"); });
        }
        var cases = new (string Id, string Expression, string[] Input, string? Expected, string? ErrorPhase)[]
        {
            ("startofweek-sunday/literal-noopt/serialized", "@noopt startofweek(\"2000-01-02T03:04:05Z\")", ["source sentinel"], "2000-01-02 00:00:00", null),
            ("startofweek-sunday/literal-noopt/epoch", "@noopt (startofweek(\"2000-01-02T03:04:05Z\")) - d\"1970-01-01T00:00:00Z\"", ["source sentinel"], "946738800000000", null),
            ("startofweek-sunday/line-noopt/serialized", "@noopt startofweek(line)", ["2000-01-02T03:04:05Z"], "2000-01-02 00:00:00", null),
            ("startofweek-sunday/line-noopt/epoch", "@noopt (startofweek(line)) - d\"1970-01-01T00:00:00Z\"", ["2000-01-02T03:04:05Z"], "946738800000000", null),
            ("startofweek-epoch-zero/literal-noopt/body-exception", "@noopt startofweek(\"1970-01-01T00:00:00Z\")", ["source sentinel"], null, "transform"),
            ("startofweek-epoch-zero/line-noopt/body-exception", "@noopt startofweek(line)", ["1970-01-01T00:00:00Z"], null, "transform"),
            ("startofweek-raw-year2000/literal-noopt/serialized", "@noopt startofweek((d\"1970-01-01T00:00:00Z\" + toint(\"946684800000000\")))", ["source sentinel"], "1999-12-26 00:00:00", null),
            ("startofweek-raw-year2000/literal-noopt/epoch", "@noopt (startofweek((d\"1970-01-01T00:00:00Z\" + toint(\"946684800000000\")))) - d\"1970-01-01T00:00:00Z\"", ["source sentinel"], "946134000000000", null),
            ("startofweek-none/literal-noopt/serialized", "@noopt startofweek(none)", ["source sentinel"], "none", null),
            ("startofweek-bool/literal-noopt/serialized", "@noopt startofweek(true)", ["source sentinel"], "none", null),
            ("startofweek-int/literal-noopt/serialized", "@noopt startofweek(toint(\"7\"))", ["source sentinel"], "none", null),
            ("startofweek-float/literal-noopt/serialized", "@noopt startofweek(1.25)", ["source sentinel"], "none", null),
            ("startofweek-array-empty/literal-noopt/serialized", "@noopt startofweek(array())", ["source sentinel"], "[]", null),
            ("startofweek-array-mixed/literal-noopt/serialized", "@noopt startofweek(array(\"2000-01-02T03:04:05Z\", none, toint(\"7\"), false, (d\"1970-01-01T00:00:00Z\" + toint(\"946684800000000\")), array(\"2000-01-03\")))", ["source sentinel"], "[2000-01-02 00:00:00, none, none, none, 1999-12-26 00:00:00, none]", null),
            ("startofweek-array-invalid-middle/literal-noopt/body-exception", "@noopt startofweek(array(\"2000-01-02T03:04:05Z\", \"not a date\", \"2000-01-03\"))", ["source sentinel"], null, "transform"),
            ("startofweek-array-nested/literal-noopt/serialized", "@noopt startofweek(array(array()))", ["source sentinel"], "[none]", null),
            ("startofmonth-sunday/literal-noopt/serialized", "@noopt startofmonth(\"2000-01-02T03:04:05Z\")", ["source sentinel"], "2000-01-01 00:00:00", null),
            ("startofmonth-sunday/literal-noopt/epoch", "@noopt (startofmonth(\"2000-01-02T03:04:05Z\")) - d\"1970-01-01T00:00:00Z\"", ["source sentinel"], "946652400000000", null),
            ("startofmonth-sunday/line-noopt/serialized", "@noopt startofmonth(line)", ["2000-01-02T03:04:05Z"], "2000-01-01 00:00:00", null),
            ("startofmonth-sunday/line-noopt/epoch", "@noopt (startofmonth(line)) - d\"1970-01-01T00:00:00Z\"", ["2000-01-02T03:04:05Z"], "946652400000000", null),
            ("startofmonth-epoch-zero/literal-noopt/serialized", "@noopt startofmonth(\"1970-01-01T00:00:00Z\")", ["source sentinel"], "System exception", null),
            ("startofmonth-epoch-zero/literal-noopt/epoch", "@noopt (startofmonth(\"1970-01-01T00:00:00Z\")) - d\"1970-01-01T00:00:00Z\"", ["source sentinel"], "-32400000000", null),
            ("startofmonth-epoch-zero/line-noopt/serialized", "@noopt startofmonth(line)", ["1970-01-01T00:00:00Z"], "System exception", null),
            ("startofmonth-epoch-zero/line-noopt/epoch", "@noopt (startofmonth(line)) - d\"1970-01-01T00:00:00Z\"", ["1970-01-01T00:00:00Z"], "-32400000000", null),
            ("startofmonth-raw-year2000/literal-noopt/serialized", "@noopt startofmonth((d\"1970-01-01T00:00:00Z\" + toint(\"946684800000000\")))", ["source sentinel"], "2000-01-01 00:00:00", null),
            ("startofmonth-raw-year2000/literal-noopt/epoch", "@noopt (startofmonth((d\"1970-01-01T00:00:00Z\" + toint(\"946684800000000\")))) - d\"1970-01-01T00:00:00Z\"", ["source sentinel"], "946652400000000", null),
            ("startofmonth-none/literal-noopt/serialized", "@noopt startofmonth(none)", ["source sentinel"], "none", null),
            ("startofmonth-bool/literal-noopt/serialized", "@noopt startofmonth(true)", ["source sentinel"], "none", null),
            ("startofmonth-int/literal-noopt/serialized", "@noopt startofmonth(toint(\"7\"))", ["source sentinel"], "none", null),
            ("startofmonth-float/literal-noopt/serialized", "@noopt startofmonth(1.25)", ["source sentinel"], "none", null),
            ("startofmonth-array-empty/literal-noopt/serialized", "@noopt startofmonth(array())", ["source sentinel"], "[]", null),
            ("startofmonth-array-mixed/literal-noopt/serialized", "@noopt startofmonth(array(\"2000-01-02T03:04:05Z\", none, toint(\"7\"), false, (d\"1970-01-01T00:00:00Z\" + toint(\"946684800000000\")), array(\"2000-01-03\")))", ["source sentinel"], "[2000-01-01 00:00:00, none, none, none, 2000-01-01 00:00:00, none]", null),
            ("startofmonth-array-invalid-middle/literal-noopt/body-exception", "@noopt startofmonth(array(\"2000-01-02T03:04:05Z\", \"not a date\", \"2000-01-03\"))", ["source sentinel"], null, "transform"),
            ("startofmonth-array-nested/literal-noopt/serialized", "@noopt startofmonth(array(array()))", ["source sentinel"], "[none]", null),
            ("startofyear-sunday/literal-noopt/serialized", "@noopt startofyear(\"2000-01-02T03:04:05Z\")", ["source sentinel"], "2000-01-01 00:00:00", null),
            ("startofyear-sunday/literal-noopt/epoch", "@noopt (startofyear(\"2000-01-02T03:04:05Z\")) - d\"1970-01-01T00:00:00Z\"", ["source sentinel"], "946652400000000", null),
            ("startofyear-sunday/line-noopt/serialized", "@noopt startofyear(line)", ["2000-01-02T03:04:05Z"], "2000-01-01 00:00:00", null),
            ("startofyear-sunday/line-noopt/epoch", "@noopt (startofyear(line)) - d\"1970-01-01T00:00:00Z\"", ["2000-01-02T03:04:05Z"], "946652400000000", null),
            ("startofyear-epoch-zero/literal-noopt/serialized", "@noopt startofyear(\"1970-01-01T00:00:00Z\")", ["source sentinel"], "System exception", null),
            ("startofyear-epoch-zero/literal-noopt/epoch", "@noopt (startofyear(\"1970-01-01T00:00:00Z\")) - d\"1970-01-01T00:00:00Z\"", ["source sentinel"], "-32400000000", null),
            ("startofyear-epoch-zero/line-noopt/serialized", "@noopt startofyear(line)", ["1970-01-01T00:00:00Z"], "System exception", null),
            ("startofyear-epoch-zero/line-noopt/epoch", "@noopt (startofyear(line)) - d\"1970-01-01T00:00:00Z\"", ["1970-01-01T00:00:00Z"], "-32400000000", null),
            ("startofyear-raw-year2000/literal-noopt/serialized", "@noopt startofyear((d\"1970-01-01T00:00:00Z\" + toint(\"946684800000000\")))", ["source sentinel"], "2000-01-01 00:00:00", null),
            ("startofyear-raw-year2000/literal-noopt/epoch", "@noopt (startofyear((d\"1970-01-01T00:00:00Z\" + toint(\"946684800000000\")))) - d\"1970-01-01T00:00:00Z\"", ["source sentinel"], "946652400000000", null),
            ("startofyear-none/literal-noopt/serialized", "@noopt startofyear(none)", ["source sentinel"], "none", null),
            ("startofyear-bool/literal-noopt/serialized", "@noopt startofyear(true)", ["source sentinel"], "none", null),
            ("startofyear-int/literal-noopt/serialized", "@noopt startofyear(toint(\"7\"))", ["source sentinel"], "none", null),
            ("startofyear-float/literal-noopt/serialized", "@noopt startofyear(1.25)", ["source sentinel"], "none", null),
            ("startofyear-array-empty/literal-noopt/serialized", "@noopt startofyear(array())", ["source sentinel"], "[]", null),
            ("startofyear-array-mixed/literal-noopt/serialized", "@noopt startofyear(array(\"2000-01-02T03:04:05Z\", none, toint(\"7\"), false, (d\"1970-01-01T00:00:00Z\" + toint(\"946684800000000\")), array(\"2000-01-03\")))", ["source sentinel"], "[2000-01-01 00:00:00, none, none, none, 2000-01-01 00:00:00, none]", null),
            ("startofyear-array-invalid-middle/literal-noopt/body-exception", "@noopt startofyear(array(\"2000-01-02T03:04:05Z\", \"not a date\", \"2000-01-03\"))", ["source sentinel"], null, "transform"),
            ("startofyear-array-nested/literal-noopt/serialized", "@noopt startofyear(array(array()))", ["source sentinel"], "[none]", null),
        };
        using (var observations = NewFile(directory, "timestamp-calendar-public-observations.ndjson"))
        foreach (var item in cases.Concat(cases.Where(item => item.Id.EndsWith("sunday/literal-noopt/serialized", StringComparison.Ordinal))
            .Select(item => (Id: item.Id + "/normal", Expression: item.Expression.Replace("@noopt ", "", StringComparison.Ordinal), item.Input, item.Expected, item.ErrorPhase))))
        {
            if (!nativeProfileQualified)
            {
                Row(observations, json => { json.WriteString("id", item.Id); json.WriteString("status", "unqualified-profile-not-executed");
                    json.WriteNull("passed"); json.WriteString("qualification", Qualification); });
                continue;
            }
            var rules = new[] { new LineTransformRule("@cs " + item.Expression, TablePaneMask.All) };
            var raw = item.Input.ToArray(); var before = raw.ToArray();
            var phase = "compile"; var keys = new[] { "previous-output-sentinel" }; Exception? failure = null;
            try
            {
                var plan = LineTransformPlan.Compile(rules, new LineTransformBudget(),
                    timestampEnvironment: LineTransformTimestampEnvironments.ForRules(rules));
                phase = "transform"; keys = plan.Transform(raw, TablePaneMask.Left, false); phase = "complete";
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or ArithmeticException)
            { failure = error; }
            var ok = raw.SequenceEqual(before) && (item.ErrorPhase is null
                ? failure is null && phase == "complete" && keys.SequenceEqual(Enumerable.Repeat(item.Expected!, raw.Length))
                : failure is not null && phase == item.ErrorPhase && keys.SequenceEqual(["previous-output-sentinel"]));
            Row(observations, json => { json.WriteString("id", item.Id); json.WriteString("expression", rules[0].Expression);
                json.WriteString("phase", phase); json.WriteString("expected", item.Expected); json.WriteString("expectedErrorPhase", item.ErrorPhase);
                json.WriteString("exceptionType", failure?.GetType().FullName); json.WriteString("exceptionMessage", failure?.Message);
                json.WriteStartArray("originalInput"); foreach (var line in before) json.WriteStringValue(line); json.WriteEndArray();
                json.WriteStartArray("actualKeys"); foreach (var key in keys) json.WriteStringValue(key); json.WriteEndArray();
                json.WriteBoolean("inputPreserved", raw.SequenceEqual(before)); json.WriteBoolean("passed", ok);
                json.WriteBoolean("serializerSystemString", failure is null && item.Expected == "System exception");
                json.WriteString("qualification", Qualification); json.WriteBoolean("nativeFullAstExecuted", false); });
            Verify("App host " + item.Id, ok, "timestamp-calendar-public-observations.ndjson");
        }
        RunEngineering(directory, Verify);
        var textRule = new LineTransformRule("@cs @noopt If(LineNumber = 2, todatestr(startofweek(Line)), Line)", TablePaneMask.All);
        var tableRule = new LineTransformRule("@cs @noopt If(LineNumber = 2, todatestr(startofmonth(Line)), Line)", TablePaneMask.All);
        var oldShown = LineTransformDialog.Shown; LineTransformDialog? shown = null;
        try
        {
            pane.SetLineTransforms([]); LineTransformDialog.Shown = dialog => shown = dialog;
            Click(Caption(pane, "行変換…")); Settle();
            var dialog = shown ?? throw new InvalidOperationException("日時関数dialogがありません。");
            dialog.Width = 720; dialog.Height = 550; Settle();
            foreach (var invalid in new[] { "", "startofweek()", "startofmonth(1, 2)", "startofyear()" })
            {
                dialog.Expression.Text = invalid; Click(Named(dialog, "LineTransformAdd"));
                Verify("dialog rejects " + Convert.ToHexString(Utf8.GetBytes(invalid)), dialog.Rules.Count == 0 && !string.IsNullOrEmpty(dialog.Error.Text));
            }
            dialog.Expression.Text = "@noopt todatestr(startofyear(\"2000-01-02\"))"; Click(Named(dialog, "LineTransformAdd"));
            Verify("dialog add functions", dialog.Rules.Count == 1);
            dialog.RuleList.SelectedIndex = 0; dialog.Expression.Text = textRule.Expression; Click(Named(dialog, "LineTransformChange"));
            Verify("dialog change dynamic Line", dialog.Rules.SequenceEqual([textRule]));
            dialog.Expression.Text = "startofyear()"; Click(Named(dialog, "LineTransformChange"));
            Verify("invalid change retains candidate", dialog.Rules.SequenceEqual([textRule]) && !string.IsNullOrEmpty(dialog.Error.Text));
            Settle();
            using (var bitmap = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("日時関数dialog PNGがありません。"))
            using (var png = NewFile(directory, "timestamp-calendar-dialog.png")) bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            using (var bounds = NewFile(directory, "timestamp-calendar-dialog-bounds.ndjson"))
            foreach (var control in new Control[] { dialog.Expression, dialog.Target, dialog.RuleList, Named(dialog, "LineTransformAdd"), dialog.Accept, dialog.Cancel })
            {
                control.BringIntoView(); Settle(); var point = control.TranslatePoint(default, dialog);
                var reachable = point is { } p && control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
                    && p.X >= -.01 && p.Y >= -.01 && p.X + control.Bounds.Width <= dialog.ClientSize.Width + .01 && p.Y + control.Bounds.Height <= dialog.ClientSize.Height + .01;
                Row(bounds, json => { json.WriteString("control", control.Name); json.WriteNumber("x", point?.X ?? -1); json.WriteNumber("y", point?.Y ?? -1);
                    json.WriteNumber("width", control.Bounds.Width); json.WriteNumber("height", control.Bounds.Height);
                    json.WriteNumber("clientWidth", dialog.ClientSize.Width); json.WriteNumber("clientHeight", dialog.ClientSize.Height); json.WriteBoolean("reachable", reachable); });
                Verify("dialog reachable " + control.Name, reachable, "timestamp-calendar-dialog-bounds.ndjson");
                using var frame = dialog.CaptureRenderedFrame() ?? throw new InvalidOperationException("dialog control PNG missing");
                using var frameFile = NewFile(directory, "dialog-control-" + control.Name + ".png");
                frame.Save(frameFile, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            Click(dialog.Accept); Click(dialog.Accept); Settle();
            Verify("duplicate apply adopts one rule", pane.CaptureProject().LineTransformRules.SequenceEqual([textRule]));
            shown = null; Click(Caption(pane, "行変換…")); Settle(); dialog = shown ?? throw new InvalidOperationException("日時関数dialog再開失敗です。");
            dialog.Expression.Text = "@noopt startofmonth(None)"; Click(Named(dialog, "LineTransformAdd")); Click(dialog.Cancel); Settle();
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
            screenshot("timestamp-calendar-" + mode.ToLowerInvariant() + ".png");
            var workspace = Path.Combine(directory, "timestamp-calendar-" + mode + "-workspace.json");
            pump(WorkspaceStore.SaveWorkspaceAsync(workspace, new ComparisonWorkspace { Entries = [pane.CaptureProject()], ActiveEntryIndex = 0 }));
            var restored = Wait(WorkspaceStore.LoadWorkspaceAsync(workspace)); pane.ApplyProject(restored.Entries.Single()); Compare();
            Verify(mode + " workspace rules roundtrip", pane.CaptureProject().LineTransformRules.SequenceEqual([rule]) && EqualKeys());
            var serialized = File.ReadAllText(workspace, Utf8);
            Verify(mode + " runtime host not persisted", !serialized.Contains("TimestampEnvironment", StringComparison.OrdinalIgnoreCase)
                && !serialized.Contains("CurrentTzdSeconds", StringComparison.OrdinalIgnoreCase) && !serialized.Contains("ProfileId", StringComparison.OrdinalIgnoreCase) && !serialized.Contains("CalendarEnvironment", StringComparison.OrdinalIgnoreCase));
            var htmlPath = Path.Combine(directory, "timestamp-calendar-" + mode + "-report.html"); pump(pane.SaveReportAsync(htmlPath));
            var html = File.ReadAllText(htmlPath, Utf8);
            Verify(mode + " report raw and functions", html.Contains(mode == "Text" ? "startofweek" : "startofmonth", StringComparison.OrdinalIgnoreCase) && html.Contains("todatestr", StringComparison.OrdinalIgnoreCase)
                && html.Contains(mode == "Text" ? "2000-01-02T03:04:05" : "2000-01-02T03:04:05", StringComparison.Ordinal)
                && html.Contains(mode == "Text" ? "2000-01-02" : "2000-01-09", StringComparison.Ordinal));

            var state = pane.CaptureAdoptionState(); var diff = pane.CurrentDiff;
            var adoptedTable = mode == "Table" ? Table() : null;
            pane.SetLineTransforms([new("@noopt startofmonth(\"not a date\")", TablePaneMask.All)]); Compare();
            Verify(mode + " invalid calendar keeps view and restores controls", Equals(state, pane.CaptureAdoptionState())
                && (mode == "Text" ? ReferenceEquals(diff, pane.CurrentDiff) : ReferenceEquals(adoptedTable, Table()))
                && pane.LeftEditor.Text == raw[0] && pane.RightEditor.Text == raw[2] && pane.CompareButton.IsEnabled);
            Verify(mode + " failure message presented", pane.GetVisualDescendants().OfType<TextBlock>()
                .Any(block => block.IsEffectivelyVisible && (block.Text ?? "").Contains("not a date", StringComparison.Ordinal)));
            Layout(mode.ToLowerInvariant() + "-error", pane);
            screenshot("timestamp-calendar-" + mode.ToLowerInvariant() + "-invalid-preserved.png");
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
                screenshot("timestamp-calendar-" + mode.ToLowerInvariant() + "-cancel-preserved.png");
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
        screenshot("timestamp-calendar-shared-settings.png");
        var oldWidth = window.Width; var oldHeight = window.Height;
        try
        {
            window.Width = 1280; window.Height = 850; Layout("normal", extra);
            for (var index = 0; index < 12; index++) window.AddSession();
            var manyTabs = window.ActivePane; manyTabs.ApplyProject(shared); pump(manyTabs.CompareProjectAsync()); Settle();
            window.Width = 850; window.Height = 550; Layout("minimum-many-tabs", manyTabs);
        }
        finally { window.Width = oldWidth; window.Height = oldHeight; Settle(); }

        using (var summary = NewFile(directory, "calendar-assertions.json"))
        using (var json = new Utf8JsonWriter(summary, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject(); json.WriteString("qualification", Qualification); json.WriteBoolean("nativeProfileQualified", nativeProfileQualified);
            json.WriteNumber("selectedNativeCases", cases.Length); json.WriteNumber("nativeCasesSkipped", nativeProfileQualified ? 0 : cases.Length + 3);
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
                screenshot("timestamp-calendar-" + name + "-" + label.Replace(' ', '-') + ".png");
            }
            toolbar.Offset = default; Settle();
        }
    }

    private static void RunEngineering(string directory, Action<string, bool, string> verify)
    {
        using var observations = NewFile(directory, "calendar-injected-engineering.ndjson");
        foreach (var function in new[] { "startofweek", "startofmonth", "startofyear" })
        foreach (var variant in new[] { "boundary-trace", "base-only", "cancel-locale", "cancel-offset", "cancel-calendar" })
        {
            using var cancellation = new CancellationTokenSource();
            var host = new CalendarProbe(cancellation, variant);
            ILineTransformTimestampEnvironment environment = variant == "base-only" ? new BaseOnlyProbe(host) : host;
            var expression = "@noopt " + function + "(Line) - d\"1970-01-01T00:00:00Z\"";
            var source = new[] { variant == "cancel-locale" ? "probe-locale-input" : variant.StartsWith("cancel-", StringComparison.Ordinal) ? "2000-01-02T03:04:05Z" : "1970-01-01T00:00:00Z" };
            var original = source.ToArray(); var output = new[] { "previous-output-sentinel" }; Exception? error = null;
            var phase = "compile";
            try
            {
                var plan = LineTransformPlan.Compile([new(expression, TablePaneMask.All)], new(),
                    cancellationToken: cancellation.Token, timestampEnvironment: environment);
                phase = "transform"; output = plan.Transform(source, TablePaneMask.Left, false, cancellationToken: cancellation.Token);
                phase = "complete";
            }
            catch (Exception caught) when (caught is ArgumentException or InvalidOperationException or OperationCanceledException)
            { error = caught; }
            var ok = source.SequenceEqual(original) && (error is null || output.SequenceEqual(["previous-output-sentinel"]));
            if (variant.StartsWith("cancel-", StringComparison.Ordinal)) ok &= error is OperationCanceledException && phase == "transform";
            else if (variant == "base-only") ok &= error is InvalidOperationException && phase == "transform" && host.CalendarCalls == 0;
            else if (function == "startofweek") ok &= error is InvalidOperationException && error.Message == "System exception"
                && phase == "transform" && host.CalendarCalls == 1 && host.OffsetInputs.SequenceEqual(new long[] { 0, -378_000_000_000L });
            else ok &= error is null && output.SequenceEqual(["-32400000000"]) && host.CalendarCalls == 1 && host.OffsetInputs.SequenceEqual(new long[] { 0 });
            Row(observations, json => { json.WriteString("id", function + "/" + variant); json.WriteString("scope", "injected engineering; not Native/OS qualification");
                json.WriteString("expression", expression); json.WriteString("phase", phase); json.WriteString("exceptionType", error?.GetType().FullName);
                json.WriteString("exceptionMessage", error?.Message); json.WriteNumber("calendarCalls", host.CalendarCalls);
                json.WriteStartArray("offsetInputs"); foreach (var value in host.OffsetInputs) json.WriteNumberValue(value); json.WriteEndArray();
                json.WriteStartArray("events"); foreach (var value in host.Events) json.WriteStringValue(value); json.WriteEndArray();
                json.WriteBoolean("inputPreserved", source.SequenceEqual(original)); json.WriteBoolean("passed", ok); });
            verify("engineering " + function + "/" + variant, ok, "calendar-injected-engineering.ndjson");
        }
    }

    private sealed class CalendarProbe(CancellationTokenSource cancellation, string variant) : ILineTransformTimestampCalendarEnvironment
    {
        public List<long> OffsetInputs { get; } = [];
        public List<string> Events { get; } = [];
        public int CalendarCalls { get; private set; }
        public string ProfileId => "injected-engineering-calendar";
        public int CurrentTzdSeconds => 32400;
        public bool TryParseWindowsLocale(string text, out long epochMicroseconds)
        {
            Events.Add("locale-parse"); epochMicroseconds = 946_738_800_000_000L;
            if (variant == "cancel-locale") cancellation.Cancel();
            return text == "probe-locale-input";
        }
        public bool TryGetSerializationOffset(long epochMicroseconds, out int seconds, out string failureText)
        {
            OffsetInputs.Add(epochMicroseconds); Events.Add("local-offset:" + epochMicroseconds);
            if (variant == "cancel-offset") cancellation.Cancel();
            seconds = 32400; failureText = "System exception"; return epochMicroseconds >= 0;
        }
        public int GetCalendarMidnightOffset(int year, int month, int day, long naiveEpochMicroseconds)
        {
            CalendarCalls++; Events.Add(FormattableString.Invariant($"calendar:{year}-{month}-{day}:{naiveEpochMicroseconds}"));
            if (variant == "cancel-calendar") cancellation.Cancel();
            return 32400;
        }
    }
    private sealed class BaseOnlyProbe(CalendarProbe inner) : ILineTransformTimestampEnvironment
    {
        public string ProfileId => inner.ProfileId;
        public int CurrentTzdSeconds => inner.CurrentTzdSeconds;
        public bool TryParseWindowsLocale(string text, out long epochMicroseconds) => inner.TryParseWindowsLocale(text, out epochMicroseconds);
        public bool TryGetSerializationOffset(long epochMicroseconds, out int seconds, out string failureText)
            => inner.TryGetSerializationOffset(epochMicroseconds, out seconds, out failureText);
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





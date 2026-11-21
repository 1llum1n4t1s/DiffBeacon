using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiffBeacon.Core;

namespace DiffBeacon.App;

internal static class HeadlessFolderDateStrChecks
{
    internal static void Run(string output, Action<string, bool, string> check, Action<Task> pump)
    {
        var work = Path.Combine(output, "folder-datestr"); Directory.CreateDirectory(work);
        var sequence = 0;
        var profiles = new[]
        {
            new Profile("jst-next-day", new DateTime(2026, 10, 22, 20, 0, 0, DateTimeKind.Utc), 9 * 3600, 9 * 3600, "2026-10-23"),
            new Profile("current-not-target-dst", new DateTime(2026, 1, 15, 7, 30, 0, DateTimeKind.Utc), -7 * 3600, -8 * 3600, "2026-01-15")
        };
        foreach (var profile in profiles)
        foreach (var count in new[] { 2, 3 })
        {
            var group = Path.Combine(work, profile.Name + "-" + count); Directory.CreateDirectory(group);
            var roots = Enumerable.Range(0, count).Select(side => Path.Combine(group, "side-" + side)).ToArray();
            var rows = new Dictionary<string, bool[]>(StringComparer.Ordinal)
            {
                ["shared.bin"] = Enumerable.Repeat(true, count).ToArray(),
                ["shared.dir"] = Enumerable.Repeat(true, count).ToArray()
            };
            for (var side = 0; side < count; side++)
            {
                rows.Add("only-" + side + ".bin", Enumerable.Range(0, count).Select(index => index == side).ToArray());
                rows.Add("only-" + side + ".dir", Enumerable.Range(0, count).Select(index => index == side).ToArray());
            }
            var originals = new List<Original>();
            for (var side = 0; side < count; side++)
            {
                Directory.CreateDirectory(roots[side]);
                foreach (var row in rows)
                {
                    if (!row.Value[side]) continue;
                    var path = Path.Combine(roots[side], row.Key);
                    var directory = row.Key.EndsWith(".dir", StringComparison.Ordinal);
                    byte[]? bytes = null;
                    if (directory) Directory.CreateDirectory(path);
                    else { bytes = Encoding.UTF8.GetBytes("DateStr original " + profile.Name + "/" + count + "/" + side + "/" + row.Key + "\n"); File.WriteAllBytes(path, bytes); }
                    if (directory) Directory.SetLastWriteTimeUtc(path, profile.Utc);
                    else File.SetLastWriteTimeUtc(path, profile.Utc);
                    originals.Add(new(path, directory, bytes, File.GetLastWriteTimeUtc(path), File.GetAttributes(path)));
                }
            }
            using (var stream = File.Create(Path.Combine(group, "input-originals.json")))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject(); writer.WriteString("sourceContract", "Nodes1068-1073: current fixed tzd, numeric %Y-%m-%d, absent None");
                writer.WriteString("profile", profile.Name); writer.WriteString("mtimeUtc", profile.Utc);
                writer.WriteNumber("currentTzdSeconds", profile.CurrentOffset); writer.WriteNumber("targetDateOffsetSeconds", profile.TargetOffset);
                writer.WriteString("sourceExpectedDate", profile.Expected);
                writer.WriteStartArray("inputs");
                foreach (var original in originals)
                {
                    writer.WriteStartObject(); writer.WriteString("path", original.Path); writer.WriteBoolean("directory", original.Directory);
                    writer.WriteString("mtimeUtc", original.Modified); writer.WriteNumber("attributes", (int)original.Attributes);
                    if (original.Bytes is { } bytes) { writer.WriteString("bytesHex", Convert.ToHexString(bytes)); writer.WriteString("sha256", Convert.ToHexString(SHA256.HashData(bytes))); }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            var options = new DirectoryComparisonOptions { Recursive = false, ShowFiltered = true, Mode = DirectoryComparisonMode.TimeAndSize };
            DirectoryComparisonResult Compare(FileFilter? filter)
            {
                var selected = options with { FileFilter = filter };
                var task = count == 2 ? FolderComparisons.CompareAsync(roots[0], roots[1], selected, CancellationToken.None)
                    : FolderComparisons.CompareAsync(roots[0], roots[1], roots[2], selected, CancellationToken.None);
                pump(task); return task.GetAwaiter().GetResult();
            }
            var originalModel = Compare(null);
            bool PresenceMatches(DirectoryComparisonResult model) => model.AllEntries.Count == rows.Count
                && model.AllEntries.All(entry => rows.TryGetValue(entry.RelativePath, out var expected)
                    && States(entry, count).Select(state => state is not null).SequenceEqual(expected));
            Record(group, "baseline", "", PresenceMatches(originalModel)
                && originalModel.AllEntries.All(entry => entry.Error is null
                    && States(entry, count).Where(state => state is not null).All(state => state!.LastWriteTimeUtc == profile.Utc)),
                originalModel, rows.Keys.ToArray(), null, default, default);
            var prefixes = count == 2 ? new[] { (Name: "Left", Side: 0), (Name: "Middle", Side: 1), (Name: "Right", Side: 1) }
                : new[] { (Name: "Left", Side: 0), (Name: "Middle", Side: 1), (Name: "Right", Side: 2) };
            foreach (var directive in new[] { "@opt", "@noopt" })
            {
                foreach (var prefix in prefixes)
                {
                    Observe(directive, prefix.Name + " present", directive + " " + prefix.Name + "DateStr == '" + profile.Expected + "'",
                        rows.Where(row => row.Value[prefix.Side]).Select(row => row.Key).ToArray());
                    Observe(directive, prefix.Name + " absent", directive + " " + prefix.Name + "DateStr == None",
                        rows.Where(row => !row.Value[prefix.Side]).Select(row => row.Key).ToArray());
                }
                for (var pattern = -1; pattern < count; pattern++)
                {
                    var expected = Enumerable.Range(0, count).Select(side => pattern == -1 || pattern == side ? "'" + profile.Expected + "'" : "None");
                    var expression = directive + " AllEqual(Array(DateStr,Array(" + string.Join(',', expected) + ")))";
                    Observe(directive, "array pattern " + pattern, expression,
                        rows.Where(row => pattern == -1 ? row.Value.All(value => value)
                            : row.Value.Select((value, side) => value == (side == pattern)).All(value => value)).Select(row => row.Key).ToArray());
                }
            }
            void Observe(string directive, string id, string expression, string[] expected)
            {
                var environment = new Environment(profile);
                DirectoryComparisonResult? model = null; Exception? failure = null; FileFilter? filter = null;
                Counts before = default;
                try
                {
                    filter = FileFilter.ParseExpression(expression, timestampEnvironment: environment);
                    // Now/Todayの解析時準備はDateStrのgetter検証から分離する。
                    before = environment.Counts; model = Compare(filter);
                }
                catch (Exception error) { failure = error; }
                var after = environment.Counts;
                var visible = model?.AllEntries.Where(entry => !entry.IsFiltered).Select(entry => entry.RelativePath).Order(StringComparer.Ordinal).ToArray() ?? [];
                var passed = failure is null && filter is { UsesProperties: false } && filter.PropertyNames.Count == 0
                    && model is not null && PresenceMatches(model)
                    && model.AllEntries.All(entry => entry.Error is null && States(entry, count).Zip(States(originalModel.AllEntries.Single(old => old.RelativePath == entry.RelativePath), count))
                        .All(pair => Same(pair.First, pair.Second)))
                    && visible.SequenceEqual(expected.Order(StringComparer.Ordinal))
                    && after.Current > before.Current && after.Target == before.Target && after.Calendar == before.Calendar;
                Record(group, directive + " " + id, expression, passed, model, expected, failure, before, after);
            }
            var retained = originals.All(original => File.GetAttributes(original.Path) == original.Attributes
                && File.GetLastWriteTimeUtc(original.Path) == original.Modified
                && (original.Directory ? Directory.Exists(original.Path) : File.ReadAllBytes(original.Path).AsSpan().SequenceEqual(original.Bytes!)))
                && roots.Sum(root => Directory.EnumerateFiles(root).Count() + Directory.EnumerateDirectories(root).Count()) == originals.Count;
            Record(group, "input preservation", "", retained, null, [], null, default, default);
        }
        foreach (var directory in new[] { false, true })
        foreach (var directive in new[] { "@opt", "@noopt" })
        {
            var utc = new DateTime(2026, 1, 15, 7, 30, 0, DateTimeKind.Utc);
            var expected = utc.Add(TimeZoneInfo.Local.GetUtcOffset(DateTime.Now)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var expression = directive + " DateStr == '" + expected + "'";
            var filter = FileFilter.ParseExpression(expression);
            Record(work, "standalone null environment " + directory, expression, filter.Matches("synthetic", directory, 1, utc), null, [], null, default, default);
        }

        // fake Matchesの符号付きepoch境界。実ファイルmtimeやFILETIMEのpre-1970互換は検証しない。
        foreach (var directive in new[] { "@opt", "@noopt" })
        foreach (var upper in new[] { false, true })
        {
            var utc = upper ? new DateTime(9999, 12, 31, 23, 30, 0, DateTimeKind.Utc)
                : new DateTime(1, 1, 1, 0, 30, 0, DateTimeKind.Utc);
            var offset = upper ? 9 * 3600 : -7 * 3600;
            var environment = new BoundaryEnvironment(offset);
            var expression = directive + " DateStr == '0000-12-31'";
            bool? matched = null; Exception? failure = null;
            try
            {
                var filter = FileFilter.ParseExpression(expression, timestampEnvironment: environment);
                matched = filter.Matches("synthetic-signed-year-boundary", false, 1, utc);
            }
            catch (Exception error) { failure = error; }
            var passed = (upper ? failure?.GetType() == typeof(ArgumentException) && matched is null
                : failure is null && matched == true)
                && environment.CurrentCalls == 1 && environment.TargetCalls == 0;
            var number = ++sequence;
            using var stream = File.Create(Path.Combine(work, "case-" + number.ToString("D3", CultureInfo.InvariantCulture) + ".json"));
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject(); writer.WriteString("id", directive + (upper ? " year9999-plus09" : " year1-minus07"));
            writer.WriteString("qualification", "standaloneMatches signed epoch extension; not native realmtime or pre-1970 FileTimestamp compatibility");
            writer.WriteString("expression", expression); writer.WriteString("mtimeUtc", utc); writer.WriteNumber("currentTzdSeconds", offset);
            writer.WriteBoolean("nativeExecuted", false); writer.WriteBoolean("passed", passed);
            writer.WriteString("expectedDate", upper ? null : "0000-12-31");
            writer.WriteString("expectedExceptionType", upper ? typeof(ArgumentException).FullName : null);
            writer.WriteString("actualExceptionType", failure?.GetType().FullName); writer.WriteString("failure", failure?.ToString());
            if (matched is { } result) writer.WriteBoolean("actualMatched", result); else writer.WriteNull("actualMatched");
            writer.WriteNumber("currentOffsetCalls", environment.CurrentCalls); writer.WriteNumber("targetOffsetCalls", environment.TargetCalls);
            writer.WriteEndObject();
            check("folder DateStr " + number + " signed year boundary", passed, expression + " | " + (failure?.GetType().FullName ?? matched?.ToString()));
        }

        // fake Matchesのepoch直前100ns。実mtimeのFILETIME変換互換とは分離する。
        foreach (var directive in new[] { "@opt", "@noopt" })
        {
            var utc = DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc).AddTicks(-1);
            var environment = new BoundaryEnvironment(0);
            var expression = directive + " DateStr == '1969-12-31'";
            bool? matched = null; Exception? failure = null;
            try
            {
                var filter = FileFilter.ParseExpression(expression, timestampEnvironment: environment);
                matched = filter.Matches("synthetic-before-unix-epoch", false, 1, utc);
            }
            catch (Exception error) { failure = error; }
            var passed = failure is null && matched == true && environment.CurrentCalls == 1 && environment.TargetCalls == 0;
            var number = ++sequence;
            using var stream = File.Create(Path.Combine(work, "case-" + number.ToString("D3", CultureInfo.InvariantCulture) + ".json"));
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject(); writer.WriteString("id", directive + " epoch-minus100ns");
            writer.WriteString("qualification", "standaloneMatches signed epoch extension; not native realmtime or pre-1970 FileTimestamp compatibility");
            writer.WriteString("expression", expression); writer.WriteString("mtimeUtc", utc); writer.WriteNumber("currentTzdSeconds", 0);
            writer.WriteBoolean("nativeExecuted", false); writer.WriteBoolean("passed", passed); writer.WriteString("expectedDate", "1969-12-31");
            writer.WriteString("actualExceptionType", failure?.GetType().FullName); writer.WriteString("failure", failure?.ToString());
            if (matched is { } result) writer.WriteBoolean("actualMatched", result); else writer.WriteNull("actualMatched");
            writer.WriteNumber("currentOffsetCalls", environment.CurrentCalls); writer.WriteNumber("targetOffsetCalls", environment.TargetCalls);
            writer.WriteEndObject();
            check("folder DateStr " + number + " epoch minus100ns", passed, expression + " | " + (failure?.GetType().FullName ?? matched?.ToString()));
        }

        void Record(string group, string id, string expression, bool passed, DirectoryComparisonResult? model,
            string[] expected, Exception? failure, Counts before, Counts after)
        {
            var number = ++sequence;
            using var stream = File.Create(Path.Combine(group, "case-" + number.ToString("D3", CultureInfo.InvariantCulture) + ".json"));
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject(); writer.WriteString("id", id); writer.WriteString("expression", expression); writer.WriteBoolean("passed", passed);
            writer.WriteString("qualification", model is null ? "standalone extension or preservation; not native execution" : "actual App FolderComparisons; source-derived DateStr fixed current-offset contract; native boundary execution pending");
            writer.WriteBoolean("nativeExecuted", false); writer.WriteString("failure", failure?.ToString());
            writer.WriteStartArray("expectedVisible"); foreach (var name in expected.Order(StringComparer.Ordinal)) writer.WriteStringValue(name); writer.WriteEndArray();
            writer.WriteStartArray("actualVisible");
            if (model is not null) foreach (var entry in model.AllEntries.Where(entry => !entry.IsFiltered).OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)) writer.WriteStringValue(entry.RelativePath);
            writer.WriteEndArray();
            writer.WriteStartObject("environmentCallsAfterParse"); writer.WriteNumber("currentBefore", before.Current); writer.WriteNumber("currentAfter", after.Current);
            writer.WriteNumber("targetBefore", before.Target); writer.WriteNumber("targetAfter", after.Target); writer.WriteNumber("calendarBefore", before.Calendar); writer.WriteNumber("calendarAfter", after.Calendar); writer.WriteEndObject();
            if (model is not null) { writer.WriteStartObject("model"); FolderCommands.WriteModel(writer, model); writer.WriteEndObject(); }
            writer.WriteEndObject();
            check("folder DateStr " + number + " " + id, passed, expression + (failure is null ? "" : " | " + failure.Message));
        }
    }

    private sealed class BoundaryEnvironment(int offset) : ILineTransformTimestampEnvironment
    {
        internal int CurrentCalls { get; private set; }
        internal int TargetCalls { get; private set; }
        public string ProfileId => "DateStr standalone signed year boundary";
        public int CurrentTzdSeconds { get { CurrentCalls++; return offset; } }
        public bool TryParseWindowsLocale(string text, out long epochMicroseconds) { epochMicroseconds = 0; return false; }
        public bool TryGetSerializationOffset(long epochMicroseconds, out int seconds, out string failureText)
        { TargetCalls++; seconds = 0; failureText = "target-date route forbidden"; return false; }
    }
    private sealed record Profile(string Name, DateTime Utc, int CurrentOffset, int TargetOffset, string Expected);
    private sealed record Original(string Path, bool Directory, byte[]? Bytes, DateTime Modified, FileAttributes Attributes);
    private readonly record struct Counts(int Current, int Target, int Calendar);
    private sealed class Environment(Profile profile) : ILineTransformTimestampClockEnvironment
    {
        private int current; private int target; private int calendar;
        internal Counts Counts => new(current, target, calendar);
        public string ProfileId => "DateStr fixed current offset " + profile.Name;
        public int CurrentTzdSeconds { get { current++; return profile.CurrentOffset; } }
        public long CaptureNowEpochMicroseconds() => (profile.Utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        public bool TryParseWindowsLocale(string text, out long epochMicroseconds) { epochMicroseconds = 0; return false; }
        public bool TryGetSerializationOffset(long epochMicroseconds, out int seconds, out string failureText)
        { target++; seconds = profile.TargetOffset; failureText = ""; return true; }
        public int GetCalendarMidnightOffset(int year, int month, int day, long naiveEpochMicroseconds)
        { calendar++; return profile.TargetOffset; }
    }
    private static DirectorySideSnapshot?[] States(DirectoryEntry entry, int count)
        => count == 2 ? [entry.LeftState, entry.RightState] : [entry.LeftState, entry.MiddleState, entry.RightState];
    private static bool Same(DirectorySideSnapshot? left, DirectorySideSnapshot? right)
        => left is null ? right is null : right is not null && left.Path == right.Path && left.Kind == right.Kind
            && left.Size == right.Size && left.LastWriteTimeUtc == right.LastWriteTimeUtc && left.Attributes == right.Attributes
            && left.ScanState == right.ScanState && left.LinkTarget == right.LinkTarget && left.Error == right.Error;
}

using System.Text;
using System.Text.Json;

namespace DiffBeacon.App;

// 実画像を入力した原本コピー核の診断。GUI接続と元形式保存は別の移植単位。
internal static class ImageCopyCommands
{
    private sealed record Action(string Kind, int Source, int Destination, int Index, string? Path);
    internal static async Task<int> RunAsync(string[] args)
    {
        var paths = new List<string>(); var option = 1;
        while (option < args.Length && !args[option].StartsWith("--", StringComparison.Ordinal))
            paths.Add(ImagePngStore.ValidateLocal(args[option++]));
        if (paths.Count is not (2 or 3)) throw new ArgumentException("画像コピーは二者または三者の入力が必要です。");
        string? scriptPath = null; var hashesOnly = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (option < args.Length)
        {
            var name = args[option++];
            if (!seen.Add(name)) throw new ArgumentException("画像コピーのオプションが重複しています。");
            if (name == "--hashes-only") { hashesOnly = true; continue; }
            if (name != "--script" || option == args.Length) throw new ArgumentException("画像コピーのオプションまたは値が不正です。");
            scriptPath = ImagePngStore.ValidateLocal(args[option++]);
        }
        if (scriptPath is null) throw new ArgumentException("画像コピーには --script JSON が必要です。");
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var token = cancel.Token;
            using var script = await ReadScriptAsync(scriptPath, token);
            var root = script.RootElement;
            ValidateObject(root, ["blockSize", "threshold", "readOnly", "actions"]);
            var blockSize = root.TryGetProperty("blockSize", out var block) ? Integer(block) : 8;
            if (blockSize is < 1 or > 256) throw new ArgumentException("ブロックサイズは1..256です。");
            var threshold = root.TryGetProperty("threshold", out var thresholdJson) ? thresholdJson.GetDouble() : 0;
            ImageComparisonEngine.ValidateThreshold(threshold);
            var readOnly = new bool[paths.Count];
            if (root.TryGetProperty("readOnly", out var flags))
            {
                if (flags.ValueKind != JsonValueKind.Array || flags.GetArrayLength() != paths.Count)
                    throw new ArgumentException("readOnlyは入力数と同じ長さのbool配列です。");
                for (var i = 0; i < flags.GetArrayLength(); i++) readOnly[i] = flags[i].GetBoolean();
            }
            if (!root.TryGetProperty("actions", out var actionJson) || actionJson.ValueKind != JsonValueKind.Array || actionJson.GetArrayLength() > 64)
                throw new ArgumentException("actionsは64操作までの配列です。");
            var protectedPaths = paths.Append(scriptPath).ToArray();
            var actions = new List<Action>();
            foreach (var value in actionJson.EnumerateArray())
            {
                ValidateObject(value, ["kind", "src", "dst", "index", "path"]);
                if (!value.TryGetProperty("kind", out var kindJson) || kindJson.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("画像操作kindが必要です。");
                var kind = kindJson.GetString()!;
                if (kind is not ("copy" or "all" or "auto" or "undo" or "redo" or "save" or "set-savepoint" or "export"))
                    throw new ArgumentException("未知の画像操作kindです。");
                int Read(string field, bool required)
                {
                    if (value.TryGetProperty(field, out var number)) return Integer(number);
                    if (required) throw new ArgumentException($"画像操作に {field} が必要です。");
                    return 0;
                }
                var source = Read("src", kind is "copy" or "all");
                var destination = Read("dst", kind is "copy" or "all" or "auto" or "save" or "set-savepoint" or "export");
                var index = Read("index", kind is "copy" or "set-savepoint");
                string? output = null;
                if (kind == "export")
                {
                    if (destination < 0 || destination >= paths.Count || readOnly[destination])
                        throw new InvalidOperationException("保存する画像paneが不正または読取り専用です。");
                    if (!value.TryGetProperty("path", out var pathJson) || pathJson.ValueKind != JsonValueKind.String)
                        throw new ArgumentException("画像exportに保存先pathが必要です。");
                    output = ImagePngStore.ValidateTarget(pathJson.GetString()!, protectedPaths);
                }
                else if (value.TryGetProperty("path", out _)) throw new ArgumentException("pathはexportだけで使用できます。");
                actions.Add(new(kind, source, destination, index, output));
            }
            var snapshots = new List<ImageComparisonEngine.Snapshot>();
            foreach (var path in paths)
            {
                var snapshot = await ImageComparisonEngine.OpenAsync(path, token);
                if (snapshot.FrameCount != 1) throw new InvalidOperationException("画像編集は単一フレームだけです。多ページ画像の編集は未対応です。");
                snapshots.Add(snapshot);
            }
            ImageComparisonEngine.ValidateComparison(snapshots, snapshots.Select(_ => 1).ToArray(), threshold);
            // 全操作・全stateの上限検査を完了するまでexportを公開しない。
            var exports = new List<(string Path, ImageComparisonEngine.DecodedFrame Frame)>();
            using var content = new BoundedJsonStream();
            await Task.Run(() =>
            {
                var frames = snapshots.Select(snapshot => snapshot.Decode(1, token)).ToArray();
                var session = new ImageEditSession(frames, readOnly, blockSize, threshold, token);
                using var writer = new Utf8JsonWriter(content);
                writer.WriteStartObject(); writer.WriteStartArray("states"); WriteState(writer, session, hashesOnly, token);
                long exportBytes = 0;
                foreach (var action in actions)
                {
                    token.ThrowIfCancellationRequested();
                    var result = -1;
                    switch (action.Kind)
                    {
                        case "copy": session.Copy(action.Index, action.Source, action.Destination, token); break;
                        case "all": session.CopyAll(action.Source, action.Destination, token); break;
                        case "auto": result = session.AutoMerge(action.Destination, token); break;
                        case "undo": result = session.Undo(token) ? 1 : 0; break;
                        case "redo": result = session.Redo(token) ? 1 : 0; break;
                        case "save": session.MarkSaved(action.Destination); break;
                        case "set-savepoint": session.SetSavePoint(action.Destination, action.Index); break;
                        case "export":
                            var frame = session.CaptureFrame(action.Destination);
                            exportBytes = checked(exportBytes + frame.Pixels.LongLength);
                            if (exportBytes > ImageEditSession.MaximumHistoryBytes) throw new InvalidOperationException("保存待ち画像が256 MiBを超えます。");
                            exports.Add((action.Path!, frame)); session.MarkSaved(action.Destination); result = 1; break;
                    }
                    writer.WriteStartObject(); writer.WriteNumber("actionResult", result); writer.WritePropertyName("state");
                    WriteState(writer, session, hashesOnly, token); writer.WriteEndObject();
                    writer.Flush();
                }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
            }, token);
            token.ThrowIfCancellationRequested();
            foreach (var export in exports) await ImagePngStore.SaveAsync(export.Path, export.Frame, protectedPaths, token);
            token.ThrowIfCancellationRequested();
            Console.WriteLine(Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length)));
            return 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static async Task<JsonDocument> ReadScriptAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous);
        if (file.Length > 1024 * 1024) throw new InvalidOperationException("画像操作scriptは1 MiBまでです。");
        using var bytes = new MemoryStream(); var buffer = new byte[65_536];
        int length;
        while ((length = await file.ReadAsync(buffer, token)) != 0)
        {
            if (bytes.Length + length > 1024 * 1024) throw new InvalidOperationException("画像操作scriptは1 MiBまでです。");
            bytes.Write(buffer.AsSpan(0, length));
        }
        var value = bytes.ToArray();
        // PowerShell等が書いたUTF-8 BOMを受理する。
        var offset = value.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return JsonDocument.Parse(value.AsMemory(offset));
    }
    private static int Integer(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw new ArgumentException("画像操作の整数値が不正です。");
    private static void ValidateObject(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("画像操作scriptにobjectが必要です。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new ArgumentException("画像操作scriptに未知または重複したpropertyがあります。");
    }

    private static void WriteState(Utf8JsonWriter writer, ImageEditSession session, bool hashesOnly, CancellationToken token)
    {
        writer.WriteStartObject(); writer.WriteStartArray("frames");
        foreach (var frame in session.CaptureFrames())
        {
            token.ThrowIfCancellationRequested(); writer.WriteStartObject();
            writer.WriteNumber("width", frame.Width); writer.WriteNumber("height", frame.Height);
            if (!hashesOnly)
            {
                var base64Bytes = checked((frame.Pixels.LongLength + 2) / 3 * 4);
                if (base64Bytes + writer.BytesCommitted + writer.BytesPending + 256 > ProjectReport.MaximumBytes)
                    throw new InvalidOperationException("画像編集JSONは32 MiBまでです。");
                writer.WriteBase64String("bgraBase64", frame.Pixels);
            }
            writer.WriteString("sha256", ImageComparisonEngine.PixelHash(frame.Pixels, token)); writer.WriteEndObject();
        }
        writer.WriteEndArray(); var result = session.Regions;
        if ((long)result.Columns * result.Rows > 262_144) throw new InvalidOperationException("画像編集の診断JSONは262,144ブロックまでです。");
        writer.WriteStartArray("regionIds");
        for (var y = 0; y < result.Rows; y++)
        {
            token.ThrowIfCancellationRequested(); writer.WriteStartArray();
            for (var x = 0; x < result.Columns; x++) writer.WriteNumberValue(result.RegionIds[y * result.Columns + x]);
            writer.WriteEndArray();
        }
        writer.WriteEndArray(); writer.WriteNumber("differenceCount", result.Regions.Count);
        writer.WriteStartArray("regions");
        foreach (var region in result.Regions)
        {
            token.ThrowIfCancellationRequested(); writer.WriteStartObject();
            writer.WriteNumber("id", region.Id); writer.WriteNumber("op", region.Op);
            writer.WriteNumber("left", region.Left); writer.WriteNumber("top", region.Top);
            writer.WriteNumber("right", region.Right); writer.WriteNumber("bottom", region.Bottom); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteNumber("conflictCount", result.ConflictCount);
        writer.WriteStartObject("history"); writer.WriteNumber("index", session.HistoryIndex); writer.WriteNumber("count", session.HistoryCount);
        writer.WriteBoolean("undoable", session.CanUndo); writer.WriteBoolean("redoable", session.CanRedo); writer.WriteStartArray("panes");
        for (var i = 0; i < session.PaneCount; i++)
        {
            writer.WriteStartObject(); writer.WriteBoolean("modified", session.IsModified(i));
            writer.WriteNumber("modcount", session.ModCount(i)); writer.WriteNumber("savepoint", session.SavePoint(i)); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject();
    }

    private sealed class BoundedJsonStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Ensure(count); base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Ensure(buffer.Length);
            var bytes = buffer.ToArray(); base.Write(bytes, 0, bytes.Length);
        }
        private void Ensure(int count)
        {
            if (count > ProjectReport.MaximumBytes - Length) throw new InvalidOperationException("画像編集JSONは32 MiBまでです。");
        }
    }
}

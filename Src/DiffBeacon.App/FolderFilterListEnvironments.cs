using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>一回のフォルダー比較だけが所有する、置換リストの読込みと依存スナップショット。</summary>
internal sealed class FolderFilterListState(ILineTransformLogEnvironment? logger,
    Action<string, int>? readProgress = null) : IFileFilterListEnvironment
{
    private const int MaximumFileBytes = 1_048_576;
    private const int MaximumDependencies = 16_384;
    private readonly object gate = new();
    private readonly Dictionary<string, Stamp?> dependencies = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private sealed record Stamp(string Kind, string Sha256, long Length, long WriteTicks, int Attributes);
    private sealed record Snapshot(Stamp Stamp, byte[]? Bytes);
    internal IReadOnlyList<string> Paths { get { lock (gate) return dependencies.Keys.Order(StringComparer.Ordinal).ToArray(); } }

    public string ReadList(string path, FileFilterListReadContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        { Failed(path); return ""; }
        lock (gate)
        {
            if (!dependencies.ContainsKey(full))
            {
                if (dependencies.Count >= MaximumDependencies) throw new InvalidOperationException("フォルダーフィルターの置換リスト依存数が上限を超えています。");
                dependencies.Add(full, null);
            }
        }
        var snapshot = ReadSnapshot(full, context.CancellationToken, context.Charge);
        lock (gate)
        {
            if (dependencies[full] is { } previous && previous != snapshot.Stamp)
                throw Changed();
            dependencies[full] = snapshot.Stamp;
        }
        if (snapshot.Bytes is null) { Failed(path); return ""; }
        var bytes = snapshot.Bytes.AsSpan();
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException error) { throw new ArgumentException("置換リストは有効なUTF-8にしてください。", error); }
    }

    internal void RequireUnchanged(CancellationToken token = default)
    {
        KeyValuePair<string, Stamp?>[] captured;
        lock (gate) captured = dependencies.ToArray();
        foreach (var entry in captured)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Value is null || ReadSnapshot(entry.Key, token).Stamp != entry.Value) throw Changed();
        }
    }

    internal string CurrentStamp(CancellationToken token = default)
    {
        RequireUnchanged(token);
        var text = new StringBuilder();
        lock (gate)
        {
            foreach (var entry in dependencies.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var stamp = entry.Value ?? throw Changed();
                text.Append(entry.Key.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(entry.Key).Append('|')
                    .Append(stamp.Kind).Append('|').Append(stamp.Sha256).Append('|').Append(stamp.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(stamp.WriteTicks.ToString(CultureInfo.InvariantCulture)).Append('|').Append(stamp.Attributes.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private Snapshot ReadSnapshot(string full, CancellationToken token, Action<long>? charge = null)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            ArchiveActions.ValidatePath(full);
            if (OperatingSystem.IsMacOS()) FolderCopyMacFileKind.Require(full, DirectoryEntryKind.File);
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
            if (stream.Length > MaximumFileBytes) throw new InvalidOperationException("置換リストは1 MiB以下にしてください。");
            charge?.Invoke(stream.Length);
            var bytes = new byte[checked((int)stream.Length)];
            var position = 0;
            while (position < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(bytes.AsSpan(position, Math.Min(8192, bytes.Length - position)));
                if (read == 0) throw new EndOfStreamException("置換リストが読込み中に短くなりました。");
                position += read;
                if (charge is not null) readProgress?.Invoke(full, position);
                token.ThrowIfCancellationRequested();
            }
            if (stream.ReadByte() != -1) throw new IOException("置換リストが読込み中に大きくなりました。");
            ArchiveActions.ValidatePath(full);
            token.ThrowIfCancellationRequested();
            return new(new("file", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length,
                File.GetLastWriteTimeUtc(full).Ticks, (int)File.GetAttributes(full)), bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 不在・アクセス拒否・リンクも依存集合に残し、次の検証で状態遷移を検出する。
            token.ThrowIfCancellationRequested();
            return new(new("io:" + error.GetType().Name, "", 0, 0, 0), null);
        }
    }

    private void Failed(string path) => logger?.Write(LineTransformLogLevel.Error, "Failed to open regex replace list file: " + path);
    private static IOException Changed() => new("比較中に置換リストが変わりました。再比較してください。");
}

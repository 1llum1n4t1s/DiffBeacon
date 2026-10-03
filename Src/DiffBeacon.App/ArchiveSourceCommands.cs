using System.Text.Json;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static class ArchiveSourceCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var required = args[0] switch { "--archive-source-list" => 2, "--archive-source-entry" => 4, _ => throw new ArgumentException("不明な格納階層コマンドです。") };
        var passwordInput = args.Length == required + 1 && args[^1] == "--password-stdin";
        if (args.Length != required && !passwordInput) throw new ArgumentException("格納階層コマンドの引数が不正です。");
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, change) => { change.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        string?[]? passwords = null;
        try
        {
            var token = cancel.Token;
            var descriptor = ArchiveActions.ValidatePath(args[1]);
            var (source, limits) = await LoadAsync(descriptor, token);
            var service = new ManagedArchive(limits);
            passwords = new string?[source.EntryChain.Count + 1];
            if (passwordInput)
            {
                if (!Console.IsInputRedirected) throw new ArgumentException("パスワードはリダイレクトした標準入力から送ってください。");
                for (var index = 0; index < passwords.Length; index++)
                    passwords[index] = await ArchiveCommands.ReadPasswordAsync(token);
            }
            if (args[0] == "--archive-source-entry")
            {
                await ArchiveActions.ExportSourceAsync(source, args[2], args[3], descriptor, limits, passwords, token);
                CommandLine.WriteJson(writer => writer.WriteString("output", Path.GetFullPath(args[3])));
                return 0;
            }
            var result = await Task.Run(() => service.ResolveManifest(source, passwords, token), token);
            CommandLine.WriteJson(writer =>
            {
                writer.WriteStartObject("source"); writer.WriteString("rootPath", result.Source.RootPath);
                writer.WriteString("rootSha256", result.Source.RootSha256); writer.WriteStartArray("entryChain");
                foreach (var entry in result.Source.EntryChain) writer.WriteStringValue(entry);
                writer.WriteEndArray(); writer.WriteEndObject();
                writer.WriteString("format", result.Manifest.Format); writer.WriteStartArray("entries");
                foreach (var entry in result.Manifest.Entries)
                {
                    writer.WriteStartObject(); writer.WriteString("path", entry.Path); writer.WriteBoolean("directory", entry.IsDirectory);
                    writer.WriteNumber("size", entry.Size); writer.WriteString("sha256", entry.Sha256); writer.WriteBoolean("encrypted", entry.IsEncrypted);
                    if (entry.LastModifiedTime is { } time) { writer.WriteString("modifiedTime", time); writer.WriteString("timeKind", time.Kind.ToString()); }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            });
            return 0;
        }
        finally { Console.CancelKeyPress -= handler; if (passwords is not null) Array.Clear(passwords); }
    }

    private static async Task<(ArchiveSource Source, ManagedArchiveLimits Limits)> LoadAsync(string descriptor, CancellationToken token)
    {
        using var file = new FileStream(descriptor, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 1024 * 1024) throw new InvalidDataException("格納階層descriptorは1 MiBまでです。");
        var bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, token);
        token.ThrowIfCancellationRequested();
        if (file.ReadByte() >= 0) throw new IOException("読込み中にdescriptorのサイズが変わりました。");
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        CheckProperties(root, ["rootPath", "entryChain", "rootSha256", "limits"]);
        var path = root.GetProperty("rootPath").GetString();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var chain = new List<string>();
        if (root.TryGetProperty("entryChain", out var entries))
        {
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 4096)
                throw new InvalidDataException("格納階層が不正です。");
            foreach (var entry in entries.EnumerateArray())
                chain.Add(entry.GetString() ?? throw new InvalidDataException("格納名がありません。"));
        }
        var sha = root.TryGetProperty("rootSha256", out var fingerprint) ? fingerprint.GetString() : null;
        var defaults = new ManagedArchiveLimits();
        var limits = defaults;
        if (root.TryGetProperty("limits", out var settings))
        {
            CheckProperties(settings, ["maximumEntries", "maximumInputBytes", "maximumEntryBytes", "maximumDecodedBytes",
                "maximumPreviewBytes", "maximumOutputBytes", "maximumPathCharacters", "maximumWrapperDepth", "maximumWorkBytes"]);
            limits = new(
                MaximumEntries: checked((int)Lower("maximumEntries", defaults.MaximumEntries)),
                MaximumInputBytes: Lower("maximumInputBytes", defaults.MaximumInputBytes),
                MaximumEntryBytes: Lower("maximumEntryBytes", defaults.MaximumEntryBytes),
                MaximumDecodedBytes: Lower("maximumDecodedBytes", defaults.MaximumDecodedBytes),
                MaximumPreviewBytes: checked((int)Lower("maximumPreviewBytes", defaults.MaximumPreviewBytes)),
                MaximumOutputBytes: Lower("maximumOutputBytes", defaults.MaximumOutputBytes),
                MaximumPathCharacters: checked((int)Lower("maximumPathCharacters", defaults.MaximumPathCharacters)),
                MaximumWrapperDepth: checked((int)Lower("maximumWrapperDepth", defaults.MaximumWrapperDepth)),
                MaximumWorkBytes: Lower("maximumWorkBytes", defaults.MaximumWorkBytes));
            long Lower(string name, long maximum)
            {
                if (!settings.TryGetProperty(name, out var value)) return maximum;
                if (!value.TryGetInt64(out var number) || number <= 0 || number > maximum)
                    throw new InvalidDataException("descriptorの上限は正の整数で、既定値以下にしてください。");
                return number;
            }
        }
        return (new ArchiveSource(Path.GetFullPath(path, Path.GetDirectoryName(descriptor)!), chain, sha), limits);
    }

    private static void CheckProperties(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("descriptorの項目はobjectで指定してください。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("descriptorに未対応または重複した項目があります。");
    }
}

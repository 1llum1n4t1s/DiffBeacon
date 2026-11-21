using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class FolderModelScenarios
{
    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, Action<string, string> skip, string python)
    {
        var work = Path.Combine(fixtures, "folder-model");
        if (Directory.Exists(work)) throw new IOException("Folder model成果物は新しいrunへ作成してください。");
        Directory.CreateDirectory(work);
        var fixture = Path.GetFullPath("tests/Fixtures/FolderSync");
        var expectationPath = Path.Combine(fixture, "model-expectations.json");
        var expectedBytes = File.ReadAllBytes(expectationPath);
        using var expected = JsonDocument.Parse(expectedBytes);
        var root = expected.RootElement;
        var inputs = Path.Combine(work, "inputs");
        var fixedTime = DateTime.Parse(root.GetProperty("fixedMtimeUtc").GetString()!, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        foreach (var group in root.GetProperty("groups").EnumerateObject())
        {
            foreach (var side in new[] { "left", "right" })
            {
                var sideRoot = Path.Combine(inputs, group.Name, side); Directory.CreateDirectory(sideRoot);
                foreach (var directory in group.Value.GetProperty(side + "Dirs").EnumerateArray())
                    Directory.CreateDirectory(Within(sideRoot, directory.GetString()!));
                foreach (var file in group.Value.GetProperty(side + "Files").EnumerateObject())
                {
                    var path = Within(sideRoot, file.Name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                    stream.Write(Convert.FromHexString(file.Value.GetString()!));
                }
            }
            if (group.Value.TryGetProperty("readonly", out var readOnly))
                foreach (var item in readOnly.EnumerateArray())
                {
                    var path = Within(Path.Combine(inputs, group.Name), item.GetString()!);
                    File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                }
        }
        var controls = Path.Combine(inputs, "controls"); Directory.CreateDirectory(controls);
        foreach (var control in root.GetProperty("controls").EnumerateObject())
            File.WriteAllText(Within(controls, control.Name), control.Value.GetString()!, new UTF8Encoding(false));
        // 全入力を作成し終えてからdirectoryも含めて同じ過去UTCへ固定する。
        foreach (var entry in Directory.EnumerateFileSystemEntries(inputs, "*", SearchOption.AllDirectories).Prepend(inputs))
        {
            if (Directory.Exists(entry)) Directory.SetLastWriteTimeUtc(entry, fixedTime);
            else File.SetLastWriteTimeUtc(entry, fixedTime);
        }
        var before = Snapshot(inputs);
        await Json(work, "source-before.json", before);
        await File.WriteAllBytesAsync(Path.Combine(work, "expectations.json"), expectedBytes);
        var records = new List<object>();
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            if (item.TryGetProperty("platform", out var platform) && platform.GetString() == "windows" && !OperatingSystem.IsWindows())
            {
                var reason = "Windows FileShare.Noneの実IO error採取。ほかのOSの同等失敗は未実測。";
                skip("Folder model " + id, reason); records.Add(new { id, skipped = true, reason }); continue;
            }
            var group = item.GetProperty("group").GetString()!;
            var groupRoot = Path.Combine(inputs, group);
            var arguments = item.GetProperty("args").EnumerateArray().Select(value => value.GetString()!
                .Replace("{left}", Path.Combine(groupRoot, "left"), StringComparison.Ordinal)
                .Replace("{right}", Path.Combine(groupRoot, "right"), StringComparison.Ordinal)
                .Replace("{missing}", Path.Combine(groupRoot, "missing-root"), StringComparison.Ordinal)
                .Replace("{sizeFilter}", Path.Combine(controls, "size.flt"), StringComparison.Ordinal)
                .Replace("{typeFilter}", Path.Combine(controls, "type.flt"), StringComparison.Ordinal)).ToArray();
            FileStream? held = null;
            try
            {
                if (item.TryGetProperty("lock", out var locked) && locked.ValueKind == JsonValueKind.String)
                    held = new FileStream(Within(Path.Combine(groupRoot, "left"), locked.GetString()!), FileMode.Open, FileAccess.Read, FileShare.None);
                var result = await run("folder-model-" + id, item.GetProperty("exit").GetInt32(), item.GetProperty("json").GetBoolean(), arguments);
                await File.WriteAllTextAsync(Path.Combine(work, id + ".stdout.txt"), result.Stdout, new UTF8Encoding(false));
                await File.WriteAllTextAsync(Path.Combine(work, id + ".stderr.txt"), result.Stderr, new UTF8Encoding(false));
                records.Add(new { id, skipped = false, command = result });
            }
            finally { held?.Dispose(); }
        }
        await Json(work, "commands.json", records);
        var after = Snapshot(inputs); await Json(work, "source-after.json", after);
        check("Folder model CSharp source snapshot retained", before.SequenceEqual(after), "bytes/SHA/mtime/attributes/empty directories");
        var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-B", "-X", "utf8", Path.Combine(fixture, "verify-model.py"), fixture, work }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var creation = process.StartTime.ToUniversalTime();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var text = await stdout; var error = await stderr;
        await File.WriteAllTextAsync(Path.Combine(work, "independent.stdout.json"), text, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(work, "independent.stderr.txt"), error, new UTF8Encoding(false));
        await Json(work, "independent-process.json", new { pid = process.Id, creationUtc = creation, exitObservedUtc = DateTime.UtcNow,
            actualExit = process.ExitCode, readerSha256 = Hash(Path.Combine(fixture, "verify-model.py")), expectedSha256 = Hash(expectationPath) });
        check("Folder model independent full inputs and plans", process.ExitCode == 0, text + error);
    }

    private static string Within(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root); var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("固定fixture pathは入力root内だけを使用します。");
        return path;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed record InputEntry(string Path, string Kind, long Length, string? Sha256, string? Hex, long LastWriteFileTime, int Attributes);
    private static InputEntry[] Snapshot(string inputs) => Directory.EnumerateFileSystemEntries(inputs, "*", SearchOption.AllDirectories).Prepend(inputs)
        .Order(StringComparer.Ordinal).Select(path =>
        {
            var directory = Directory.Exists(path); var bytes = directory ? null : File.ReadAllBytes(path);
            return new InputEntry(System.IO.Path.GetRelativePath(inputs, path).Replace('\\', '/'), directory ? "directory" : "file", bytes?.Length ?? 0,
                bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)), bytes is null ? null : Convert.ToHexString(bytes),
                File.GetLastWriteTimeUtc(path).ToFileTimeUtc(), (int)File.GetAttributes(path));
        }).ToArray();
    private static Task Json<T>(string work, string name, T value) => File.WriteAllTextAsync(Path.Combine(work, name),
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
}

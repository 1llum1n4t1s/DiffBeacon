using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class FolderWindowsMetadataScenarios
{
    internal static async Task RunAsync(string work, string fixture,
        Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, Action<string, string> skip, string python)
    {
        var drive = OperatingSystem.IsWindows() ? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(work))!) : null;
        if (drive is null || drive.DriveType == DriveType.Network ||
            !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            skip("Folder Windows metadata actual main", "Windows local NTFS専用。"); return;
        }
        var root = Path.GetFullPath(Path.Combine(work, "windows-metadata-main"));
        if (Directory.Exists(root)) throw new IOException("Windows metadata成果物は新しいrunへ作成してください。");
        Directory.CreateDirectory(root);
        var reader = Path.GetFullPath(Path.Combine(fixture, "verify-metadata-copy.py"));
        using var provenance = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "copy-provenance.json")));
        var expectedSha = provenance.RootElement.GetProperty("fixedFiles").GetProperty("verify-metadata-copy.py").GetString()!;
        check("Folder Windows metadata main fixed reader", Hash(reader) == expectedSha, Hash(reader));
        if (Hash(reader) != expectedSha) throw new IOException("Windows metadata reader SHA不一致。");
        await Reader("prepare");
        var manifestPath = Path.Combine(root, "inputs.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        foreach (var capability in manifest.RootElement.GetProperty("skips").EnumerateArray())
            skip("Folder Windows metadata compressed parent", capability.GetString()!);
        foreach (var row in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = row.GetProperty("id").GetString()!;
            var location = Path.Combine(root, "cases", id);
            var command = await run("folder-metadata-main-" + id, 0, false,
                ["--folder-sync", Path.Combine(location, "left"), Path.Combine(location, "right"),
                 "--direction", "left-to-right", "--copy", "all", "--select", "payload.bin"]);
            await File.WriteAllTextAsync(Path.Combine(location, "app.stdout.json"), command.Stdout, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(location, "app.stderr.txt"), command.Stderr, new UTF8Encoding(false));
            await WriteJson(Path.Combine(location, "app-process.json"), command);
        }
        await Reader("verify");

        async Task Reader(string action)
        {
            var inputPaths = action == "verify"
                ? new[] { Path.Combine(root, "inputs.json") }.Concat(Directory.GetFiles(Path.Combine(root, "cases"), "app.stdout.json", SearchOption.AllDirectories)).ToArray()
                : Array.Empty<string>();
            var inputBefore = inputPaths.ToDictionary(path => path, Hash);
            var before = Hash(reader);
            if (before != expectedSha) throw new IOException("Windows metadata reader起動前SHA不一致。");
            var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", reader, action, root }) start.ArgumentList.Add(argument);
            var launched = DateTime.UtcNow;
            using var process = Process.Start(start)!;
            var birth = process.StartTime.ToUniversalTime();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); var text = await stdout; var error = await stderr;
            var after = Hash(reader); var inputAfter = inputPaths.ToDictionary(path => path, Hash);
            await File.WriteAllTextAsync(Path.Combine(root, action + ".stdout.json"), text, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, action + ".stderr.txt"), error, new UTF8Encoding(false));
            await WriteJson(Path.Combine(root, action + "-process.json"), new { pid = process.Id, creationUtc = birth,
                launchedUtc = launched, exitObservedUtc = DateTime.UtcNow, waitCompleted = true,
                actualExit = process.ExitCode, readerBeforeSha256 = before, readerAfterSha256 = after,
                expectedSha256 = expectedSha, inputBefore, inputAfter });
            var passed = process.ExitCode == 0 && before == after && inputBefore.All(pair => inputAfter[pair.Key] == pair.Value);
            check("Folder Windows metadata actual main independent " + action, passed, text + error);
            if (!passed) throw new IOException("Windows metadata independent reader拒否: " + error);
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static Task WriteJson(string path, object value) => File.WriteAllTextAsync(path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
}

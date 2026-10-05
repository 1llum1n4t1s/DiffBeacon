using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class FolderCopyScenarios
{
    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run,
        Action<string, bool, string> check, Action<string, string> skip, string python)
    {
        var work = Path.Combine(fixtures, "folder-copy");
        if (Directory.Exists(work)) throw new IOException("Folder copy成果物は新しいrunへ作成してください。");
        Directory.CreateDirectory(work);
        var fixture = Path.GetFullPath("tests/Fixtures/FolderSync");
        var expectedPath = Path.Combine(fixture, "copy-expectations.json");
        var bytes = await File.ReadAllBytesAsync(expectedPath);
        await File.WriteAllBytesAsync(Path.Combine(work, "expectations.json"), bytes);
        using var expected = JsonDocument.Parse(bytes);
        var root = expected.RootElement;
        var time = DateTime.Parse(root.GetProperty("fixedMtimeUtc").GetString()!, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        var destinationDirectoryTime = DateTime.Parse(root.GetProperty("fixedDestinationDirectoryMtimeUtc").GetString()!, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        var records = new List<object>();
        var plans = new List<object>();
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var caseRoot = Path.Combine(work, "cases", id);
            var group = item.GetProperty("before");
            foreach (var side in new[] { "left", "right" })
            {
                var sideRoot = Path.Combine(caseRoot, side); Directory.CreateDirectory(sideRoot);
                foreach (var directory in group.GetProperty(side + "Dirs").EnumerateArray()) Directory.CreateDirectory(Within(sideRoot, directory.GetString()!));
                foreach (var file in group.GetProperty(side + "Files").EnumerateObject())
                {
                    var path = Within(sideRoot, file.Name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, Convert.FromHexString(file.Value.GetString()!));
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                }
            }
            var controls = Path.Combine(caseRoot, "controls"); Directory.CreateDirectory(controls);
            foreach (var control in root.GetProperty("controls").EnumerateObject())
                await File.WriteAllTextAsync(Within(controls, control.Name), control.Value.GetString()!, new UTF8Encoding(false));
            foreach (var entry in group.GetProperty("readonly").EnumerateArray())
            {
                var path = Within(caseRoot, entry.GetString()!);
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            }
            PinTime(caseRoot, time);
            // source/fileと既存destination directoryを別時刻にし、誤った時刻転写を識別する。
            var destinationRoot = Path.Combine(caseRoot, item.GetProperty("destination").GetString()!);
            foreach (var directory in Directory.EnumerateDirectories(destinationRoot, "*", SearchOption.AllDirectories))
                Directory.SetLastWriteTimeUtc(directory, destinationDirectoryTime);
            Directory.SetLastWriteTimeUtc(destinationRoot, destinationDirectoryTime);
            await WriteJson(caseRoot, "before.json", Snapshot(caseRoot));
            var arguments = item.GetProperty("args").EnumerateArray().Select(value => value.GetString()!
                .Replace("{left}", Path.Combine(caseRoot, "left"), StringComparison.Ordinal)
                .Replace("{right}", Path.Combine(caseRoot, "right"), StringComparison.Ordinal)
                .Replace("{missing}", Path.Combine(caseRoot, "missing-root"), StringComparison.Ordinal)
                .Replace("{sizeFilter}", Path.Combine(controls, "size.flt"), StringComparison.Ordinal)
                .Replace("{typeFilter}", Path.Combine(controls, "type.flt"), StringComparison.Ordinal)).ToArray();
            if (item.TryGetProperty("planCandidates", out _))
            {
                var planArguments = (string[])arguments.Clone(); planArguments[0] = "--folder-plan";
                var plan = await run("folder-copy-plan-" + id, 0, true, planArguments);
                await WriteJson(caseRoot, "after-plan.json", Snapshot(caseRoot));
                plans.Add(new { id, command = plan, work = caseRoot });
            }
            var command = await run("folder-copy-" + id, item.GetProperty("exit").GetInt32(), item.GetProperty("json").GetBoolean(), arguments);
            await WriteJson(caseRoot, "after.json", Snapshot(caseRoot));
            records.Add(new { id, command, work = caseRoot });
        }

        // 256MiB制限を越える一つのfileだけを、64KiB bufferで作成・観測する。
        var large = Path.Combine(work, "large"); var largeSource = Path.Combine(large, "left"); var largeDestination = Path.Combine(large, "right");
        Directory.CreateDirectory(largeSource); Directory.CreateDirectory(largeDestination);
        var payload = Path.Combine(largeSource, "payload.bin");
        var largeSize = root.GetProperty("largeFile").GetProperty("size").GetInt64();
        var buffer = new byte[65536]; for (var index = 0; index < buffer.Length; index++) buffer[index] = (byte)index;
        await using (var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous))
        {
            var remaining = largeSize;
            while (remaining != 0) { var count = (int)Math.Min(buffer.Length, remaining); await output.WriteAsync(buffer.AsMemory(0, count)); remaining -= count; }
        }
        File.SetLastWriteTimeUtc(payload, time); PinTime(large, time);
        await WriteJson(large, "before.json", Snapshot(large));
        var largeCommand = await run("folder-copy-large-stream", 0, false, ["--folder-copy", largeSource, largeDestination, "payload.bin"]);
        await WriteJson(large, "after.json", Snapshot(large)); records.Add(new { id = "large-stream", command = largeCommand, work = large });
        await WriteJson(work, "commands.json", records);
        await WriteJson(work, "plan-commands.json", plans);
        var specials = new List<object>();
        foreach (var item in root.GetProperty("special").GetProperty(OperatingSystem.IsWindows() ? "windows" : "mac").EnumerateArray())
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) break;
            var kind=OperatingSystem.IsWindows()?item.GetProperty("id").GetString()!:item.GetProperty("kind").GetString()!;
            var side=item.TryGetProperty("side",out var s)?s.GetString()!:"left";
            var mode=item.TryGetProperty("mode",out var m)?m.GetString()!:"content";
            var mixed=item.TryGetProperty("mixed",out var mix)&&mix.GetBoolean();
            var id=OperatingSystem.IsWindows()?kind:$"{kind}-{side}-{mode}{(mixed?"-mixed":"")}";
            var specialRoot=Path.Combine(work,"special",id);Directory.CreateDirectory(specialRoot);
            var before=await Special("prepare",specialRoot,kind,side,mixed);
            var calls=new List<CommandResult>();
            string? skipped=null;
            if (OperatingSystem.IsMacOS() && kind=="case-alias")
            {
                if(Directory.Exists(Path.Combine(specialRoot,"LEFT")))
                {
                    calls.Add(await run("folder-copy-mac-"+id+"-sync",2,false,["--folder-sync",Path.Combine(specialRoot,"left"),Path.Combine(specialRoot,"LEFT"),"--direction","left-to-right","--copy","all","--select","payload.bin"]));
                    calls.Add(await run("folder-copy-mac-"+id+"-legacy",2,false,["--folder-copy",Path.Combine(specialRoot,"left"),Path.Combine(specialRoot,"LEFT"),"payload.bin"]));
                }
                else {skipped="Case-sensitive filesystem: Directory.Exists(LEFT) is false";skip("Folder copy Mac case alias",skipped);}
            }
            else if (OperatingSystem.IsMacOS())
            {
                var exit=kind=="link"?(side=="both"?0:1):2;
                calls.Add(await run("folder-copy-mac-"+id+"-directory",exit,true,["--directory",Path.Combine(specialRoot,"left"),Path.Combine(specialRoot,"right"),"--mode",mode]));
                foreach(var sourceSide in side=="both"||mixed?new[]{"left","right"}:new[]{side})
                    calls.Add(await run("folder-copy-mac-"+id+"-"+sourceSide,2,false,["--folder-copy",Path.Combine(specialRoot,sourceSide),Path.Combine(specialRoot,sourceSide=="left"?"right":"left"),"node.bin"]));
            }
            else calls.Add(await run("folder-copy-win-"+id,0,false,["--folder-copy",Path.Combine(specialRoot,"left"),Path.Combine(specialRoot,"right"),"payload.bin"]));
            var after=await Special("snapshot",specialRoot,kind,side,mixed);
            // 特殊nodeは全app/helperの終了・独立lstat一致確認後、その専用nodeだけを解放する。
            CommandResult? release=null;
            if(OperatingSystem.IsMacOS() && kind is "fifo" or "socket") release=await Special("release",specialRoot,kind,side,mixed);
            specials.Add(new{id,kind,side,mode,mixed,work=specialRoot,before,after,commands=calls,release,skipped});
        }
        await WriteJson(work,"special-commands.json",specials);

        await ReviewRegressions();

        var gui = await run("folder-copy-gui", 0, false, ["--self-test", Path.Combine(work, "gui"), "--folder-copy-only"]);
        await File.WriteAllTextAsync(Path.Combine(work, "gui.stdout.txt"), gui.Stdout, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(work, "gui.stderr.txt"), gui.Stderr, new UTF8Encoding(false));
        await WriteJson(work, "gui-process.json", gui);

        var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(fixture, "verify-copy.py"), fixture, work }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!; var creation = process.StartTime.ToUniversalTime();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var text = await stdout; var error = await stderr;
        await File.WriteAllTextAsync(Path.Combine(work, "independent.stdout.json"), text, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(work, "independent.stderr.txt"), error, new UTF8Encoding(false));
        await WriteJson(work, "independent-process.json", new { pid = process.Id, creationUtc = creation, exitObservedUtc = DateTime.UtcNow, actualExit = process.ExitCode,
            readerSha256 = Hash(Path.Combine(fixture, "verify-copy.py")), expectedSha256 = Hash(expectedPath) });
        check("Folder copy independent full bytes metadata and GUI", process.ExitCode == 0, text + error);
        await WindowsStreams();
        if (!OperatingSystem.IsMacOS()) skip("Folder copy Mac FIFO/socket actual app", "Mac lstat/非regularの実OS拒否とCSDK ABIはMac RID実検証工程。");
        if (!OperatingSystem.IsWindows()) skip("Folder copy Windows compressed sparse actual app", "NTFS圧縮/Sparse mainstream bytesはWindows実測工程。ADS/EFS完全保持ではありません。");


        async Task WindowsStreams()
        {
            if (!OperatingSystem.IsWindows()) { skip("Folder Windows full streams", "Windows file stream backend専用。macOSのdefault-only経路はwholeで検証。"); return; }
            var records = new List<object>();
            foreach (var id in new[] { "overwrite", "fresh", "empty-main", "plain", "directory", "long-path" })
            {
                var location = Path.GetFullPath(Path.Combine(work, "windows-streams", id));
                await StreamReader("prepare", location, id);
                var relative = id == "directory" ? "tree" : id == "long-path" ? string.Join('/', Enumerable.Repeat(new string('l', 40), 8).Append("payload.bin")) : "payload.bin";
                var command = await run("folder-stream-" + id, 0, false, ["--folder-sync", Path.Combine(location, "left"), Path.Combine(location, "right"), "--direction", "left-to-right", "--copy", "all", "--select", relative]);
                await File.WriteAllTextAsync(Path.Combine(location, "app.stdout.json"), command.Stdout, new UTF8Encoding(false));
                await File.WriteAllTextAsync(Path.Combine(location, "app.stderr.txt"), command.Stderr, new UTF8Encoding(false));
                await WriteJson(location, "app-process.json", command);
                records.Add(new { id, work = location, command });
            }
            await WriteJson(work, "windows-stream-commands.json", records);
            await StreamReader("verify", Path.GetFullPath(work));
        }
        async Task StreamReader(string action, string location, string? id = null)
        {
            var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-B", "-X", "utf8", Path.Combine(fixture, "verify-streams.py"), action, location }) start.ArgumentList.Add(arg);
            if (id is not null) start.ArgumentList.Add(id);
            var launched = DateTime.UtcNow; using var helper = Process.Start(start)!; var created = helper.StartTime.ToUniversalTime();
            var stdout = helper.StandardOutput.ReadToEndAsync(); var stderr = helper.StandardError.ReadToEndAsync();
            await helper.WaitForExitAsync(); var text = await stdout; var error = await stderr;
            var save = action == "prepare" ? location : work;
            // helper失敗でも診断を保持する。製品を実行する前にprepare失敗を伝播する。
            Directory.CreateDirectory(save);
            await File.WriteAllTextAsync(Path.Combine(save, "stream-" + action + ".stdout.json"), text, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(save, "stream-" + action + ".stderr.txt"), error, new UTF8Encoding(false));
            await WriteJson(save, "stream-" + action + "-process.json", new { pid = helper.Id, creationUtc = created, launchedUtc = launched, exitObservedUtc = DateTime.UtcNow, actualExit = helper.ExitCode, readerSha256 = Hash(Path.Combine(fixture, "verify-streams.py")) });
            check("Folder stream independent " + action + " " + id, helper.ExitCode == 0, text + error);
            if (helper.ExitCode != 0) throw new IOException("Folder stream independent reader拒否: " + error);
        }

        async Task<CommandResult> Special(string action,string specialRoot,string kind,string side,bool mixed)
        {
            var start=new ProcessStartInfo(python){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
            foreach(var argument in new[]{"-B","-X","utf8",Path.Combine(fixture,"verify-copy.py"),"--special",action,specialRoot,kind,side,mixed?"mixed":"plain"})start.ArgumentList.Add(argument);
            var launched=DateTime.UtcNow;var timer=Stopwatch.StartNew();using var helper=Process.Start(start)!;var created=helper.StartTime.ToUniversalTime();var output=helper.StandardOutput.ReadToEndAsync();var error=helper.StandardError.ReadToEndAsync();
            await helper.WaitForExitAsync();var stdout=await output;var stderr=await error;
            await File.WriteAllTextAsync(Path.Combine(specialRoot,action+".stdout.json"),stdout,new UTF8Encoding(false));await File.WriteAllTextAsync(Path.Combine(specialRoot,action+".stderr.txt"),stderr,new UTF8Encoding(false));
            check("Folder special "+Path.GetFileName(specialRoot)+" "+action,helper.ExitCode==0,stdout+stderr);
            return new CommandResult("folder-special-"+action,start.ArgumentList.ToArray(),helper.ExitCode,stdout,stderr,timer.ElapsedMilliseconds,helper.Id,created,launched,DateTime.UtcNow);
        }

        async Task ReviewRegressions()
        {
            var fixedPath = Path.Combine(fixture, "review-expectations.json");
            var fixedBytes = await File.ReadAllBytesAsync(fixedPath);
            await File.WriteAllBytesAsync(Path.Combine(work, "review-expectations.json"), fixedBytes);
            using var contract = JsonDocument.Parse(fixedBytes);
            var review = new List<object>();
            foreach (var item in contract.RootElement.GetProperty("cli").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var location = Path.Combine(work, "review", id);
                foreach (var side in new[] { "left", "right" })
                {
                    Directory.CreateDirectory(Path.Combine(location, side));
                    foreach (var file in item.GetProperty("files").GetProperty(side).EnumerateObject())
                    {
                        var path = Within(Path.Combine(location, side), file.Name);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await File.WriteAllBytesAsync(path, Convert.FromHexString(file.Value.GetString()!));
                    }
                }
                PinTime(location, time);
                await WriteJson(location, "before.json", Snapshot(location));
                var args = item.GetProperty("args").EnumerateArray().Select(v => v.GetString()!
                    .Replace("{left}", Path.Combine(location, "left"), StringComparison.Ordinal)
                    .Replace("{right}", Path.Combine(location, "right"), StringComparison.Ordinal)
                    .Replace("{inner}", Path.Combine(location, "left", "Sub"), StringComparison.Ordinal)).ToArray();
                CommandResult? plan = null;
                if (item.GetProperty("plan").GetBoolean())
                {
                    var planArgs = (string[])args.Clone(); planArgs[0] = "--folder-plan";
                    plan = await run("folder-review-plan-" + id, 0, true, planArgs);
                    await WriteJson(location, "after-plan.json", Snapshot(location));
                }
                var command = await run("folder-review-" + id, 2, false, args);
                await WriteJson(location, "after.json", Snapshot(location));
                review.Add(new { id, work = location, command, plan });
            }
            await WriteJson(work, "review-commands.json", review);
            var macReview = new List<object>();
            if (OperatingSystem.IsMacOS()) foreach (var item in contract.RootElement.GetProperty("mac").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var kind = item.GetProperty("kind").GetString()!;
                var position = item.GetProperty("position").GetString()!;
                var location = Path.Combine(work, "review-mac", id); Directory.CreateDirectory(location);
                var before = await ReviewHelper("prepare", location, kind, position);
                var calls = new List<CommandResult>(); var separate = new List<CommandResult>(); string? skipped = null;
                if (kind == "alias")
                {
                    using var observed = JsonDocument.Parse(before.Stdout);
                    if (observed.RootElement.GetProperty("aliasAvailable").GetBoolean())
                        foreach (var direction in new[] { "left-to-right", "right-to-left" }) foreach (var api in new[] { "sync", "legacy" }) foreach (var selected in new[] { "a.bin", "Sub/a.bin" })
                        {
                            var outer = Path.Combine(location, "left"); var inner = Path.Combine(location, "LEFT", "Sub");
                            var args = api == "sync" ? new[] { "--folder-sync", outer, inner, "--direction", direction, "--copy", "all", "--select", selected }
                                : new[] { "--folder-copy", direction == "left-to-right" ? outer : inner, direction == "left-to-right" ? inner : outer, selected };
                            calls.Add(await run($"folder-review-{id}-{api}-{direction}-{selected.Replace('/', '-')}", 2, false, args));
                        }
                    else { skipped = "Case-sensitive filesystem: nested alias unavailable"; skip("Folder review Mac nested alias", skipped); }
                }
                else
                {
                    var selected = position == "leaf" ? "node.bin" : "node.bin/child.bin";
                    calls.Add(await run("folder-review-" + id + "-sync", 2, false, ["--folder-sync", Path.Combine(location, "left"), Path.Combine(location, "right"), "--direction", "left-to-right", "--copy", "all", "--select", selected]));
                    if (position == "ancestor") calls.Add(await run("folder-review-" + id + "-legacy", 2, false, ["--folder-copy", Path.Combine(location, "left"), Path.Combine(location, "right"), selected]));
                }
                var after = await ReviewHelper("snapshot", location, kind, position);
                CommandResult? release = null;
                if (kind is "fifo" or "socket") release = await ReviewHelper("release", location, kind, position);
                else if (skipped is not null)
                {
                    // 実際に別inodeのcase-sensitive rootsだけ、誤拒否されないことも確認する。
                    foreach (var direction in new[] { "left-to-right", "right-to-left" })
                        separate.Add(await run("folder-review-separate-" + direction, 0, true, ["--folder-sync", Path.Combine(location, "left"), Path.Combine(location, "LEFT"), "--direction", direction, "--copy", "all", "--select", "independent.bin"]));
                    await ReviewHelper("separate-snapshot", location, kind, position);
                }
                macReview.Add(new { id, work = location, before, after, commands = calls, separateCommands = separate, release, skipped });
            }
            await WriteJson(work, "review-mac-commands.json", macReview);
        }

        async Task<CommandResult> ReviewHelper(string action, string location, string kind, string position)
        {
            var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", Path.Combine(fixture, "verify-review.py"), "--helper", action, location, kind, position }) start.ArgumentList.Add(argument);
            var launch = DateTime.UtcNow; var timer = Stopwatch.StartNew(); using var helper = Process.Start(start)!;
            var creation = helper.StartTime.ToUniversalTime(); var stdout = helper.StandardOutput.ReadToEndAsync(); var stderr = helper.StandardError.ReadToEndAsync();
            await helper.WaitForExitAsync(); var text = await stdout; var error = await stderr;
            await File.WriteAllTextAsync(Path.Combine(location, action + ".stdout.json"), text, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(location, action + ".stderr.txt"), error, new UTF8Encoding(false));
            check("Folder review helper " + Path.GetFileName(location) + " " + action, helper.ExitCode == 0, text + error);
            var record = new CommandResult("folder-review-helper-" + action, start.ArgumentList.ToArray(), helper.ExitCode, text, error, timer.ElapsedMilliseconds, helper.Id, creation, launch, DateTime.UtcNow);
            await WriteJson(location, action + "-process.json", record);
            return record;
        }
    }

    private static string Within(string root, string relative)
    {
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!resolved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("固定folder-copy fixtureは専用root内だけを使用します。");
        return resolved;
    }
    private static void PinTime(string root, DateTime time)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
            if (Directory.Exists(path)) Directory.SetLastWriteTimeUtc(path, time); else File.SetLastWriteTimeUtc(path, time);
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private sealed record ObservedEntry(string Path, string Kind, long Length, string? Sha256, long LastWriteFileTime, int Attributes, int? UnixMode);
    private static ObservedEntry[] Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)[0] is "left" or "right" or "controls")
        .Order(StringComparer.Ordinal).Select(path =>
        {
            var directory = Directory.Exists(path);
            return new ObservedEntry(Path.GetRelativePath(root, path).Replace('\\', '/'), directory ? "directory" : "file",
                directory ? 0 : new FileInfo(path).Length, directory ? null : Hash(path), File.GetLastWriteTimeUtc(path).ToFileTimeUtc(),
                (int)File.GetAttributes(path), OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path));
        }).ToArray();
    private static Task WriteJson<T>(string root, string name, T value) => File.WriteAllTextAsync(Path.Combine(root, name),
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
}

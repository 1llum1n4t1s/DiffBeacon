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
        await VerifyGuiListDependencies();
        var moveReports = Directory.GetFiles(Path.Combine(work, "gui"), "folder-move-observations.json", SearchOption.AllDirectories);
        check("Folder move GUI observation report", moveReports.Length == 1, "report count=" + moveReports.Length);
        if (moveReports.Length == 1)
        {
            using var moveDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(moveReports[0]));
            var moveRoot = moveDocument.RootElement;
            var moveCases = moveRoot.GetProperty("cases").EnumerateArray().ToArray();
            var expectedMoveCases = OperatingSystem.IsWindows() ? 18 : 17;
            check("Folder move GUI report complete", moveRoot.GetProperty("complete").GetBoolean()
                && moveCases.Length == expectedMoveCases, $"case count={moveCases.Length}, expected={expectedMoveCases}");
            if (moveRoot.GetProperty("complete").GetBoolean() && moveCases.Length == expectedMoveCases)
            {
            var success = MoveCase("success");
            var successSource = FindMoveEntry(success.GetProperty("beforeLeft"), "a.bin");
            var successAfterSource = FindMoveEntry(success.GetProperty("afterLeft"), "a.bin");
            var successAfterDestination = FindMoveEntry(success.GetProperty("afterRight"), "a.bin");
            var successResult = success.GetProperty("result");
            check("Folder move GUI publishes exact source bytes", success.GetProperty("confirmed").GetBoolean()
                && successResult.GetProperty("Succeeded").GetBoolean()
                && successResult.GetProperty("MovedCount").GetInt32() == 1
                && successAfterSource.ValueKind == JsonValueKind.Undefined
                && successAfterDestination.ValueKind == JsonValueKind.Object
                && successAfterDestination.GetProperty("sha256").GetString() == successSource.GetProperty("sha256").GetString(),
                "source file moves to the selected destination with identical SHA-256");
            var reverse = MoveCase("right-to-left");
            check("Folder move GUI supports right to left", MoveSucceeded(reverse, "a.bin")
                && reverse.GetProperty("sourceSide").GetString() == "Right"
                && reverse.GetProperty("destinationSide").GetString() == "Left",
                "right-side source is removed and left-side destination has the exact SHA-256");
            var multiple = MoveCase("multiple-success");
            check("Folder move GUI moves every selected file", MoveSucceeded(multiple, "a.bin", "b.bin"),
                "both selected source files move with their exact SHA-256 values");
            foreach (var id in new[] { "reject-existing-destination", "reject-directory", "reject-shared-input" })
            {
                var rejected = MoveCase(id);
                check("Folder move GUI rejects safely " + id, !rejected.GetProperty("confirmed").GetBoolean()
                    && rejected.GetProperty("result").ValueKind == JsonValueKind.Null
                    && SameMoveSnapshot(rejected.GetProperty("beforeLeft"), rejected.GetProperty("afterLeft"))
                    && SameMoveSnapshot(rejected.GetProperty("beforeRight"), rejected.GetProperty("afterRight")),
                    "no confirmation or filesystem change");
            }
            var changed = MoveCase("source-changed-after-plan");
            check("Folder move GUI rechecks source", changed.GetProperty("confirmed").GetBoolean()
                && !changed.GetProperty("result").GetProperty("MutationOccurred").GetBoolean()
                && FindMoveEntry(changed.GetProperty("afterLeft"), "a.bin").GetProperty("sha256").GetString()
                    == Convert.ToHexString(SHA256.HashData(new byte[] { 0x99 }))
                && SameMoveSnapshot(changed.GetProperty("beforeRight"), changed.GetProperty("afterRight")),
                "changed source remains and destination is untouched");
            var partial = MoveCase("partial-second-failure");
            var moveEntries = partial.GetProperty("result").GetProperty("entries").EnumerateArray()
                .ToDictionary(entry => entry.GetProperty("RelativePath").GetString()!, entry => entry.GetProperty("status").GetString()!, StringComparer.Ordinal);
            check("Folder move GUI records partial mutation", partial.GetProperty("result").GetProperty("MutationOccurred").GetBoolean()
                && moveEntries.GetValueOrDefault("a.bin") == "Moved"
                && moveEntries.GetValueOrDefault("b.bin") == "Failed"
                && moveEntries.GetValueOrDefault("c.bin") == "NotExecuted"
                && FindMoveEntry(partial.GetProperty("afterLeft"), "a.bin").ValueKind == JsonValueKind.Undefined
                && FindMoveEntry(partial.GetProperty("afterRight"), "a.bin").ValueKind == JsonValueKind.Object
                && FindMoveEntry(partial.GetProperty("afterLeft"), "b.bin").ValueKind == JsonValueKind.Object
                && FindMoveEntry(partial.GetProperty("afterLeft"), "c.bin").ValueKind == JsonValueKind.Object
                && FindMoveEntry(partial.GetProperty("afterRight"), "b.bin").ValueKind == JsonValueKind.Undefined
                && FindMoveEntry(partial.GetProperty("afterRight"), "c.bin").ValueKind == JsonValueKind.Undefined,
                "first move retained, failed and not-run source files stay in place");

            var filterChanged = MoveCase("filter-changed-after-hash");
            check("Folder move GUI cancels after filter changes during hashing", filterChanged.GetProperty("confirmed").GetBoolean()
                && filterChanged.GetProperty("result").GetProperty("Cancelled").GetBoolean()
                && !filterChanged.GetProperty("result").GetProperty("MutationOccurred").GetBoolean()
                && SameMoveSnapshot(filterChanged.GetProperty("beforeLeft"), filterChanged.GetProperty("afterLeft"))
                && SameMoveSnapshot(filterChanged.GetProperty("beforeRight"), filterChanged.GetProperty("afterRight")),
                "both roots remain unchanged after the protected filter changes");
            var destinationAppeared = MoveCase("destination-appears-after-plan");
            check("Folder move GUI preserves a destination that appears after planning", destinationAppeared.GetProperty("confirmed").GetBoolean()
                && !destinationAppeared.GetProperty("result").GetProperty("MutationOccurred").GetBoolean()
                && FindMoveEntry(destinationAppeared.GetProperty("afterSource"), "a.bin").ValueKind == JsonValueKind.Object
                && FindMoveEntry(destinationAppeared.GetProperty("afterDestination"), "a.bin").GetProperty("sha256").GetString()
                    == Convert.ToHexString(SHA256.HashData(new byte[] { 0x66 })),
                "the source stays in place and the concurrently created destination is not overwritten");
            var partialCancel = MoveCase("partial-cancel-after-first");
            var cancelledEntries = partialCancel.GetProperty("result").GetProperty("entries").EnumerateArray()
                .ToDictionary(entry => entry.GetProperty("RelativePath").GetString()!, entry => entry.GetProperty("status").GetString()!, StringComparer.Ordinal);
            check("Folder move GUI records partial cancellation", partialCancel.GetProperty("result").GetProperty("Cancelled").GetBoolean()
                && cancelledEntries.GetValueOrDefault("a.bin") == "Moved"
                && cancelledEntries.GetValueOrDefault("b.bin") == "Cancelled"
                && cancelledEntries.GetValueOrDefault("c.bin") == "NotExecuted"
                && FindMoveEntry(partialCancel.GetProperty("afterSource"), "a.bin").ValueKind == JsonValueKind.Undefined
                && FindMoveEntry(partialCancel.GetProperty("afterDestination"), "a.bin").ValueKind == JsonValueKind.Object
                && FindMoveEntry(partialCancel.GetProperty("afterSource"), "b.bin").ValueKind == JsonValueKind.Object
                && FindMoveEntry(partialCancel.GetProperty("afterSource"), "c.bin").ValueKind == JsonValueKind.Object,
                "first file remains moved, the cancelled and unexecuted files remain at source");
            if (OperatingSystem.IsWindows())
            {
                var alternateStream = MoveCase("preserve-alternate-stream");
                check("Folder move GUI retains alternate stream test result", MoveSucceeded(alternateStream, "a.bin"),
                    "Windows same-volume move passes the independent ADS assertion in the app self-check");
            }
            foreach (var direction in new[] { "LeftToRight", "RightToLeft", "LeftToMiddle", "MiddleToLeft", "MiddleToRight", "RightToMiddle" })
            {
                var threeWay = MoveCase("threeway-" + direction);
                var sourceSide = threeWay.GetProperty("sourceSide").GetString()!;
                var destinationSide = threeWay.GetProperty("destinationSide").GetString()!;
                var untouchedSide = new[] { "Left", "Middle", "Right" }.Single(side => side != sourceSide && side != destinationSide);
                check("Folder move GUI honors three-way direction " + direction, MoveSucceeded(threeWay, "a.bin")
                    && threeWay.GetProperty("result").GetProperty("MovedCount").GetInt32() == 1
                    && SideSnapshot(threeWay, untouchedSide, before: true).GetRawText()
                        == SideSnapshot(threeWay, untouchedSide, before: false).GetRawText(),
                    $"{sourceSide} moves to {destinationSide}; {untouchedSide} remains byte-for-byte unchanged");
            }

            JsonElement MoveCase(string id) => moveCases.SingleOrDefault(value => value.GetProperty("name").GetString() == id);
            JsonElement FindMoveEntry(JsonElement snapshot, string path) => snapshot.EnumerateArray()
                .FirstOrDefault(entry => entry.GetProperty("path").GetString() == path);
            bool MoveSucceeded(JsonElement item, params string[] paths)
            {
                if (item.ValueKind != JsonValueKind.Object || !item.GetProperty("confirmed").GetBoolean()) return false;
                var result = item.GetProperty("result");
                if (result.ValueKind != JsonValueKind.Object || !result.GetProperty("Succeeded").GetBoolean()
                    || result.GetProperty("MovedCount").GetInt32() != paths.Length) return false;
                return paths.All(path =>
                {
                    var beforeSource = FindMoveEntry(item.GetProperty("beforeSource"), path);
                    return beforeSource.ValueKind == JsonValueKind.Object
                        && FindMoveEntry(item.GetProperty("afterSource"), path).ValueKind == JsonValueKind.Undefined
                        && FindMoveEntry(item.GetProperty("beforeDestination"), path).ValueKind == JsonValueKind.Undefined
                        && FindMoveEntry(item.GetProperty("afterDestination"), path).ValueKind == JsonValueKind.Object
                        && FindMoveEntry(item.GetProperty("afterDestination"), path).GetProperty("sha256").GetString()
                            == beforeSource.GetProperty("sha256").GetString();
                });
            }
            static JsonElement SideSnapshot(JsonElement item, string side, bool before)
            {
                var name = (before ? "before" : "after") + side;
                return item.GetProperty(name);
            }
            static bool SameMoveSnapshot(JsonElement first, JsonElement second) => first.GetRawText() == second.GetRawText();
            }
        }

        var trashReports = Directory.GetFiles(Path.Combine(work, "gui"), "folder-trash-observations.json", SearchOption.AllDirectories);
        check("Folder trash GUI observation report", trashReports.Length == 1, "report count=" + trashReports.Length);
        if (trashReports.Length == 1)
        {
            using var trashDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(trashReports[0]));
            var trashRoot = trashDocument.RootElement;
            var trashCases = trashRoot.GetProperty("cases").EnumerateArray().ToArray();
            check("Folder trash GUI report complete", trashRoot.GetProperty("complete").GetBoolean()
                && trashCases.Length == 7, $"case count={trashCases.Length}, expected=7");
            if (trashRoot.GetProperty("complete").GetBoolean() && trashCases.Length == 7)
            {
                var selectedDirectory = TrashCase("selected-directory-moves-differing-files-only");
                var selectedDirectoryResult = selectedDirectory.GetProperty("result");
                var selectedDirectorySource = FindTrashEntry(selectedDirectory.GetProperty("afterSource"), "tree/leaf.bin");
                var selectedDirectoryPlan = selectedDirectory.GetProperty("planEntries").EnumerateArray().ToArray();
                check("Folder trash removes changed files but retains their directory", selectedDirectory.GetProperty("confirmed").GetBoolean()
                    && selectedDirectoryResult.GetProperty("Succeeded").GetBoolean()
                    && selectedDirectoryResult.GetProperty("TrashedCount").GetInt32() == 1
                    && selectedDirectoryPlan.Length == 1 && selectedDirectoryPlan[0].GetProperty("RelativePath").GetString() == "tree/leaf.bin"
                    && FindTrashEntry(selectedDirectory.GetProperty("afterSource"), "tree").GetProperty("directory").GetBoolean()
                    && selectedDirectorySource.ValueKind == JsonValueKind.Undefined
                    && SameTrashSnapshot(selectedDirectory.GetProperty("beforeOther"), selectedDirectory.GetProperty("afterOther")),
                    "only the differing child file moves to the OS trash; the selected directory and other side remain");

                var reverseTrash = TrashCase("right-to-left");
                check("Folder trash supports right to left", TrashSucceeded(reverseTrash, "a.bin")
                    && reverseTrash.GetProperty("sourceSide").GetString() == "Right"
                    && SameTrashSnapshot(reverseTrash.GetProperty("beforeOther"), reverseTrash.GetProperty("afterOther")),
                    "the selected right-side file moves to trash while the left side remains unchanged");

                var directoryTrash = TrashCase("unsupported-empty-directory");
                check("Folder trash never deletes directories", !directoryTrash.GetProperty("confirmed").GetBoolean()
                    && directoryTrash.GetProperty("result").ValueKind == JsonValueKind.Null
                    && directoryTrash.GetProperty("planEntries")[0].GetProperty("IsSupported").GetBoolean() == false
                    && FindTrashEntry(directoryTrash.GetProperty("afterSource"), "tree").GetProperty("directory").GetBoolean(),
                    "empty directories stay in place and do not reach the OS trash API");

                var protectedInput = TrashCase("shared-input-protected");
                check("Folder trash protects another comparison input", !protectedInput.GetProperty("confirmed").GetBoolean()
                    && protectedInput.GetProperty("result").ValueKind == JsonValueKind.Null
                    && SameTrashSnapshot(protectedInput.GetProperty("beforeSource"), protectedInput.GetProperty("afterSource")),
                    "shared inputs are rejected before confirmation and remain unchanged");

                var changedSource = TrashCase("source-content-changed-after-confirmation");
                check("Folder trash checks content immediately before deleting", changedSource.GetProperty("confirmed").GetBoolean()
                    && !changedSource.GetProperty("result").GetProperty("MutationOccurred").GetBoolean()
                    && FindTrashEntry(changedSource.GetProperty("afterSource"), "a.bin").GetProperty("sha256").GetString()
                        == Convert.ToHexString(SHA256.HashData(new byte[] { 0x99 }))
                    && SameTrashSnapshot(changedSource.GetProperty("beforeOther"), changedSource.GetProperty("afterOther")),
                    "the changed source stays present and the other side remains unchanged");

                var partialTrash = TrashCase("partial-cancel-after-first");
                var partialTrashEntries = partialTrash.GetProperty("result").GetProperty("entries").EnumerateArray()
                    .ToDictionary(entry => entry.GetProperty("RelativePath").GetString()!, entry => entry.GetProperty("status").GetString()!, StringComparer.Ordinal);
                check("Folder trash records partial cancellation", partialTrash.GetProperty("result").GetProperty("Cancelled").GetBoolean()
                    && partialTrashEntries.GetValueOrDefault("a.bin") == "Trashed"
                    && partialTrashEntries.GetValueOrDefault("b.bin") == "Cancelled"
                    && partialTrashEntries.GetValueOrDefault("c.bin") == "NotExecuted"
                    && FindTrashEntry(partialTrash.GetProperty("afterSource"), "a.bin").ValueKind == JsonValueKind.Undefined
                    && FindTrashEntry(partialTrash.GetProperty("afterSource"), "b.bin").ValueKind == JsonValueKind.Object
                    && FindTrashEntry(partialTrash.GetProperty("afterSource"), "c.bin").ValueKind == JsonValueKind.Object,
                    "only the first file reaches trash; cancelled and pending files stay in the source folder");

                var threeWayTrash = TrashCase("threeway-left-to-middle");
                check("Folder trash honors three-way source selection", TrashSucceeded(threeWayTrash, "a.bin")
                    && threeWayTrash.GetProperty("sourceSide").GetString() == "Left"
                    && FindTrashEntry(threeWayTrash.GetProperty("afterMiddle"), "a.bin").ValueKind == JsonValueKind.Undefined
                    && SameTrashSnapshot(threeWayTrash.GetProperty("beforeRight"), threeWayTrash.GetProperty("afterRight")),
                    "the selected left-side difference is removed; the middle and right sides remain unchanged");
            }

            JsonElement TrashCase(string id) => trashCases.SingleOrDefault(value => value.GetProperty("name").GetString() == id);
            static JsonElement FindTrashEntry(JsonElement snapshot, string path) => snapshot.EnumerateArray()
                .FirstOrDefault(entry => entry.GetProperty("path").GetString() == path);
            static bool SameTrashSnapshot(JsonElement before, JsonElement after) => before.GetRawText() == after.GetRawText();
            static bool TrashSucceeded(JsonElement item, string path)
            {
                if (item.ValueKind != JsonValueKind.Object || !item.GetProperty("confirmed").GetBoolean()) return false;
                var result = item.GetProperty("result");
                return result.ValueKind == JsonValueKind.Object && result.GetProperty("Succeeded").GetBoolean()
                    && result.GetProperty("MutationOccurred").GetBoolean() && result.GetProperty("TrashedCount").GetInt32() == 1
                    && FindTrashEntry(item.GetProperty("afterSource"), path).ValueKind == JsonValueKind.Undefined;
            }
        }

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
        await WindowsMetadata();
        await FolderWindowsMetadataScenarios.RunAsync(work, fixture, run, check, skip, python);
        if (!OperatingSystem.IsMacOS()) skip("Folder copy Mac FIFO/socket actual app", "Mac lstat/非regularの実OS拒否とCSDK ABIはMac RID実検証工程。");
        if (!OperatingSystem.IsWindows()) skip("Folder copy Windows compressed sparse actual app", "NTFS圧縮/Sparse mainstream bytesはWindows実測工程。ADS/EFS完全保持ではありません。");


        async Task WindowsMetadata()
        {
            if (!OperatingSystem.IsWindows() || !new DriveInfo(Path.GetPathRoot(Path.GetFullPath(work))!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                skip("Folder Windows metadata rejection GUI", "Windows NTFSでの実GUI・Win32 handle照合専用。");
                return;
            }
            var reports = Directory.GetFiles(Path.Combine(work, "gui"), "folder-windows-metadata-observations.json", SearchOption.AllDirectories);
            check("Folder Windows metadata GUI exact producer", reports.Length == 1, "観測JSON数=" + reports.Length);
            if (reports.Length != 1) return;
            var reader = Path.Combine(fixture, "verify-metadata.py");
            using var provenance = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "copy-provenance.json")));
            var expectedSha = provenance.RootElement.GetProperty("fixedFiles").GetProperty("verify-metadata.py").GetString();
            var readerSha = Hash(reader);
            check("Folder Windows metadata fixed reader", readerSha == expectedSha, readerSha);
            if (readerSha != expectedSha) return;
            var diagnostics = Path.Combine(Path.GetDirectoryName(reports[0])!, "folder-windows-metadata-diagnostic-observations.json");
            var inputSha = Hash(reports[0]); var diagnosticsSha = Hash(diagnostics);
            var proof = Path.Combine(work, "windows-metadata-independent.json");
            var readerInfo = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-B", "-X", "utf8", reader, reports[0], proof }) readerInfo.ArgumentList.Add(argument);
            using var readerProcess = Process.Start(readerInfo)!;
            var creationUtc = readerProcess.StartTime.ToUniversalTime();
            var readerStdout = readerProcess.StandardOutput.ReadToEndAsync();
            var readerStderr = readerProcess.StandardError.ReadToEndAsync();
            await readerProcess.WaitForExitAsync();
            var readerText = await readerStdout; var readerError = await readerStderr;
            var readerUnchanged = Hash(reader) == readerSha;
            var inputsUnchanged = Hash(reports[0]) == inputSha && Hash(diagnostics) == diagnosticsSha;
            await File.WriteAllTextAsync(Path.Combine(work, "windows-metadata-independent.stdout.json"), readerText, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(work, "windows-metadata-independent.stderr.txt"), readerError, new UTF8Encoding(false));
            await WriteJson(work, "windows-metadata-independent-process.json", new
            {
                pid = readerProcess.Id, creationUtc, exitObservedUtc = DateTime.UtcNow, actualExit = readerProcess.ExitCode,
                waitForExitComplete = true, readerSha256 = readerSha, inputSha256 = inputSha, diagnosticsInputSha256 = diagnosticsSha,
                readerUnchanged, inputsUnchanged
            });
            check("Folder Windows metadata independent literals creation and checked closes", readerProcess.ExitCode == 0 && readerUnchanged && inputsUnchanged,
                readerText + readerError + $" reader保持={readerUnchanged} inputs保持={inputsUnchanged}");
        }

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
        async Task VerifyGuiListDependencies()
        {
            var reader = Path.Combine(work, "verify-gui-list-dependencies.py");
            await File.WriteAllTextAsync(reader, """
import hashlib,json,pathlib,struct,sys
root=pathlib.Path(sys.argv[1]); reports=list(root.rglob('folder-list-dependency-observations.json'))
assert len(reports)==1, reports
data=json.loads(reports[0].read_text(encoding='utf-8')); assert data['complete']
rows={r['id']:r for r in data['cases']}; expected={'copy','move','stale','cancel'}
if sys.platform in ('win32','darwin'): expected.add('trash')
assert set(rows)==expected,(set(rows),expected)
for name,row in rows.items():
 assert row['passed'],row
 target=pathlib.Path(row['target']); raw=target.read_bytes()
 assert hashlib.sha256(raw).hexdigest().upper()==row['afterSha256'],row
 assert row['beforeWriteTicks']==row['afterWriteTicks'],row
 assert (row['beforeSha256']!=row['afterSha256'])==(name=='stale'),row
 assert row['injected']==(name in ('stale','cancel')),row
 if name=='stale': assert raw==b'a\tY\n'
 pngs=list(root.rglob('folder-list-'+name+'.png')); assert len(pngs)==1,pngs
 png=pngs[0].read_bytes(); assert png[:8]==b'\x89PNG\r\n\x1a\n'
 width,height=struct.unpack('>II',png[16:24]); assert width>=850 and height>=550
reports=list(root.rglob('folder-list-output-observations.json')); assert len(reports)==1,reports
extra=json.loads(reports[0].read_text(encoding='utf-8')); assert extra['complete']
outputs={r['id']:r for r in extra['cases']}
assert set(outputs)=={'html-initial','html-late','html-cancel','html-stale-candidate','package-late','workspace-initial','workspace-late','line-asset-missing','working-asset-missing','workspace-repeat','workspace-repeat-protected','workspace-queued-adoption'}
for name,row in outputs.items():
 assert row['passed'],row
 target=pathlib.Path(row['target']); actual=hashlib.sha256(target.read_bytes()).hexdigest().upper() if target.exists() else None
 assert actual==row['afterSha256'],row
 positive=name in ('html-stale-candidate','workspace-repeat','workspace-queued-adoption')
 assert row['rejected']!=positive,row
 if positive: assert row['beforeSha256']!=row['afterSha256'],row
 else: assert row['beforeSha256']==row['afterSha256'] and row['beforeWriteTicks']==row['afterWriteTicks'],row
 assert row['hook']==(name not in ('html-initial','workspace-initial','workspace-repeat-protected')),row
 assert row['adopted']==(name not in ('html-cancel','html-stale-candidate','workspace-repeat')),row
 if name in ('html-cancel','html-stale-candidate'): assert row['dependencies']==row['initialDependencies'] and str(target) not in row['dependencies'],row
 for dependency in row['initialDependencies']: assert pathlib.Path(dependency).read_bytes()==b'a\tX\n',row
 if row['adopted']: assert str(target) in row['dependencies'],row
 if name=='html-stale-candidate': assert '<html' in target.read_text(encoding='utf-8').lower()
 if name in ('workspace-repeat','workspace-repeat-protected','workspace-queued-adoption'):
  parsed=json.loads(target.read_text(encoding='utf-8')); assert parsed['entries'] and 'folderListDependencies' not in target.read_text(encoding='utf-8')
 for snapshot in row['dependencySnapshots']:
  p=pathlib.Path(snapshot['path']); assert (hashlib.sha256(p.read_bytes()).hexdigest().upper() if p.exists() else None)==snapshot['sha256'],row
 if name=='workspace-queued-adoption':
  assert row['publishedBeforeQueuedAdoption'] and row['order']==['publisher-entered','adoption-queued','atomic-visible-before-adoption','adoption-complete'],row
  assert row['dependencySnapshots']==[{'path':str(target),'sha256':row['afterSha256']}],row
 elif row['hook'] and row['adopted']: assert row['order']==['publisher-entered','adoption-complete'],row
 if row['workspace']:
  workspace=pathlib.Path(row['workspace']); assert workspace.read_bytes()==b'KEEP WORKSPACE\n'
  assert row['workspaceBeforeSha256']==row['workspaceAfterSha256']==hashlib.sha256(workspace.read_bytes()).hexdigest().upper()
  raw=(target.parents[1]/'source.list').read_bytes() if name=='line-asset-missing' else b'working text\n'
  assert target==pathlib.Path(str(workspace)+'.assets')/(hashlib.sha256(raw).hexdigest().upper()+('.replace-list' if name=='line-asset-missing' else '.text')) and not target.exists()
 pngs=list(root.rglob('folder-list-output-'+name+'.png')); assert len(pngs)==1,pngs
 png=pngs[0].read_bytes(); assert png[:8]==b'\x89PNG\r\n\x1a\n' and struct.unpack('>II',png[16:24])[0]>=850
reports=list(root.rglob('folder-copy-publication-observations.json')); assert len(reports)==1,reports
atomic=json.loads(reports[0].read_text(encoding='utf-8')); assert atomic['complete']
copies={row['id']:row for row in atomic['cases']}
assert set(copies)=={'late-list','queued-list','cancel','failure','new-parent','empty-directory'},copies
for name,row in copies.items():
 assert row['passed'] and row['hooked'] and row['clean'],row
 source=pathlib.Path(row['source']); target=pathlib.Path(row['target']); accepted=pathlib.Path(row['accepted'])
 sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper() if p.is_file() else None
 assert sha(source)==row['sourceBeforeSha256']==row['sourceAfterSha256'],row
 assert sha(target)==row['afterSha256'],row
 assert accepted.read_bytes()==b'a\tOLD\n' and sha(accepted)==row['acceptedBeforeSha256']==row['acceptedAfterSha256'],row
 assert not list(target.parents[0].rglob('.diffbeacon-copy-*')) if target.parent.exists() else True,row
 positive=name=='queued-list'
 assert row['succeeded']==positive and row['publishedCount']==int(positive),row
 if positive:
  assert source.read_bytes()==target.read_bytes()==b'a\tNEW\n' and row['adopted'] and row['adoptedSha256']==row['afterSha256'],row
  assert row['order']==['publisher-entered','adoption-queued','atomic-published','adoption-complete'],row
  assert row['dependencies']==[str(target)],row
  if atomic['windows']: assert row['windowsMetadataVerified'],row
 else:
  assert row['beforeSha256']==row['afterSha256'] and row['beforeWriteTicks']==row['afterWriteTicks'],row
  assert row['guardedBeforeWriteTicks']==row['guardedAfterWriteTicks'],row
  if name=='new-parent': assert not pathlib.Path(row['guardedPath']).exists() and not target.exists(),row
  elif name=='empty-directory':
   assert source.is_dir() and target.is_dir() and not list(target.iterdir()),row
   assert target.stat().st_mtime_ns//100+621355968000000000==row['beforeWriteTicks'],row
   import datetime
   assert source.stat().st_mtime_ns==int(datetime.datetime(2002,3,4,5,6,7,tzinfo=datetime.timezone.utc).timestamp())*1000000000,row
  else: assert target.read_bytes()==b'a\tOLD\n' and source.read_bytes()==b'a\tNEW\n' and row['staged'],row
 if name=='late-list': assert row['adopted'] and row['adoptedSha256']==row['beforeSha256'] and row['dependencies']==[str(target)],row
 if name in ('cancel','failure'): assert not row['adopted'] and row['dependencies']==row['initialDependencies'],row
 if name=='cancel': assert row['cancelled'],row
 pngs=list(root.rglob('folder-publication-'+name+'.png')); assert len(pngs)==1,pngs
 png=pngs[0].read_bytes(); assert png[:8]==b'\x89PNG\r\n\x1a\n' and struct.unpack('>II',png[16:24])[0]>=850
print(json.dumps({'passed':True,'cases':len(rows),'pngs':len(rows),'outputProtectionCases':len(outputs),'outputProtectionPngs':len(outputs),'atomicCopyCases':len(copies),'sameSizeRestoredMtime':True,'cancellationDuringRead':True,'cancelledAndStaleDependenciesRetained':True,'workspaceSourceDependencyProtected':True}))
""", new UTF8Encoding(false));
            var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-B", "-X", "utf8", reader, Path.Combine(work, "gui") }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); var text = await stdout; var error = await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "gui-list-reader-result.json"), text, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(work, "gui-list-reader-stderr.txt"), error, new UTF8Encoding(false));
            check("Folder GUI replacement list independent reader", process.ExitCode == 0, text + error);
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

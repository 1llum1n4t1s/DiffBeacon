using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class ImageCopyScenarios
{
    // 期待値は無改変C++原本143case/935state。GUI接続は次単位。
    // 先行契約: artifacts/verification/image-copy-cli/failure-contract.md
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, Action<string, string> skip)
    {
        const string goldenHash = "A1645415624D84B3CB4A328148151A0FAE48B05412CF2E504589CD98C289D65D";
        var source = FindFixtures("ImageCopy", "winimerge-image-copy-golden.json");
        var golden = await File.ReadAllBytesAsync(Path.Combine(source, "winimerge-image-copy-golden.json"));
        var sha = Convert.ToHexString(SHA256.HashData(golden));
        check("image-copy-golden-sha256", sha == goldenHash, sha);
        if (sha != goldenHash) throw new InvalidDataException("画像copy goldenのbytesが変わっています。");
        await File.WriteAllBytesAsync(Path.Combine(output, "image-copy-golden.json"), golden);
        using var document = JsonDocument.Parse(golden); var root = document.RootElement;
        check("image-copy-source-revision", root.GetProperty("sourceRevision").GetString() == "da639cdfaeca87aaad0eaceec509afa11ad61421"
            && root.GetProperty("sourceSha256").GetString() == "956B69A5D76D6918CD6D3245B5DB5BFDEF902C82221E2539FFEA1B33996AE40B", root.GetProperty("sourceRevision").GetString()!);
        var folder = Path.Combine(fixtures, "image-copy"); Directory.CreateDirectory(folder);
        var observations = new List<object>(); var statesChecked = 0;
        var cases = root.GetProperty("cases").EnumerateArray().Select(item => item.Clone()).ToArray();
        var prepared = new Dictionary<string, (string[] Inputs, string Script)>(StringComparer.Ordinal);
        foreach (var item in cases)
        {
            var name = item.GetProperty("name").GetString()!;
            var input = new List<string>(); var pane = 0;
            foreach (var image in item.GetProperty("images").EnumerateArray())
            {
                var path = Path.Combine(folder, name + "-" + pane++ + ".png");
                WritePng(path, image.GetProperty("width").GetInt32(), image.GetProperty("height").GetInt32(), Convert.FromBase64String(image.GetProperty("bgraBase64").GetString()!));
                input.Add(path);
            }
            var script = Script(name, item.GetProperty("actions"), item);
            var before = input.Append(script).Select(Hash).ToArray();
            var result = await run("image-copy-" + name, 0, true, ["--image-copy", .. input, "--script", script]);
            using var actual = JsonDocument.Parse(result.Stdout);
            var expectedStates = item.GetProperty("expected").GetProperty("states"); var actualStates = actual.RootElement.GetProperty("states");
            check("image-copy-" + name + "-state-count", actualStates.GetArrayLength() == expectedStates.GetArrayLength(), actualStates.GetArrayLength().ToString(CultureInfo.InvariantCulture));
            for (var index = 0; index < expectedStates.GetArrayLength(); index++)
            {
                if (index >= actualStates.GetArrayLength()) break;
                check($"image-copy-{name}-state{index}-original", SameLegacyState(expectedStates[index], actualStates[index]),
                    "原本の全原画BGRA/寸法/SHA/分類/grid/履歴/dirty/savepointと照合");
                ValidatePixels(name + "-state" + index, index == 0 ? actualStates[index] : actualStates[index].GetProperty("state"));
                statesChecked++;
            }
            check("image-copy-" + name + "-inputs-script-preserved", input.Append(script).Select(Hash).SequenceEqual(before), string.Join(',', before));
            observations.Add(new { name, inputs = input, script, inputSha256 = before, actual = actual.RootElement.Clone() });
            prepared.Add(name, (input.ToArray(), script));
        }
        check("image-copy-case-count", observations.Count == 143, observations.Count.ToString(CultureInfo.InvariantCulture));
        check("image-copy-state-count", statesChecked == 935, statesChecked.ToString(CultureInfo.InvariantCulture));
        await File.WriteAllTextAsync(Path.Combine(output, "image-copy-observations.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));

        // exportは静止原画の保存。golden末尾stateのBGRAだけを独立復号期待にする。
        foreach (var caseName in new[] { "2-transparent-all-0to1", "2-dimensions-all-1to0", "3-ring-hole-all-0to1", "3-history-two-expansions" })
        {
            var item = cases.Single(value => value.GetProperty("name").GetString() == caseName);
            var input = prepared[caseName].Inputs; var expectedStates = item.GetProperty("expected").GetProperty("states");
            var finalState = expectedStates[expectedStates.GetArrayLength() - 1].GetProperty("state");
            var exportPath = Path.Combine(folder, caseName + "-export.png");
            var dst = caseName == "2-transparent-all-0to1" || caseName == "3-ring-hole-all-0to1" ? 1 : 0;
            var actions = item.GetProperty("actions").EnumerateArray().Select(action => (object)action.Clone()).ToList();
            // 相対パスはrunnerと同じCWDで解決されることも実経路で確認。
            actions.Add(new { kind = "export", dst, path = caseName == "2-transparent-all-0to1" ? Path.GetRelativePath(Environment.CurrentDirectory, exportPath) : exportPath });
            var script = Script(caseName + "-export", actions, item);
            var before = input.Append(script).Select(Hash).ToArray(); var attrs = input.Select(File.GetAttributes).ToArray();
            await File.WriteAllTextAsync(exportPath, "existing PNG output must be atomically replaced");
            if (OperatingSystem.IsWindows()) File.SetAttributes(exportPath, FileAttributes.Hidden | FileAttributes.Archive);
            else File.SetUnixFileMode(exportPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try
            {
                for (var i = 0; i < input.Length; i++) File.SetAttributes(input[i], attrs[i] | FileAttributes.ReadOnly);
                var result = await run("image-copy-" + caseName + "-export", 0, true, ["--image-copy", .. input, "--script", script]);
                check("image-copy-" + caseName + "-export-exists", File.Exists(exportPath), exportPath);
                if (File.Exists(exportPath))
                {
                    if (OperatingSystem.IsWindows())
                        check("image-copy-" + caseName + "-export-attributes", (File.GetAttributes(exportPath) & (FileAttributes.Hidden | FileAttributes.Archive)) == (FileAttributes.Hidden | FileAttributes.Archive), File.GetAttributes(exportPath).ToString());
                    else
                        check("image-copy-" + caseName + "-export-unix-mode", File.GetUnixFileMode(exportPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), File.GetUnixFileMode(exportPath).ToString());
                    var decoded = ImageReportScenarios.DecodePng(await File.ReadAllBytesAsync(exportPath));
                    var expectedFrame = finalState.GetProperty("frames")[dst];
                    var pixels = Convert.FromBase64String(expectedFrame.GetProperty("bgraBase64").GetString()!);
                    check("image-copy-" + caseName + "-export-raw-bgra", decoded.Width == expectedFrame.GetProperty("width").GetInt32()
                        && decoded.Height == expectedFrame.GetProperty("height").GetInt32() && decoded.Bgra.SequenceEqual(pixels), Convert.ToHexString(SHA256.HashData(decoded.Bgra)));
                    using var parsed = JsonDocument.Parse(result.Stdout); var returned = parsed.RootElement.GetProperty("states");
                    check("image-copy-" + caseName + "-export-state-count", returned.GetArrayLength() == expectedStates.GetArrayLength() + 1, returned.GetRawText().Length.ToString(CultureInfo.InvariantCulture));
                    var last = returned[returned.GetArrayLength() - 1]; var exported = last.GetProperty("state");
                    check("image-copy-" + caseName + "-export-result", last.GetProperty("actionResult").GetInt32() == 1, last.GetProperty("actionResult").GetRawText());
                    foreach (var field in new[] { "frames", "regionIds", "regions", "differenceCount", "conflictCount" })
                        check("image-copy-" + caseName + "-export-preserves-" + field, Same(finalState.GetProperty(field), exported.GetProperty(field)), field);
                    var history = exported.GetProperty("history"); var prior = finalState.GetProperty("history");
                    foreach (var field in new[] { "index", "count", "undoable", "redoable" })
                        check("image-copy-" + caseName + "-export-history-" + field, Same(prior.GetProperty(field), history.GetProperty(field)), field);
                    for (var pane = 0; pane < input.Length; pane++)
                    {
                        var expectedPane = prior.GetProperty("panes")[pane]; var actualPane = history.GetProperty("panes")[pane];
                        check("image-copy-" + caseName + "-export-history-pane" + pane, pane == dst
                            ? !actualPane.GetProperty("modified").GetBoolean() && actualPane.GetProperty("modcount").GetInt32() == expectedPane.GetProperty("modcount").GetInt32()
                                && actualPane.GetProperty("savepoint").GetInt32() == expectedPane.GetProperty("modcount").GetInt32()
                            : Same(expectedPane, actualPane), actualPane.GetRawText());
                    }
                    var reloadScript = Script(caseName + "-reload", Array.Empty<object>());
                    var reload = await run("image-copy-" + caseName + "-export-reload", 0, true, ["--image-copy", exportPath, exportPath, "--script", reloadScript]);
                    using var reread = JsonDocument.Parse(reload.Stdout); var initial = reread.RootElement.GetProperty("states")[0];
                    check("image-copy-" + caseName + "-reload-equal", initial.GetProperty("differenceCount").GetInt32() == 0, initial.GetRawText().Length.ToString(CultureInfo.InvariantCulture));
                    foreach (var frame in initial.GetProperty("frames").EnumerateArray())
                        check("image-copy-" + caseName + "-reload-bgra", Same(expectedFrame, frame), frame.GetProperty("sha256").GetString()!);
                    await File.WriteAllTextAsync(Path.Combine(output, "image-copy-" + caseName + "-export-observation.json"), JsonSerializer.Serialize(new { exportPath, expected = finalState.Clone(), actual = parsed.RootElement.Clone(), reloaded = reread.RootElement.Clone() }, new JsonSerializerOptions { WriteIndented = true }));
                }
                check("image-copy-" + caseName + "-export-inputs-script-preserved", input.Append(script).Select(Hash).SequenceEqual(before), string.Join(',', before));
                check("image-copy-" + caseName + "-export-readonly-inputs", input.All(path => File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)), string.Join(',', input));
            }
            finally
            {
                for (var i = 0; i < input.Length; i++) File.SetAttributes(input[i], attrs[i]);
                if (OperatingSystem.IsWindows() && File.Exists(exportPath)) File.SetAttributes(exportPath, FileAttributes.Normal);
            }
        }

        var safe = prepared["3-ring-hole-all-0to1"].Inputs; var baseline = safe.Select(Hash).ToArray();
        var undoExport = Path.Combine(folder, "undo-then-export.png");
        var undoScript = Script("undo-then-export", new object[] { new { kind = "all", src = 0, dst = 1 }, new { kind = "undo" }, new { kind = "export", dst = 1, path = undoExport } });
        var undoSaved = await run("image-copy-undo-then-export", 0, true, ["--image-copy", .. safe, "--script", undoScript]);
        using (var parsed = JsonDocument.Parse(undoSaved.Stdout))
        {
            var states = parsed.RootElement.GetProperty("states"); var state = states[states.GetArrayLength() - 1].GetProperty("state");
            var paneState = state.GetProperty("history").GetProperty("panes")[1];
            check("image-copy-undo-export-savepoint-current-index", state.GetProperty("history").GetProperty("index").GetInt32() == -1
                && paneState.GetProperty("modcount").GetInt32() == 1 && paneState.GetProperty("savepoint").GetInt32() == 0 && !paneState.GetProperty("modified").GetBoolean(), paneState.GetRawText());
            var exported = ImageReportScenarios.DecodePng(await File.ReadAllBytesAsync(undoExport));
            var original = states[0].GetProperty("frames")[1];
            check("image-copy-undo-export-original-raw", exported.Width == original.GetProperty("width").GetInt32() && exported.Height == original.GetProperty("height").GetInt32()
                && exported.Bgra.SequenceEqual(Convert.FromBase64String(original.GetProperty("bgraBase64").GetString()!)), Convert.ToHexString(SHA256.HashData(exported.Bgra)));
        }
        async Task Reject(string name, string script, string[]? input = null, string? preserved = null, bool hashesOnly = false, string? reason = null)
        {
            input ??= safe; var prior = input.Where(File.Exists).Append(script).Select(Hash).ToArray();
            var protectedHash = preserved is not null ? Hash(preserved) : null;
            var result = await run("image-copy-reject-" + name, 2, false, ["--image-copy", .. input, "--script", script, .. (hashesOnly ? new[] { "--hashes-only" } : Array.Empty<string>())]);
            check("image-copy-reject-" + name + "-no-success-json", string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            check("image-copy-reject-" + name + "-input-script-preserved", input.Where(File.Exists).Append(script).Select(Hash).SequenceEqual(prior), string.Join(',', prior));
            if (preserved is not null) check("image-copy-reject-" + name + "-output-preserved", Hash(preserved) == protectedHash, preserved);
            if (reason is not null) check("image-copy-reject-" + name + "-reason", result.Stderr.Contains(reason, StringComparison.Ordinal), result.Stderr);
        }
        foreach (var (name, content) in new[] {
            ("invalid-json", "{"), ("nonobject", "[]"), ("unknown-root", "{\"actions\":[],\"unknown\":1}"),
            ("duplicate-root", "{\"actions\":[],\"actions\":[]}"), ("actions-nonarray", "{\"actions\":{}}"),
            ("nonobject-action", "{\"actions\":[0]}"), ("unknown-kind", "{\"actions\":[{\"kind\":\"wat\"}]}"),
            ("unknown-action", "{\"actions\":[{\"kind\":\"undo\",\"unexpected\":1}]}"),
            ("duplicate-action", "{\"actions\":[{\"kind\":\"undo\",\"kind\":\"redo\"}]}"),
            ("missing-copy-src", "{\"actions\":[{\"kind\":\"copy\",\"dst\":1,\"index\":0}]}"),
            ("missing-copy-index", "{\"actions\":[{\"kind\":\"copy\",\"src\":0,\"dst\":1}]}"),
            ("missing-all-dst", "{\"actions\":[{\"kind\":\"all\",\"src\":0}]}"),
            ("missing-export-path", "{\"actions\":[{\"kind\":\"export\",\"dst\":1}]}"),
            ("readonly-count", "{\"readOnly\":[false],\"actions\":[]}"), ("readonly-type", "{\"readOnly\":[0,0,0],\"actions\":[]}"),
            ("block-zero", "{\"blockSize\":0,\"actions\":[]}"), ("block-large", "{\"blockSize\":257,\"actions\":[]}"),
            ("threshold-negative", "{\"threshold\":-1,\"actions\":[]}"), ("threshold-nonfinite", "{\"threshold\":1e309,\"actions\":[]}") })
            await Reject(name, Text(name + ".json", content));
        var noActions = Script("empty", Array.Empty<object>());
        var missingScript = await run("image-copy-reject-missing-script-option", 2, false, ["--image-copy", .. safe]);
        check("image-copy-reject-missing-script-option-no-json", string.IsNullOrWhiteSpace(missingScript.Stdout) && !string.IsNullOrWhiteSpace(missingScript.Stderr), missingScript.Stderr);
        foreach (var (name, options) in new[] { ("missing-value", new[] { "--script" }), ("unknown-option", new[] { "--script", noActions, "--unknown", "1" }),
            ("duplicate-script", new[] { "--script", noActions, "--script", noActions }) })
        {
            var result = await run("image-copy-reject-" + name, 2, false, ["--image-copy", .. safe, .. options]);
            check("image-copy-reject-" + name + "-no-json", string.IsNullOrWhiteSpace(result.Stdout) && !string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        }
        await Reject("one-input", noActions, [safe[0]]);
        await Reject("four-inputs", noActions, [.. safe, safe[0]]);
        var images = FindFixtures("Images", "expectations.json");
        await Reject("multi-frame", noActions, [Path.Combine(images, "same-first-left.gif"), safe[0]]);
        await Reject("canvas-limit", noActions, [Path.Combine(images, "canvas-wide.gif"), Path.Combine(images, "canvas-tall.gif")]);
        var overActions = Script("over-actions", Enumerable.Range(0, 65).Select(_ => new { kind = "undo" }).ToArray());
        await Reject("action-limit", overActions);
        var overScript = Text("over-script.json", "{\"actions\":[]}" + new string(' ', 1024 * 1024));
        await Reject("script-byte-limit", overScript);
        var large = Path.Combine(folder, "json-cap.png"); WritePng(large, 512, 512, new byte[512 * 512 * 4]);
        var budgetOutput = Text("budget-output.png", "protected pending export\n");
        var jsonActions = new List<object> { new { kind = "export", dst = 1, path = budgetOutput } };
        jsonActions.AddRange(Enumerable.Range(0, 63).Select(_ => (object)new { kind = "undo" }));
        var capped = Script("json-limit", jsonActions);
        await Reject("json-limit", capped, [large, large], budgetOutput, reason: "32 MiB");
        // skinny canvasはraw出力約22MiBだがblock256のbbox走査を繰返すと256Mを超える。
        var narrowA = Path.Combine(folder, "work-a.png"); var narrowB = Path.Combine(folder, "work-b.png");
        var narrowPixels = new byte[32000 * 4];
        for (var index = 0; index < narrowPixels.Length; index += 4) { narrowPixels[index + 2] = 255; narrowPixels[index + 3] = 255; }
        WritePng(narrowA, 1, 32000, new byte[32000 * 4]); WritePng(narrowB, 1, 32000, narrowPixels);
        var workActions = new List<object> { new { kind = "export", dst = 1, path = budgetOutput } };
        workActions.AddRange(Enumerable.Range(0, 31).SelectMany(_ => new object[] { new { kind = "all", src = 0, dst = 1 }, new { kind = "undo" } }));
        workActions.Add(new { kind = "all", src = 0, dst = 1 });
        var workScript = Text("work-limit.json", JsonSerializer.Serialize(new { blockSize = 256, actions = workActions }));
        await Reject("cumulative-work", workScript, [narrowA, narrowB], budgetOutput, reason: "256M");
        // hashes-onlyでJSON上限を避け、原本の無変更all履歴を使って256MiBへ到達。
        var historyImage = Path.Combine(folder, "history-cap.png"); WritePng(historyImage, 2000, 2000, new byte[2000 * 2000 * 4]);
        var historyActions = new List<object> { new { kind = "export", dst = 1, path = budgetOutput } };
        historyActions.AddRange(Enumerable.Range(0, 9).Select(_ => (object)new { kind = "all", src = 0, dst = 1 }));
        await Reject("history-bytes", Script("history-limit", historyActions), [historyImage, historyImage], budgetOutput, hashesOnly: true, reason: "256 MiB");
        foreach (var dst in new[] { -1, 3 }) await Reject("export-dst-" + dst, Script("export-dst-" + dst, new[] { new { kind = "export", dst, path = Path.Combine(folder, "never-created-" + dst + ".png") } }));
        var protectedOutput = Text("protected-export.png", "protected output\n");
        foreach (var pane in Enumerable.Range(0, safe.Length))
            await Reject("export-input-" + pane, Script("export-input-" + pane, new[] { new { kind = "export", dst = 1, path = safe[pane] } }), preserved: safe[pane]);
        // scriptもPNG拡張子を許すため、拡張子拒否に隠れずscript自身の保護を確認。
        var selfScript = Path.Combine(folder, "export-self-script.png");
        await File.WriteAllTextAsync(selfScript, JsonSerializer.Serialize(new { actions = new[] { new { kind = "export", dst = 1, path = selfScript } } }));
        await Reject("export-script", selfScript, preserved: selfScript);
        await Reject("export-readonly-pane", Script("export-readonly-pane", new[] { new { kind = "export", dst = 1, path = protectedOutput } }, readOnly: [false, true, false]), preserved: protectedOutput);
        var outputAttributes = File.GetAttributes(protectedOutput);
        try
        {
            File.SetAttributes(protectedOutput, outputAttributes | FileAttributes.ReadOnly);
            await Reject("export-readonly-output", Script("export-readonly-output", new[] { new { kind = "export", dst = 1, path = protectedOutput } }), preserved: protectedOutput);
            check("image-copy-readonly-output-attributes", File.GetAttributes(protectedOutput).HasFlag(FileAttributes.ReadOnly), protectedOutput);
        }
        finally { File.SetAttributes(protectedOutput, outputAttributes); }
        var linked = Path.Combine(folder, "linked-export.png"); var linkCreated = false;
        try
        {
            try { File.CreateSymbolicLink(linked, protectedOutput); linkCreated = true; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            { Text("link-unverified.txt", exception.ToString()); skip("image-copy OS symbolic-link output protection", exception.Message); }
            if (linkCreated)
            {
                await Reject("export-link", Script("export-link", new[] { new { kind = "export", dst = 1, path = linked } }), preserved: protectedOutput);
                check("image-copy-reject-export-link-preserved", File.GetAttributes(linked).HasFlag(FileAttributes.ReparsePoint), linked);
            }
        }
        finally { if (linkCreated) File.Delete(linked); }
        check("image-copy-rejection-baseline-inputs", safe.Select(Hash).SequenceEqual(baseline), string.Join(',', baseline));

        string Text(string name, string value) { var path = Path.Combine(folder, name); File.WriteAllText(path, value); return path; }
        string Script(string name, object actions, JsonElement? item = null, bool[]? readOnly = null)
        {
            var script = new Dictionary<string, object?> { ["actions"] = actions };
            if (item.HasValue) { script["blockSize"] = item.Value.GetProperty("blockSize").GetInt32(); script["threshold"] = item.Value.GetProperty("threshold").GetDouble(); script["readOnly"] = item.Value.GetProperty("readOnly").Clone(); }
            if (readOnly is not null) script["readOnly"] = readOnly;
            return Text(name + "-script.json", JsonSerializer.Serialize(script));
        }
        void ValidatePixels(string name, JsonElement state)
        {
            var pane = 0;
            foreach (var frame in state.GetProperty("frames").EnumerateArray())
            {
                var raw = Convert.FromBase64String(frame.GetProperty("bgraBase64").GetString()!);
                check("image-copy-" + name + "-pane" + pane++ + "-sha-length", raw.LongLength == (long)frame.GetProperty("width").GetInt32() * frame.GetProperty("height").GetInt32() * 4
                    && string.Equals(Convert.ToHexString(SHA256.HashData(raw)), frame.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), frame.GetProperty("sha256").GetString()!);
            }
        }
    }
    private static bool SameLegacyState(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != JsonValueKind.Object || actual.ValueKind != JsonValueKind.Object) return false;
        if (expected.TryGetProperty("state", out _))
            return expected.EnumerateObject().Count() == actual.EnumerateObject().Count()
                && expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var value)
                    && (property.Name == "state" ? SameLegacyState(property.Value, value) : Same(property.Value, value, property.Name)));
        // 旧原本にない表示設定だけを独立に検査し、既存の全property比較を維持する。
        if (!actual.TryGetProperty("orientations", out var orientations) || orientations.ValueKind != JsonValueKind.Array
            || orientations.GetArrayLength() != expected.GetProperty("frames").GetArrayLength()
            || orientations.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3
                || value.GetProperty("rotation").GetInt32() != 0 || value.GetProperty("flipHorizontal").GetBoolean() || value.GetProperty("flipVertical").GetBoolean())) return false;
        return expected.EnumerateObject().Count() + 1 == actual.EnumerateObject().Count()
            && expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var value) && Same(property.Value, value, property.Name));
    }

    private static bool Same(JsonElement expected, JsonElement actual, string field = "")
    {
        if (expected.ValueKind != actual.ValueKind) return false;
        if (expected.ValueKind == JsonValueKind.Object)
            return expected.EnumerateObject().Count() == actual.EnumerateObject().Count() && expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var value) && Same(property.Value, value, property.Name));
        if (expected.ValueKind == JsonValueKind.Array)
            return expected.GetArrayLength() == actual.GetArrayLength() && expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => Same(pair.First, pair.Second));
        if (field == "sha256" && expected.ValueKind == JsonValueKind.String) return string.Equals(expected.GetString(), actual.GetString(), StringComparison.OrdinalIgnoreCase);
        return JsonElement.DeepEquals(expected, actual);
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static string FindFixtures(string name, string marker)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "Fixtures", name);
            if (File.Exists(Path.Combine(path, marker))) return path;
        }
        throw new FileNotFoundException("Image fixtures were not found: " + name);
    }

    // BCLだけでraw BGRAをPNG RGBAへ包装。差分判定は行わない。
    private static void WritePng(string path, int width, int height, byte[] bgra)
    {
        if (bgra.Length != checked(width * height * 4)) throw new InvalidDataException("Fixture pixel length mismatch.");
        using var file = File.Create(path); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6; Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            var row = new byte[checked(width * 4 + 1)];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var input = (y * width + x) * 4; var target = x * 4 + 1;
                    row[target] = bgra[input + 2]; row[target + 1] = bgra[input + 1];
                    row[target + 2] = bgra[input]; row[target + 3] = bgra[input + 3];
                }
                zlib.Write(row);
            }
        }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        void Chunk(string name, byte[] data)
        {
            Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); file.Write(number);
            var type = System.Text.Encoding.ASCII.GetBytes(name); file.Write(type); file.Write(data);
            uint crc = 0xffffffff;
            foreach (var value in type.Concat(data))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
            }
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); file.Write(number);
        }
    }
}

using System.Diagnostics;
using System.Text.Json;

internal static class BinaryClipboardScenarios
{
    internal static async Task RunAsync(string fixtures, Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, string python)
    {
        var work = Path.Combine(fixtures, "binary-clipboard"); Directory.CreateDirectory(work);
        var root = Path.GetFullPath("tests/Fixtures/BinaryClipboard"); var outputs = Path.Combine(work, "codec"); Directory.CreateDirectory(outputs);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));
        foreach (var item in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            await run("binary-clipboard-" + id, 0, true, ["--binary-bytecode-self-test", item.GetProperty("operation").GetString()!, item.GetProperty("endian").GetString()!, Path.Combine(root, item.GetProperty("inputFile").GetString()!), Path.Combine(outputs, id + ".bin")]);
        }
        foreach (var endian in new[] { "little", "big" }) foreach (var type in new[] { "f", "d" })
        {
            var id = "ieee-" + type + "-" + endian; var input = Path.Combine(work, id + ".txt"); File.WriteAllText(input, type == "f" ? "<fl:1.5>" : "<do:-2.25>");
            await run(id, 0, true, ["--binary-bytecode-self-test", "decode", endian, input, Path.Combine(outputs, id + ".bin")]);
        }
        await Reader("codec", Path.Combine(root, "verify.py"), [root, outputs]);
        var repeated = Path.Combine(work, "unclosed-repeat.txt"); File.WriteAllText(repeated, string.Concat(Enumerable.Repeat("<bh:", 262144))); var repeatedOutput = Path.Combine(work, "unclosed-repeat.bin");
        await run("binary-unclosed-repeat", 0, true, ["--binary-bytecode-self-test", "decode", "little", repeated, repeatedOutput]);
        check("Binary repeated unclosed full bytes", File.ReadAllBytes(repeated).SequenceEqual(File.ReadAllBytes(repeatedOutput)), "1048576 input/output bytes, bounded54 token lookahead, 2passes");
        var longNumeric = Path.Combine(work, "long-numeric.txt"); File.WriteAllText(longNumeric, "<bh:" + new string('1', 1048576)); var longOutput = Path.Combine(work, "long-numeric.bin"); File.WriteAllText(longOutput, "KEEP");
        await run("binary-long-numeric", 2, false, ["--binary-bytecode-self-test", "decode", "little", longNumeric, longOutput]);
        check("Binary long numeric preserves output", File.ReadAllText(longOutput) == "KEEP" && new FileInfo(longNumeric).Length == 1048580, "overlong numeric prefix rejected before allocation/publication");
        var oem = Path.Combine(work, "oem"); Directory.CreateDirectory(oem);
        using var oemReference = await Reader("oem-reference", Path.Combine(root, "oem_reference.py"), [oem]);
        if (oemReference is not null)
        {
            foreach (var item in oemReference.RootElement.GetProperty("cases").EnumerateArray())
                await run("binary-oem-" + item.GetProperty("name").GetString(), item.GetProperty("expectedExit").GetInt32(), item.GetProperty("expectedExit").GetInt32() == 0, ["--binary-bytecode-self-test", "decode-oem", "little", item.GetProperty("input").GetString()!, item.GetProperty("actual").GetString()!]);
            await Reader("oem", Path.Combine(root, "oem_verify.py"), [oem]);
        }
        var gui = Path.Combine(work, "gui"); await run("binary-clipboard-gui", 0, false, ["--self-test", gui, "--binary-clipboard-only"]);
        using var receipt = await Reader("gui", Path.Combine(root, "verify_ui.py"), [gui]);
        if (receipt is null) return;
        foreach (var name in new[] { "normal", "archive" })
        {
            var workspace = receipt.RootElement.GetProperty(name + "Workspace").GetString()!; var package = receipt.RootElement.GetProperty(name + "Package").GetString()!;
            await run("binary-clipboard-" + name + "-copy", 0, true, ["--project-copy", workspace, Path.Combine(work, name + "-copied.json")]);
            await run("binary-clipboard-" + name + "-package", 0, true, ["--package-project", workspace, Path.Combine(work, name + "-cli.zip")]);
            var extracted = Path.Combine(work, name + "-extracted"); await run("binary-clipboard-" + name + "-extract", 0, true, ["--archive-extract", package, extracted]);
            await run("binary-clipboard-" + name + "-reload", 0, true, ["--project-copy", Path.Combine(extracted, "project.json"), Path.Combine(work, name + "-reloaded.json")]);
        }
        await Reader("cli", Path.Combine(root, "verify_ui.py"), [gui, work]);
        async Task<JsonDocument?> Reader(string name, string script, string[] arguments)
        {
            var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }; foreach (var arg in new[] { "-B", "-X", "utf8", script }.Concat(arguments)) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); var text = await stdout; var error = await stderr;
            File.WriteAllText(Path.Combine(work, name + "-independent.stdout.txt"), text); File.WriteAllText(Path.Combine(work, name + "-independent.stderr.txt"), error);
            check("Binary clipboard independent full bytes " + name, process.ExitCode == 0, text + error); return process.ExitCode == 0 ? JsonDocument.Parse(text) : null;
        }
    }
}

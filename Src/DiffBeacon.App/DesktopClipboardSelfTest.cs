using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace DiffBeacon.App;

// 実OSのclipboard検証はクリーンなGitHub runnerに限定する。ローカル利用者のclipboardを操作しない。
internal static class DesktopClipboardSelfTest
{
    private static readonly byte[] SourcePixels =
    [
        1, 2, 3, 0, 33, 77, 221, 64, 9, 12, 201, 128,
        111, 88, 17, 255, 255, 254, 253, 1, 32, 24, 16, 255
    ];

    internal static int Run(string output, string phase)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_OS") is not ("Windows" or "macOS"))
        {
            Console.Error.WriteLine("実OSクリップボード検証はGitHubのWindows/macOS runnerだけで実行します。");
            return 2;
        }
        if (phase is not ("--write" or "--read" or "--binary-write" or "--binary-read"))
        {
            Console.Error.WriteLine("--clipboard-self-test OUTPUT --write|--read を指定してください。");
            return 2;
        }

        var binary = phase.StartsWith("--binary-", StringComparison.Ordinal); var writing = phase is "--write" or "--binary-write";
        var binaryBytes = Enumerable.Range(0, 256).Select(value => (byte)value).Concat(new byte[] { 0, 65, 255 }).ToArray();
        var destination = Path.GetFullPath(output);
        Directory.CreateDirectory(destination);
        var checks = new List<(string Name, bool Passed, string Detail)>();
        var mode = phase[2..];
        var report = Path.Combine(destination, mode + "-report.json");
        if (File.Exists(report))
        {
            Console.Error.WriteLine("完了済みOS検証のreportを上書きしません。");
            return 2;
        }
        Window? window = null;
        Task? operation = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var source = Path.Combine(destination, binary ? "source.bin" : "source.bgra"); var sourceBytes = binary ? binaryBytes : SourcePixels;
            var writerReport = Path.Combine(destination, binary ? "binary-write-report.json" : "write-report.json");
            if (writing)
            {
                using var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write);
                stream.Write(sourceBytes);
            }
            else
            {
                if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(sourceBytes))
                    throw new InvalidDataException("同じ採取先のwriter入力がありません。");
                if (!File.Exists(writerReport))
                    throw new InvalidDataException("writerの実行記録がありません。");
                using var previous = JsonDocument.Parse(File.ReadAllBytes(writerReport));
                Check("separate successful writer process", previous.RootElement.GetProperty("failed").GetInt32() == 0
                    && previous.RootElement.GetProperty("pid").GetInt32() != Environment.ProcessId);
            }

            Program.BuildAvaloniaApp().SetupWithoutStarting();
            window = new Window
            {
                Width = 32, Height = 32, ShowInTaskbar = false, ShowActivated = false, Opacity = 0,
                Position = new PixelPoint(-10000, -10000), Title = "DiffBeacon clipboard verification"
            };
            window.Show();
            var handle = window.TryGetPlatformHandle();
            Check("native window exists", handle is not null && handle.Handle != IntPtr.Zero, handle?.HandleDescriptor ?? "none");
            var ownedWindow = window;
            var clipboard = new ImageClipboard(() => ownedWindow.Clipboard, () => ownedWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
            Dispatcher.UIThread.Post(() => operation = binary ? VerifyBinaryAsync(ownedWindow, destination, stop.Token) : VerifyAsync(clipboard, ownedWindow, destination, phase, stop.Token));
            Dispatcher.UIThread.MainLoop(stop.Token);
            if (operation is null || !operation.IsCompleted)
                throw new TimeoutException("実OSクリップボード操作が60秒以内に完了しませんでした。");
            operation.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            checks.Add(("native clipboard execution", false, error.ToString()));
        }
        finally
        {
            window?.Close();
        }

        using (var file = new FileStream(report, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("phase", mode);
            writer.WriteString("scope", "Actual native desktop clipboard on clean GitHub runner; separate writer/reader processes; no headless clipboard");
            writer.WriteString("os", RuntimeInformation.OSDescription);
            writer.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
            writer.WriteNumber("pid", Environment.ProcessId);
            writer.WriteString("sourceSha256", Convert.ToHexString(SHA256.HashData(binary ? binaryBytes : SourcePixels)));
            writer.WriteString("processCreationUtc", System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime());
            writer.WriteNumber("passed", checks.Count(check => check.Passed));
            writer.WriteNumber("failed", checks.Count(check => !check.Passed));
            writer.WriteStartArray("assertions");
            foreach (var check in checks)
            {
                writer.WriteStartObject(); writer.WriteString("name", check.Name);
                writer.WriteBoolean("passed", check.Passed); writer.WriteString("detail", check.Detail); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        Console.WriteLine($"OS clipboard {mode}: passed={checks.Count(check => check.Passed)}, failed={checks.Count(check => !check.Passed)}");
        return checks.Any(check => !check.Passed) ? 2 : 0;

        void Check(string name, bool passed, string detail = "")
        {
            checks.Add((name, passed, detail));
            if (!passed) throw new InvalidDataException(name + ": " + detail);
        }
        async Task VerifyBinaryAsync(Window nativeWindow, string directory, CancellationToken token)
        {
            try
            {
                var binaryClipboard = new BinaryClipboard(() => nativeWindow.Clipboard, () => nativeWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
                var bytecode = DiffBeacon.Core.BinaryBytecode.Encode(binaryBytes);
                if (writing) { await binaryClipboard.WriteAsync(binaryBytes, bytecode, token); Check("native binary writer publication completed", true); }
                var formats = await binaryClipboard.ReadAsync(token);
                var rawName = OperatingSystem.IsWindows() ? BinaryClipboard.RawFormat.ToSystemName("avn-app-fmt:") : BinaryClipboard.RawFormat.Identifier;
                var raw = formats.SingleOrDefault(value => value.Name == rawName);
                Check("native raw binary full bytes including NUL", raw is not null && raw.Bytes.AsSpan().SequenceEqual(binaryBytes));
                var text = OperatingSystem.IsWindows() ? formats.SingleOrDefault(value => value.Name == "CF_TEXT") : formats.FirstOrDefault(value => value.Kind == BinaryClipboardKind.PlatformText);
                Check("native standard bytecode text full value", text is not null && text.PlainText() == bytecode);
                Check("native standard text decode all256", text is not null && text.Decode(false, false, false).AsSpan().SequenceEqual(binaryBytes));
                if (OperatingSystem.IsWindows()) Check("native CF_UNICODETEXT exact value", formats.SingleOrDefault(value => value.Name == "CF_UNICODETEXT")?.PlainText() == bytecode);
                await File.WriteAllBytesAsync(Path.Combine(directory, mode + "-clipboard.bin"), raw!.Bytes, token);
                await File.WriteAllTextAsync(Path.Combine(directory, mode + "-clipboard.txt"), text!.PlainText(), new System.Text.UTF8Encoding(false), token);
                File.WriteAllText(Path.Combine(directory, mode + "-formats.json"), "[" + string.Join(",", formats.Select(value => "\"" + value.Name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"")) + "]");
            }
            finally { stop.Cancel(); }
        }

        async Task VerifyAsync(ImageClipboard clipboard, Window nativeWindow, string directory, string action, CancellationToken token)
        {
            try
            {
                if (action == "--write")
                {
                    var frame = new ImageComparisonEngine.DecodedFrame(1, 3, 2, (byte[])SourcePixels.Clone());
                    await clipboard.WriteAsync(frame, token);
                    Check("native clipboard writer completed", true);
                }
                var actual = await clipboard.ReadAsync(token);
                Check("actual clipboard BGRA dimensions and all bytes", actual is { Width: 3, Height: 2 }
                    && actual.Pixels.AsSpan().SequenceEqual(SourcePixels));
                // 別processへ渡す元入力と、clipboardから所有した画素を成果物へ記録する。
                await File.WriteAllBytesAsync(Path.Combine(directory, action[2..] + "-clipboard.bgra"), actual!.Pixels, token);
                await ImagePngStore.SaveAsync(Path.Combine(directory, action[2..] + "-clipboard.png"), actual!, [], token);
                using var transfer = await nativeWindow.Clipboard!.TryGetDataAsync();
                Check("native platform data transfer exists", transfer is not null);
                var data = transfer ?? throw new InvalidDataException("OSのdata transferがありません。");
                var custom = await data.TryGetValueAsync(ImageClipboard.RawFormat);
                Check("native custom format exists", custom is not null);
                var bitmap = await data.TryGetBitmapAsync();
                Check("native standard bitmap exists and has source dimensions", bitmap?.PixelSize == new PixelSize(3, 2));
                if (OperatingSystem.IsWindows())
                {
                    var dib = await data.TryGetValueAsync(ImageClipboard.DibFormat);
                    Check("native CF_DIB exists", dib is not null);
                    Check("CF_DIB header is bottom-up BGRA32 without palette", IsBottomUpSource(dib!), dib is null ? "none" : $"bytes={dib.Length}");
                    await File.WriteAllBytesAsync(Path.Combine(directory, action[2..] + "-clipboard.dib"), dib!, token);
                }
            }
            finally
            {
                stop.Cancel();
            }
        }
    }

    // 原本writerと独立した固定寸法header/行順チェック。製品のDIB encoder/decoderを期待値に使わない。
    private static bool IsBottomUpSource(byte[] data)
    {
        if (data.Length != 64 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 40
            || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)) != 3
            || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)) != 2
            || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12)) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14)) != 32
            || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(32)) != 0) return false;
        for (var y = 0; y < 2; y++)
            if (!data.AsSpan(40 + (1 - y) * 12, 12).SequenceEqual(SourcePixels.AsSpan(y * 12, 12))) return false;
        return true;
    }
}

using System.Runtime.InteropServices;

namespace DiffBeacon.App;

/// <summary>
/// 画像の文字認識。Windows ではプロセス内の Windows.Media.Ocr、macOS では同梱の Vision シム、
/// どちらも使えないときは外部コマンドの JSON を使う。
/// </summary>
internal static class PlatformOcr
{
    /// <summary>認識する。プラットフォームの実装が無ければ外部コマンド、それも無ければ理由を出して失敗する。</summary>
    internal static async Task<string> RecognizeAsync(string imagePath, string? externalCommand, CancellationToken cancellationToken)
    {
#if DIFFBEACON_WINDOWS_OCR
        var windows = await WindowsMediaOcr.TryRecognizeAsync(imagePath);
        if (windows is not null) return windows;
#endif
        if (OperatingSystem.IsMacOS() && MacIntegration.TryRecognize(imagePath, out var text, out _)) return text;
        if (!string.IsNullOrWhiteSpace(externalCommand))
        {
            var stdout = await RunExternalAsync(externalCommand, imagePath, cancellationToken);
            return ExternalOcr.ReadResponse(stdout);
        }
        throw new InvalidOperationException(ExternalOcr.Availability);
    }

    private static async Task<string> RunExternalAsync(string command, string imagePath, CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo(command, imagePath)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("OCR コマンドを起動できません。");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException("OCR コマンドが失敗しました。");
        return output;
    }
}

/// <summary>macOS の libdiffbeacon-mac.dylib。発行物の横に無い開発実行では何もしない。</summary>
internal static class MacIntegration
{
    private const string Library = "diffbeacon-mac";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PathsCallback(IntPtr paths);

    private static PathsCallback? _serviceCallback;

    private static void OnServicePaths(IntPtr paths)
    {
        var text = Marshal.PtrToStringUTF8(paths);
        if (string.IsNullOrWhiteSpace(text)) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => PendingPaths = text);
    }

    /// <summary>サービスが渡したパス。ウィンドウが次の比較に使う。</summary>
    internal static string? PendingPaths { get; private set; }

    [DllImport(Library, EntryPoint = "db_install_services")]
    private static extern void InstallServices(PathsCallback callback);

    [DllImport(Library, EntryPoint = "db_ocr_file")]
    private static extern int RecognizeFile(IntPtr path, IntPtr output, int outputLength, IntPtr error, int errorLength);

    /// <summary>Finder サービスを受け取る。ライブラリまたは NSApp が無ければ戻る。</summary>
    internal static bool TryInstallServices()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            _serviceCallback ??= OnServicePaths;
            InstallServices(_serviceCallback);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>Vision で読む。シムが無ければ false。</summary>
    internal static bool TryRecognize(string imagePath, out string text, out string error)
    {
        text = "";
        error = "";
        if (!OperatingSystem.IsMacOS()) return false;
        var path = Marshal.StringToCoTaskMemUTF8(imagePath);
        var output = Marshal.AllocHGlobal(64 * 1024);
        var errors = Marshal.AllocHGlobal(1024);
        try
        {
            var code = RecognizeFile(path, output, 64 * 1024, errors, 1024);
            text = Marshal.PtrToStringUTF8(output) ?? "";
            error = Marshal.PtrToStringUTF8(errors) ?? "";
            return code == 0;
        }
        catch (DllNotFoundException) { return false; }
        finally
        {
            Marshal.FreeCoTaskMem(path);
            Marshal.FreeHGlobal(output);
            Marshal.FreeHGlobal(errors);
        }
    }
}

#if DIFFBEACON_WINDOWS_OCR
/// <summary>Windows.Media.Ocr。言語パックが無いときは null。</summary>
internal static class WindowsMediaOcr
{
    internal static async Task<string?> TryRecognizeAsync(string imagePath)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return null;
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(imagePath);
        using var stream = await file.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var bitmap = await decoder.GetSoftwareBitmapAsync();
        var result = await engine.RecognizeAsync(bitmap);
        return result.Text;
    }
}
#endif

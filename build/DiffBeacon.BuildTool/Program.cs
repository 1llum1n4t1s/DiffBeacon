using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffBeacon.BuildTool;

/// <summary>
/// 発行と検証用の参照ビルド。CI が PowerShell を呼ばずに使う入口。
/// </summary>
internal static class Program
{
    /// <summary>コマンドの終了コード。</summary>
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new InvalidOperationException("publish、z-reference、mac-reference、manifest-executable のいずれかを指定してください。");
            return args[0] switch
            {
                "publish" => Publish(args[1..]),
                "z-reference" => BuildZReference(args[1..]),
                "mac-reference" => BuildMacReference(args[1..]),
                "manifest-executable" => PrintExecutable(args[1..]),
                _ => throw new InvalidOperationException($"不明なコマンドです: {args[0]}"),
            };
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    /// <summary>Native AOT を発行し、同じ OS なら自己検証して manifest を書く。</summary>
    private static int Publish(string[] args)
    {
        var rid = Required(args, "--rid");
        var outputRoot = Path.GetFullPath(Required(args, "--output"));
        var artifacts = Path.GetFullPath(Required(args, "--artifacts"));
        var timeout = int.Parse(Optional(args, "--timeout") ?? "600");
        var skipCovered = args.Contains("--skip-e2e-covered");
        var skipVerification = args.Contains("--skip-verification");
        var repo = RepositoryRoot();
        EnsureHostCanPublish(rid);
        EnsureWorkPath(outputRoot, repo);
        EnsureWorkPath(artifacts, repo);
        if (IsSameOrNested(outputRoot, artifacts) || IsSameOrNested(artifacts, outputRoot))
            throw new InvalidOperationException("中間出力先は発行出力と分けてください。");
        var output = Path.Combine(outputRoot, rid);
        EnsureNoReparse(output);
        if (Directory.Exists(output))
        {
            if (OperatingSystem.IsWindows())
                throw new InvalidOperationException("既存の発行先は削除しません。未使用の --output を指定してください: " + output);
            Directory.Delete(output, recursive: true);
        }
        Directory.CreateDirectory(output);
        var project = Path.Combine(repo, "Src", "DiffBeacon.App", "DiffBeacon.App.csproj");
        var publishArgs = new List<string> { "publish", project, "-c", "Release", "-r", rid, "-p:PublishAot=true", "--self-contained", "true", "-o", output, "--artifacts-path", artifacts, "-noAutoResponse" };
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OS"))) publishArgs.Add("-p:OS=Windows_NT");
        var sdk = Run("dotnet", ["--version"], repo).Trim();
        Run("dotnet", publishArgs, repo, stream: true);
        var executableName = OperatingSystem.IsWindows() ? "DiffBeacon.exe" : "DiffBeacon";
        var executable = Path.Combine(output, executableName);
        if (!File.Exists(executable)) throw new InvalidOperationException("ネイティブ実行ファイルが見つかりません: " + executable);
        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            CompileMacShim(repo, output);
            executable = BundleMacApp(output, executable, project, repo);
        }
        var status = "skipped by request";
        double? seconds = null;
        if (!skipVerification)
        {
            var hostArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
            var targetArch = rid.Split('-')[1];
            if (hostArch == targetArch || (hostArch == "x64" && targetArch == "x64"))
            {
                var verification = Path.Combine(outputRoot, "verification", rid);
                Directory.CreateDirectory(verification);
                if (skipCovered) Environment.SetEnvironmentVariable("DIFFBEACON_SELFTEST_SKIP_E2E_COVERED", "1");
                var timer = Stopwatch.StartNew();
                var code = RunAllowFail(executable, ["--self-test", verification], repo, TimeSpan.FromSeconds(timeout));
                timer.Stop();
                seconds = timer.Elapsed.TotalSeconds;
                if (code != 0) throw new InvalidOperationException($"ネイティブ自己検証に失敗しました: {code}");
                status = "passed";
            }
            else status = $"skipped: host {hostArch} differs from target {targetArch}";
        }
        var files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).Select(path => new PublishedFile(Path.GetRelativePath(output, path).Replace('\\', '/'), new FileInfo(path).Length, Hash(path))).ToArray();
        var manifest = new PublishManifest(rid, sdk, true, status, skipCovered ? "full without units covered by E2E selectors" : "full", timeout, seconds, Path.GetRelativePath(repo, executable), files);
        File.WriteAllText(Path.Combine(output, "publish-manifest.json"), JsonSerializer.Serialize(manifest, BuildJsonContext.Default.PublishManifest));
        Console.WriteLine("発行完了: " + output);
        return 0;
    }

    /// <summary>Vision と Finder サービス用の Objective-C シムを、発行物の横へ置く。</summary>
    private static void CompileMacShim(string repo, string output)
    {
        var source = Path.Combine(repo, "native", "macos", "DiffBeaconMac.m");
        var library = Path.Combine(output, "libdiffbeacon-mac.dylib");
        Run("clang", ["-fobjc-arc", "-dynamiclib", "-framework", "Foundation", "-framework", "AppKit", "-framework", "Vision", "-o", library, source], repo);
    }

    /// <summary>macOS では .app を組み、実行ファイルのパスを返す。</summary>
    private static string BundleMacApp(string output, string executable, string project, string repo)
    {
        var bundle = Path.Combine(output, "DiffBeacon.app");
        var macOs = Path.Combine(bundle, "Contents", "MacOS");
        Directory.CreateDirectory(macOs);
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
        foreach (var file in Directory.EnumerateFiles(output))
            File.Copy(file, Path.Combine(macOs, Path.GetFileName(file)), overwrite: true);
        // .app の実行ファイルは Contents/MacOS にあり、発行物の直下にある Translations は見えない。
        CopyTree(Path.Combine(output, "Translations"), Path.Combine(macOs, "Translations"));
        var version = Run("dotnet", ["msbuild", project, "-getProperty:Version", "-nologo", "-noAutoResponse"], repo).Trim();
        var plist = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\"><dict>\n  <key>CFBundleName</key><string>DiffBeacon</string>\n  <key>CFBundleDisplayName</key><string>DiffBeacon</string>\n  <key>CFBundleIdentifier</key><string>com.kagayoi.diffbeacon</string>\n  <key>CFBundleExecutable</key><string>DiffBeacon</string>\n  <key>CFBundlePackageType</key><string>APPL</string>\n  <key>CFBundleVersion</key><string>" + System.Security.SecurityElement.Escape(version) + "</string>\n  <key>CFBundleShortVersionString</key><string>" + System.Security.SecurityElement.Escape(version) + "</string>\n  <key>LSMinimumSystemVersion</key><string>14.0</string>\n  <key>NSHighResolutionCapable</key><true/>\n  <key>NSServices</key><array><dict>\n    <key>NSMenuItem</key><dict><key>default</key><string>DiffBeacon で比較</string></dict>\n    <key>NSMessage</key><string>diffBeaconCompare</string>\n    <key>NSSendTypes</key><array><string>NSFilenamesPboardType</string></array>\n    <key>NSPortName</key><string>DiffBeacon</string>\n  </dict></array>\n</dict></plist>\n";
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), plist);
        var bundled = Path.Combine(macOs, "DiffBeacon");
        Run("chmod", ["+x", bundled], output);
        Run("plutil", ["-lint", Path.Combine(bundle, "Contents", "Info.plist")], output);
        Run("tar", ["-czf", Path.Combine(output, "DiffBeacon.app.tar.gz"), "-C", output, "DiffBeacon.app"], output);
        return bundled;
    }

    /// <summary>ディレクトリを再帰的にコピーする。無いときは何もしない。</summary>
    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    /// <summary>公式 ncompress の参照デコーダをコンパイルする。</summary>
    private static int BuildZReference(string[] args)
    {
        var repo = RepositoryRoot();
        var output = Path.GetFullPath(Required(args, "--output"));
        EnsureWorkPath(output, repo);
        EnsureNoReparse(output);
        var sourceRoot = Path.Combine(repo, "tests", "Fixtures", "Archives", "TarZ", "reference-source");
        ExpectHash(Path.Combine(sourceRoot, "compress.c"), "29C5A78005921A7881D8C83EA711E826C8C87F354B4A2F47F8C474117F6646D8");
        ExpectHash(Path.Combine(sourceRoot, "patchlevel.h"), "458F2AF3AA2862B80E52D45F277FDA6D105A225DD0629ECA9153DD3B060B47A3");
        ExpectHash(Path.Combine(sourceRoot, "UNLICENSE"), "7E12E5DF4BAE12CB21581BA157CED20E1986A0508DD10D0E8A4AB9A4CF94E85C");
        Directory.CreateDirectory(output);
        var source = Path.Combine(sourceRoot, "compress.c");
        string compiler;
        var decoder = Path.Combine(output, OperatingSystem.IsWindows() ? "ncompress.exe" : "ncompress");
        IReadOnlyList<string> arguments;
        Dictionary<string, string>? environment = null;
        if (OperatingSystem.IsWindows())
        {
            compiler = FindMsvc(out environment);
            arguments = ["/nologo", "/O2", "/MT", "/D_CRT_SECURE_NO_WARNINGS", "/Fo" + Path.Combine(output, "compress.obj"), "/Fe" + decoder, source, "/link", "/INCREMENTAL:NO"];
        }
        else
        {
            compiler = FindOnPath("clang");
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x86_64";
            var common = new List<string> { "-O2", "-DUTIME_H", "-DLSTAT" };
            if (OperatingSystem.IsMacOS()) { common.Add("-arch"); common.Add(arch); }
            common.AddRange(["-o", decoder, source]);
            arguments = common;
        }
        var code = RunAllowFail(compiler, arguments, output, TimeSpan.FromMinutes(5), environment);
        if (code != 0) throw new InvalidOperationException("reference buildが失敗しました: " + output);
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var proof = new ZBuildProof(source, Hash(source), Hash(Path.Combine(sourceRoot, "patchlevel.h")), compiler, Hash(compiler),
            architecture, arguments.ToArray(), 0, decoder, "none; original ncompress 5.1 has upstream MSVC/binary stdio support", Hash(decoder));
        File.WriteAllText(Path.Combine(output, "build-proof.json"), JsonSerializer.Serialize(proof, BuildJsonContext.Default.ZBuildProof));
        Console.WriteLine(decoder);
        return 0;
    }

    /// <summary>macOS の stat ABI を測る参照プログラムをコンパイルして実行する。</summary>
    private static int BuildMacReference(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("macOS の SDK で実行してください。");
        var repo = RepositoryRoot();
        var output = Path.GetFullPath(Required(args, "--output"));
        EnsureWorkPath(output, repo);
        EnsureNoReparse(output);
        Directory.CreateDirectory(output);
        var source = Path.Combine(repo, "tests", "Fixtures", "FolderSync", "mac-file-kind-reference.c");
        var compiler = FindOnPath("clang");
        var reference = Path.Combine(output, "mac-file-kind-reference");
        Run(compiler, ["--version"], output);
        var sdk = Run("xcrun", ["--show-sdk-path"], output).Trim();
        var build = RunAllowFail(compiler, ["-std=c11", "-Wall", "-Wextra", "-Werror", source, "-o", reference], output, TimeSpan.FromMinutes(5));
        if (build != 0) throw new InvalidOperationException("SDK reference の build に失敗しました: " + build);
        var probe = RunCapture(reference, [], output, TimeSpan.FromMinutes(2), stream: false, environment: null, out var probeOut, out var probeErr);
        File.WriteAllText(Path.Combine(output, "sdk-stat.json"), probeOut);
        File.WriteAllText(Path.Combine(output, "probe.stderr.txt"), probeErr);
        File.WriteAllText(Path.Combine(output, "reference-receipt.json"), "{\"sdk\":\"" + sdk.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"buildExitCode\":" + build + ",\"probeExitCode\":" + probe + "}\n");
        if (probe != 0) throw new InvalidOperationException("SDK の stat ABI が製品の境界と一致しません: " + probe);
        return 0;
    }

    /// <summary>publish-manifest.json の実行ファイルを絶対パスで1行出す。</summary>
    private static int PrintExecutable(string[] args)
    {
        var publish = Path.GetFullPath(Required(args, "--publish"));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(publish, "publish-manifest.json")));
        var executable = document.RootElement.GetProperty("executable").GetString() ?? throw new InvalidOperationException("executable がありません。");
        Console.WriteLine(Path.IsPathRooted(executable) ? executable : Path.GetFullPath(Path.Combine(RepositoryRoot(), executable)));
        return 0;
    }

    /// <summary>リポジトリのルート（このツールの2つ上）。</summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DiffBeacon.slnx"))) return dir.FullName;
        throw new InvalidOperationException("DiffBeacon.slnx が見つかりません。");
    }

    /// <summary>発行 RID が今の OS と一致することを確かめる。</summary>
    private static void EnsureHostCanPublish(string rid)
    {
        var ok = rid switch
        {
            "win-x64" or "win-arm64" => OperatingSystem.IsWindows(),
            "osx-x64" or "osx-arm64" => OperatingSystem.IsMacOS(),
            "linux-x64" or "linux-arm64" => OperatingSystem.IsLinux(),
            _ => false,
        };
        if (!ok) throw new InvalidOperationException("Native AOT は対象と同じ OS 上で発行してください: " + rid);
    }

    /// <summary>作業出力がリポジトリと Codex の外の Codex/DiffBeacon 配下であることを確かめる。</summary>
    private static void EnsureWorkPath(string path, string repo)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var allowed = new List<string> { Path.Combine(Path.GetTempPath(), "Codex", "DiffBeacon"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex", "TaskArtifacts", "DiffBeacon") };
        if (Environment.GetEnvironmentVariable("DIFFBEACON_WORK_DIRECTORY") is { Length: > 0 } work) allowed.Add(work);
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } runner)
            allowed.Add(Path.Combine(runner, "Codex", "DiffBeacon"));
        bool Below(string root) => path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, comparison);
        var codex = Path.GetFullPath(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!allowed.Any(Below) || Below(repo) || path.Equals(repo, comparison) || Below(codex) || parts.Any(part => part.Equals("artifacts", StringComparison.OrdinalIgnoreCase) || part.Equals(".codex", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("生成先はrepo／Codexユーザーディレクトリ外のCodex/DiffBeacon作業別領域を指定してください。artifactsというパス要素は使用できません。");
        EnsureNoReparse(path);
    }

    /// <summary>存在する祖先がリンクでないことを確かめる。</summary>
    private static void EnsureNoReparse(string path)
    {
        for (var cursor = path; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
        {
            if (!Directory.Exists(cursor) && !File.Exists(cursor)) continue;
            var info = new DirectoryInfo(cursor);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("生成先にリンクは使用できません: " + cursor);
        }
    }

    /// <summary>2つのパスが同じか、片方が他方の中にある。</summary>
    private static bool IsSameOrNested(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return a.Equals(b, comparison) || a.StartsWith(b + Path.DirectorySeparatorChar, comparison) || b.StartsWith(a + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>名前付きオプションの値を読む。</summary>
    private static string Required(string[] args, string name) => Optional(args, name) ?? throw new InvalidOperationException(name + " が必要です。");

    /// <summary>名前付きオプションの値。無ければ null。</summary>
    private static string? Optional(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (args[index] == name) return args[index + 1];
        return null;
    }

    /// <summary>ファイルの SHA-256 を大文字16進で返す。</summary>
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>固定原本の SHA-256 を確かめる。</summary>
    private static void ExpectHash(string path, string expected)
    {
        var actual = Hash(path);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"reference原本SHAが一致しません: {path}");
    }

    /// <summary>PATH から実行ファイルを探す。</summary>
    private static string FindOnPath(string name)
    {
        var values = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var directory in values)
        {
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException(name + " が見つかりません。");
    }

    /// <summary>インストール済み Visual Studio の cl.exe を返す。</summary>
    /// <summary>cl.exe と、それが必要とする INCLUDE／LIB を返す。</summary>
    private static string FindMsvc(out Dictionary<string, string> environment)
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) throw new InvalidOperationException("MSVCの既存Visual Studioを確認できません。");
        var root = Run(vswhere, ["-latest", "-products", "*", "-property", "installationPath"], Path.GetTempPath()).Trim();
        var version = File.ReadAllText(Path.Combine(root, "VC", "Auxiliary", "Build", "Microsoft.VCToolsVersion.default.txt")).Trim();
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        var compiler = Path.Combine(root, "VC", "Tools", "MSVC", version, "bin", "Host" + arch, arch, "cl.exe");
        if (!File.Exists(compiler)) compiler = Path.Combine(root, "VC", "Tools", "MSVC", version, "bin", "Hostx64", arch, "cl.exe");
        if (!File.Exists(compiler)) throw new InvalidOperationException("host architecture用のMSVCがありません: " + arch);
        var vc = Path.Combine(root, "VC", "Tools", "MSVC", version);
        var sdkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
        var sdk = Directory.EnumerateDirectories(Path.Combine(sdkRoot, "Include")).Select(Path.GetFileName).Where(name => name is not null && Directory.Exists(Path.Combine(sdkRoot, "Lib", name, "ucrt", arch))).OrderByDescending(name => name, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidOperationException("host architecture用Windows SDKがありません。");
        environment = new Dictionary<string, string>
        {
            ["INCLUDE"] = string.Join(';', vc + "/include", $"{sdkRoot}/Include/{sdk}/ucrt", $"{sdkRoot}/Include/{sdk}/shared", $"{sdkRoot}/Include/{sdk}/um"),
            ["LIB"] = string.Join(';', $"{vc}/lib/{arch}", $"{sdkRoot}/Lib/{sdk}/ucrt/{arch}", $"{sdkRoot}/Lib/{sdk}/um/{arch}"),
        };
        return compiler;
    }

    /// <summary>プロセスを実行し、失敗なら例外にする。</summary>
    private static string Run(string file, IReadOnlyList<string> arguments, string workingDirectory, bool stream = false)
    {
        var code = RunCapture(file, arguments, workingDirectory, TimeSpan.FromHours(6), stream, null, out var stdout, out var stderr);
        if (code != 0) throw new InvalidOperationException($"{file} が終了コード {code} で失敗しました。{stderr}");
        return stdout;
    }

    /// <summary>プロセスを実行し、終了コードを返す。</summary>
    private static int RunAllowFail(string file, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null)
    {
        var code = RunCapture(file, arguments, workingDirectory, timeout, stream: true, environment, out _, out _);
        return code;
    }

    /// <summary>プロセスの標準出力と終了コードを回収する。</summary>
    private static int RunCapture(string file, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, bool stream, IReadOnlyDictionary<string, string>? environment, out string stdout, out string stderr)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = !stream, RedirectStandardError = !stream, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException(file + " を起動できません。");
        Task<string>? outputTask = stream ? null : process.StandardOutput.ReadToEndAsync();
        Task<string>? errorTask = stream ? null : process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{file} が制限時間を超えました。");
        }
        stdout = outputTask?.GetAwaiter().GetResult() ?? "";
        stderr = errorTask?.GetAwaiter().GetResult() ?? "";
        return process.ExitCode;
    }
}

/// <summary>発行 manifest のファイル1件。</summary>
internal sealed record PublishedFile(string Path, long Bytes, string Sha256);

/// <summary>発行 manifest。</summary>
internal sealed record PublishManifest(string RuntimeIdentifier, string SdkVersion, bool NativeAot, string SelfTest, string SelfTestScope, int VerificationTimeoutSeconds, double? VerificationSeconds, string Executable, PublishedFile[] Files);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PublishManifest))]
[JsonSerializable(typeof(ZBuildProof))]
internal partial class BuildJsonContext : JsonSerializerContext;

/// <summary>ncompress 参照ビルドの証明。検証スクリプトがパスと SHA をこの形で読む。</summary>
internal sealed record ZBuildProof(string Source, string SourceSha256, string PatchlevelSha256, string Compiler, string CompilerSha256,
    string HostArchitecture, string[] Arguments, int CompilerExitCode, string Decoder, string Adapter, string DecoderSha256);

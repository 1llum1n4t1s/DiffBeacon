using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DiffBeacon.Providers;

public sealed record ComparisonRequest(string LeftPath, string RightPath, string Format,
    ManagedArchiveReadOptions? LeftArchiveReadOptions = null, ManagedArchiveReadOptions? RightArchiveReadOptions = null);
public sealed record ProviderResult(string Summary, string LeftText, string RightText);

public interface IComparisonProvider
{
    string Id { get; }
    IReadOnlyList<string> Formats { get; }
    Task<ProviderResult> CompareAsync(ComparisonRequest request, CancellationToken cancellationToken);
}

/// <summary>ユーザーが選んだ実行ファイルだけを明示的に起動するプロバイダー。</summary>
public sealed class ExecutableComparisonProvider : IComparisonProvider
{
    public string Id { get; }
    public IReadOnlyList<string> Formats { get; }
    public string ExecutablePath { get; }
    private readonly string[] _arguments;
    private readonly TimeSpan _timeout;
    private const int MaximumResponseCharacters = 4 * 1024 * 1024;

    public ExecutableComparisonProvider(string id, string executablePath, IEnumerable<string> formats,
        IEnumerable<string>? arguments = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!Path.IsPathFullyQualified(executablePath)) throw new ArgumentException("プロバイダーには実行ファイルの絶対パスが必要です。", nameof(executablePath));
        Id = id;
        ExecutablePath = Path.GetFullPath(executablePath);
        Formats = Array.AsReadOnly(formats.ToArray());
        _arguments = arguments?.ToArray() ?? [];
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<ProviderResult> CompareAsync(ComparisonRequest request, CancellationToken cancellationToken)
    {
        if (!Formats.Contains(request.Format, StringComparer.OrdinalIgnoreCase)) throw new NotSupportedException($"プロバイダーは {request.Format} に対応していません。");
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!
        };
        foreach (var argument in _arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        if (!process.Start()) throw new InvalidOperationException("プロバイダーを起動できませんでした。");
        try
        {
            // stdout/stderr を並行で読み、パイプのバッファ詰まりを防ぐ。
            var outputTask = ReadBoundedAsync(process.StandardOutput, MaximumResponseCharacters, deadline.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, 64 * 1024, deadline.Token);
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartObject(); writer.WriteNumber("protocolVersion", 1);
                writer.WriteString("leftPath", Path.GetFullPath(request.LeftPath));
                writer.WriteString("rightPath", Path.GetFullPath(request.RightPath));
                writer.WriteString("format", request.Format); writer.WriteEndObject();
            }
            await process.StandardInput.WriteLineAsync(Encoding.UTF8.GetString(bytes.ToArray()).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            var exitTask = process.WaitForExitAsync(deadline.Token);
            var pending = new List<Task> { outputTask, errorTask, exitTask };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
            }
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"プロバイダー終了コード {process.ExitCode}: {error}");
            using var response = JsonDocument.Parse(output);
            var root = response.RootElement;
            if (root.GetProperty("protocolVersion").GetInt32() != 1) throw new InvalidDataException("プロバイダーのプロトコル版が未対応です。");
            return new ProviderResult(ReadString(root, "summary"), ReadString(root, "leftText"), ReadString(root, "rightText"));
        }
        catch
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            throw;
        }
    }

    private static string ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"プロバイダー応答の {property} は文字列である必要があります。");
        return value.GetString()!;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var text = new StringBuilder(); var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) return text.ToString();
            if (text.Length > limit - count) throw new InvalidDataException("プロバイダー応答がサイズ上限を超えました。");
            text.Append(buffer, 0, count);
        }
    }
}

/// <summary>自動探索やダウンロードをせず、呼び出し元が明示登録する。</summary>
public sealed class ComparisonProviderRegistry
{
    private readonly Dictionary<string, IComparisonProvider> _providers = new(StringComparer.Ordinal);
    public IEnumerable<IComparisonProvider> Providers => _providers.Values;
    public void Register(IComparisonProvider provider)
    { ArgumentNullException.ThrowIfNull(provider); if (!_providers.TryAdd(provider.Id, provider)) throw new ArgumentException("同じIDのプロバイダーが登録済みです。", nameof(provider)); }
    public IComparisonProvider Get(string id) => _providers.TryGetValue(id, out var provider) ? provider : throw new KeyNotFoundException($"プロバイダーが未登録です: {id}");
}

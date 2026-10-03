using System.Text;

namespace DiffBeacon.Core;

public sealed record TextLoadOptions
{
    public string FallbackEncodingName { get; init; } = "windows-1252";
    public long MaxFileSize { get; init; } = 64L * 1024 * 1024;
}

public sealed class TextDocument
{
    private readonly Encoding encoding;
    private readonly byte[] preamble;
    public string Path { get; private set; }
    public string Text { get; private set; }
    public string EncodingName => encoding.WebName;
    public string NewLine => TextLines.NewLine(Text);
    public bool HasBom => preamble.Length != 0;
    public bool HasFinalNewLine => TextLines.HasFinalNewLine(Text);

    private TextDocument(string path, string text, Encoding encoding, byte[] preamble)
    {
        Path = path;
        Text = text;
        this.encoding = encoding;
        this.preamble = preamble;
    }

    public static Task<TextDocument> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, new TextLoadOptions(), cancellationToken);

    public static TextDocument Create(string text = "") => new("", text, new UTF8Encoding(false, true), []);

    /// <summary>物理保存先を持たない内容を、通常のBOM・encoding・NUL契約で復号する。</summary>
    public static TextDocument FromSnapshot(byte[] bytes, TextLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return FromBytes("", bytes, options ?? new TextLoadOptions());
    }

    // 別文書の出力で読込み元のPath/Textを変更しない。
    public Task SaveCopyAsync(string path, string text, CancellationToken cancellationToken = default) =>
        new TextDocument(Path, Text, encoding, preamble).SaveAsync(path, text, cancellationToken);

    public static async Task<TextDocument> LoadAsync(string path, TextLoadOptions options,
        CancellationToken cancellationToken = default)
    {
        var absolute = System.IO.Path.GetFullPath(path);
        if (new FileInfo(absolute).Length > options.MaxFileSize)
            throw new InvalidDataException($"テキストファイルの上限 {options.MaxFileSize} バイトを超えています。");
        var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);
        return FromBytes(absolute, bytes, options);
    }

    internal static TextDocument FromBytes(string absolute, byte[] bytes, TextLoadOptions options)
    {
        if (bytes.LongLength > options.MaxFileSize) throw new InvalidDataException("テキストファイルがサイズ上限を超えています。");
        var (encoding, preambleLength) = DetectEncoding(bytes, options.FallbackEncodingName);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        if (text.Contains('\0')) throw new InvalidDataException("NUL を含むファイルはバイナリ比較で開いてください。");
        return new(absolute, text, encoding, bytes[..preambleLength]);
    }

    public async Task SaveAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0')) throw new InvalidDataException("テキストに NUL を保存できません。");
        var absolute = System.IO.Path.GetFullPath(path);
        var targetExists = File.Exists(absolute);
        var attributes = targetExists ? File.GetAttributes(absolute) : FileAttributes.Normal;
        if ((attributes & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("読み取り専用のファイルは保存できません。");
        UnixFileMode? unixMode = null;
        if (!OperatingSystem.IsWindows() && targetExists) unixMode = File.GetUnixFileMode(absolute);
        var parent = System.IO.Path.GetDirectoryName(absolute)!;
        var temporary = System.IO.Path.Combine(parent, "." + System.IO.Path.GetFileName(absolute) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
                var bytes = encoding.GetBytes(text);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (unixMode.HasValue && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, unixMode.Value);
            if (OperatingSystem.IsWindows() && targetExists)
            {
                var preserved = attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed);
                File.SetAttributes(temporary, preserved == 0 ? FileAttributes.Normal : preserved);
            }
            if (File.Exists(absolute) && (File.GetAttributes(absolute) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("読み取り専用のファイルは保存できません。");
            // 同じディレクトリ内で置換するため、書込み途中の内容を公開しない。
            File.Move(temporary, absolute, true);
            Path = absolute;
            Text = text;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes, string fallback)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return (new UTF32Encoding(true, true, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return (new UTF32Encoding(false, true, true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return (new UTF8Encoding(true, true), 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return (new UnicodeEncoding(false, true, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return (new UnicodeEncoding(true, true, true), 2);
        var count = Math.Min(bytes.Length, 8192);
        var evenZeros = 0;
        var oddZeros = 0;
        for (var i = 0; i < count; i++) if (bytes[i] == 0) { if (i % 2 == 0) evenZeros++; else oddZeros++; }
        if (count >= 4 && bytes.Length % 2 == 0)
        {
            if (oddZeros > count / 4 && evenZeros < count / 16) return (new UnicodeEncoding(false, false, true), 0);
            if (evenZeros > count / 4 && oddZeros < count / 16) return (new UnicodeEncoding(true, false, true), 0);
        }
        var utf8 = new UTF8Encoding(false, true);
        try { _ = utf8.GetCharCount(bytes); return (utf8, 0); }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(fallback, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), 0);
        }
    }
}

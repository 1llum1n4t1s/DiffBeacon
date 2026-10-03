using System.Text;

namespace DiffBeacon.App;

// 全ページの予算を確定する間だけ、復号・整列済みの一組を順に退避する。
// 差分canvas、強調画素、PNGは保存せず、復号と整列を繰り返さない。
internal sealed class ImageReportPreparation : IDisposable
{
    // Original+方向変換の実コピーは復号予算256M、整列画素はcanvas予算の最大3/4。
    // infosは48bytes/最低11work、写像は5bytes/最低2workなのでmetadataは5bytes/work以内。
    // 固定ヘッダーは一組2048bytes以内。既存予算から導出した容量であり、新しい入力制限ではない。
    internal const long MaximumBytes = 4 * (ImageComparisonEngine.MaximumDecodeWork
        + ImageComparisonEngine.MaximumDecodeWork * 3 / 4) + ImageLineDiffer.MaximumWork * 5
        + ImageComparisonEngine.MaximumFrames * 2048L;
    private readonly FileStream _stream;
    private readonly CancellationToken _token;
    private readonly string _path;
    private int _written, _read;
    private bool _reading;
    private Exception? _failure;

    internal ImageReportPreparation(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _token = token;
        _path = Path.Combine(Path.GetTempPath(), "DiffBeacon-image-report-" + Guid.NewGuid().ToString("N") + ".spool");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.None, Options = FileOptions.DeleteOnClose | FileOptions.SequentialScan, BufferSize = 65_536 };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _stream = new FileStream(_path, options);
    }

    internal void Add(ImageComparisonEngine.PreparedFrames prepared)
    {
        _token.ThrowIfCancellationRequested();
        if (_reading || _written >= ImageComparisonEngine.MaximumFrames) throw Invalid();
        var alignment = prepared.Alignment ?? throw Invalid();
        var groups = new[] { prepared.OriginalFrames, alignment.ViewFrames, prepared.Frames };
        var buffers = new List<byte[]>();
        foreach (var group in groups)
        foreach (var frame in group)
            if (!buffers.Any(buffer => ReferenceEquals(buffer, frame.Pixels))) buffers.Add(frame.Pixels);
        long bytes = 17 + 4; // work、canvas、方向とbuffer数。
        foreach (var buffer in buffers) bytes = checked(bytes + 4 + buffer.LongLength);
        foreach (var group in groups) bytes = checked(bytes + 4 + group.Count * 16L);
        bytes = checked(bytes + 8 + alignment.LineDiffInfos.Count * 48L);
        foreach (var mapping in alignment.PaneMappings) bytes = checked(bytes + 4 + mapping.SourceLines.LongLength * 5);
        if (bytes + 8 > MaximumBytes - _stream.Position) throw new InvalidDataException("画像HTML準備データの容量が共有予算の上限を超えます。");
        var end = checked(_stream.Position + 8 + bytes);
        using var writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(bytes); writer.Write(prepared.AlignmentWork); writer.Write(prepared.CanvasWork); writer.Write(alignment.Horizontal);
        writer.Write(buffers.Count);
        foreach (var buffer in buffers)
        {
            writer.Write(buffer.Length);
            for (var offset = 0; offset < buffer.Length; offset += 65_536)
            { _token.ThrowIfCancellationRequested(); writer.Write(buffer.AsSpan(offset, Math.Min(65_536, buffer.Length - offset))); }
        }
        foreach (var group in groups)
        {
            writer.Write(group.Count);
            foreach (var frame in group)
            {
                writer.Write(frame.Number); writer.Write(frame.Width); writer.Write(frame.Height);
                writer.Write(buffers.FindIndex(buffer => ReferenceEquals(buffer, frame.Pixels)));
            }
        }
        writer.Write(alignment.LineDiffInfos.Count);
        foreach (var info in alignment.LineDiffInfos)
        {
            _token.ThrowIfCancellationRequested();
            foreach (var value in info.Begin) writer.Write(value);
            foreach (var value in info.End) writer.Write(value);
            writer.Write(info.DisplayBegin);
            foreach (var value in info.DisplayEnd) writer.Write(value);
            writer.Write(info.DisplayEndMaximum); writer.Write(info.Operation);
        }
        writer.Write(alignment.PaneMappings.Count);
        foreach (var mapping in alignment.PaneMappings)
        {
            writer.Write(mapping.SourceLines.Length);
            for (var i = 0; i < mapping.SourceLines.Length; i++)
            { if ((i & 1023) == 0) _token.ThrowIfCancellationRequested(); writer.Write(mapping.SourceLines[i]); }
            for (var i = 0; i < mapping.GhostFlags.Length; i++)
            { if ((i & 1023) == 0) _token.ThrowIfCancellationRequested(); writer.Write(mapping.GhostFlags[i]); }
        }
        writer.Flush();
        if (_stream.Position != end) throw Invalid();
        _written++;
    }

    internal void Rewind()
    {
        _token.ThrowIfCancellationRequested();
        if (_reading) throw Invalid();
        _stream.Flush(); _stream.Position = 0; _reading = true;
    }

    internal ImageComparisonEngine.PreparedFrames Next()
    {
        _token.ThrowIfCancellationRequested();
        if (!_reading || _read >= _written) throw Invalid();
        using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            var length = reader.ReadInt64();
            if (length < 0 || length > MaximumBytes || length > _stream.Length - _stream.Position) throw Invalid();
            var end = checked(_stream.Position + length);
            var work = reader.ReadInt64(); var canvasWork = reader.ReadInt64(); var horizontal = ReadBoolean();
            if (work < 0 || work > ImageLineDiffer.MaximumWork || canvasWork < 0 || canvasWork > ImageComparisonEngine.MaximumDecodeWork) throw Invalid();
            var buffers = new byte[Count(9, 4)][];
            for (var i = 0; i < buffers.Length; i++)
            {
                var count = Count(checked((int)ImageComparisonEngine.MaximumPixels * 4), 1);
                if (count == 0 || count % 4 != 0) throw Invalid();
                var buffer = new byte[count];
                for (var offset = 0; offset < count; offset += 65_536)
                { _token.ThrowIfCancellationRequested(); _stream.ReadExactly(buffer.AsSpan(offset, Math.Min(65_536, count - offset))); }
                buffers[i] = buffer;
            }
            var original = Frames(); var views = Frames(); var frames = Frames();
            if (views.Length != frames.Length || original.Length != frames.Length) throw Invalid();
            var infos = new ImageLineAlignment.LineDiffInfo[Count(ImageLineDiffer.MaximumRows, 48)];
            for (var i = 0; i < infos.Length; i++)
            {
                _token.ThrowIfCancellationRequested();
                var begin = Three(); var last = Three(); var displayBegin = reader.ReadInt32(); var displayEnd = Three();
                infos[i] = new(begin, last, displayBegin, displayEnd, reader.ReadInt32(), reader.ReadInt32());
            }
            var mappings = new ImageLineAlignment.PaneMapping[Count(3, 4)];
            if (mappings.Length != frames.Length) throw Invalid();
            for (var pane = 0; pane < mappings.Length; pane++)
            {
                var count = Count(ImageLineDiffer.MaximumRows, 5);
                if (count != (horizontal ? frames[pane].Width : frames[pane].Height)) throw Invalid();
                var lines = new int[count]; var ghosts = new bool[count];
                for (var i = 0; i < count; i++)
                { if ((i & 1023) == 0) _token.ThrowIfCancellationRequested(); lines[i] = reader.ReadInt32(); }
                for (var i = 0; i < count; i++)
                { if ((i & 1023) == 0) _token.ThrowIfCancellationRequested(); ghosts[i] = ReadBoolean(); }
                mappings[pane] = new(lines, ghosts);
            }
            if (_stream.Position != end || ++_read == _written && end != _stream.Length) throw Invalid();
            _token.ThrowIfCancellationRequested();
            return new(frames, original, work, new(frames, infos, mappings, horizontal, work, views), canvasWork);

            bool ReadBoolean()
            { return reader.ReadByte() switch { 0 => false, 1 => true, _ => throw Invalid() }; }
            int Count(int maximum, int elementBytes)
            {
                _token.ThrowIfCancellationRequested();
                var count = reader.ReadInt32();
                if (count < 0 || count > maximum || (long)count * elementBytes > end - _stream.Position) throw Invalid();
                return count;
            }
            int[] Three() => [reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()];
            ImageComparisonEngine.DecodedFrame[] Frames()
            {
                var result = new ImageComparisonEngine.DecodedFrame[Count(3, 16)];
                if (result.Length < 2) throw Invalid();
                for (var i = 0; i < result.Length; i++)
                {
                    var number = reader.ReadInt32(); var width = reader.ReadInt32(); var height = reader.ReadInt32(); var buffer = reader.ReadInt32();
                    if (number is < 1 or > ImageComparisonEngine.MaximumFrames || width <= 0 || height <= 0
                        || (long)width * height > ImageComparisonEngine.MaximumPixels || (uint)buffer >= (uint)buffers.Length
                        || (long)width * height * 4 != buffers[buffer].LongLength) throw Invalid();
                    result[i] = new(number, width, height, buffers[buffer]);
                }
                return result;
            }
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("画像HTML準備データを完全に読み込めません。", error); }
    }

    private static InvalidDataException Invalid() => new("画像HTML準備データの構造または長さが不正です。");

    internal void RecordFailure(Exception failure) => _failure = failure;

    public void Dispose()
    {
        try
        {
            try { _stream.Dispose(); }
            finally
            {
                // DeleteOnCloseに加え、失敗時の残存を確認し、残れば対象だけを除去する。
                if (File.Exists(_path))
                {
                    try { File.Delete(_path); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { throw new IOException("画像HTML準備用の一時ファイルを削除できません: " + _path, error); }
                    if (File.Exists(_path)) throw new IOException("画像HTML準備用の一時ファイルが残っています: " + _path);
                }
            }
        }
        catch (Exception cleanup) when (_failure is not null)
        {
            throw new AggregateException("画像HTML処理の失敗と一時ファイルの後処理失敗: " + _failure.Message,
                _failure, cleanup);
        }
    }
}

namespace DiffBeacon.Providers;

// UNIX compress のLSB順code。幅の変更とCLEARは8code単位の境界へ揃える。
internal sealed class ZReadStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly CancellationToken _token;
    private readonly int _maximumBits;
    private readonly bool _block;
    private readonly int[] _prefix;
    private readonly byte[] _suffix;
    private readonly byte[] _stack;
    private readonly byte[] _group = new byte[16];
    private int _width = 9, _next, _maximumCode, _groupBits, _bitOffset, _pending;
    private int _previous = -1;
    private byte _first;
    private bool _eof, _disposed, _failed, _started;

    public ZReadStream(Stream inner, CancellationToken token, bool leaveOpen = false)
    {
        _inner = inner; _token = token; _leaveOpen = leaveOpen;
        token.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[3];
        var headerBytes = 0;
        while (headerBytes < 3)
        {
            token.ThrowIfCancellationRequested();
            var count = inner.Read(header[headerBytes..]); if (count == 0) break; headerBytes += count;
        }
        if (headerBytes != 3 || header[0] != 0x1f || header[1] != 0x9d
            || (header[2] & 0x60) != 0 || (header[2] & 0x1f) is < 9 or > 16)
            throw new InvalidDataException("Z/compressのヘッダーが不正です。");
        _maximumBits = header[2] & 0x1f; _block = (header[2] & 0x80) != 0;
        _next = _block ? 257 : 256;
        _maximumCode = _maximumBits == 9 ? 512 : 511;
        _prefix = new int[1 << _maximumBits]; _suffix = new byte[_prefix.Length]; _stack = new byte[_prefix.Length];
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) => ReadCore(buffer, default);
    internal int Read(Span<byte> buffer, CancellationToken cancellationToken) => ReadCore(buffer, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ReadCore(buffer.Span, cancellationToken));
    private int ReadCore(Span<byte> buffer, CancellationToken callToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed) throw new InvalidDataException("失敗したZストリームは再利用できません。");
        var written = 0;
        try
        {
            while (written < buffer.Length)
            {
                _token.ThrowIfCancellationRequested(); callToken.ThrowIfCancellationRequested();
                if (_pending != 0) { buffer[written++] = _stack[--_pending]; continue; }
                if (_eof) break;
                if (_next > _maximumCode && _width < _maximumBits)
                {
                    _width++; _maximumCode = _width == _maximumBits ? 1 << _width : (1 << _width) - 1;
                    _groupBits = _bitOffset = 0;
                }
                var code = ReadCode(callToken);
                if (code < 0) { _eof = true; break; }
                if (!_started && code > 255) throw new InvalidDataException("Zの初回codeが不正です。");
                _started = true;
                if (_block && code == 256)
                {
                    _width = 9; _maximumCode = _maximumBits == 9 ? 512 : 511;
                    _groupBits = _bitOffset = 0; _next = 257; _previous = -1;
                    continue;
                }
                if (_previous < 0)
                {
                    if (code > 255) throw new InvalidDataException("Zの初回codeが不正です。");
                    _first = (byte)code; _previous = code; _stack[_pending++] = _first;
                    continue;
                }
                var original = code;
                if (code == _next) { _stack[_pending++] = _first; code = _previous; }
                else if (code > _next) throw new InvalidDataException("Zの辞書参照が不正です。");
                while (code >= 256)
                {
                    _token.ThrowIfCancellationRequested(); callToken.ThrowIfCancellationRequested();
                    if (code >= _next || _pending >= _stack.Length - 1 || _prefix[code] >= code)
                        throw new InvalidDataException("Zの辞書鎖が不正です。");
                    _stack[_pending++] = _suffix[code]; code = _prefix[code];
                }
                _first = (byte)code; _stack[_pending++] = _first;
                if (_next < _prefix.Length)
                { _prefix[_next] = _previous; _suffix[_next] = _first; _next++; }
                _previous = original;
            }
            return written;
        }
        catch { _failed = true; throw; }
    }
    private int ReadCode(CancellationToken callToken)
    {
        if (_bitOffset + _width > _groupBits)
        {
            var length = 0;
            while (length < _width)
            {
                _token.ThrowIfCancellationRequested(); callToken.ThrowIfCancellationRequested();
                var count = _inner.Read(_group.AsSpan(length, _width - length));
                if (count == 0) break;
                length += count;
            }
            _groupBits = length * 8; _bitOffset = 0;
            if (_groupBits < _width) return -1;
        }
        var code = 0;
        for (var bit = 0; bit < _width; bit++)
            code |= ((_group[(_bitOffset + bit) >> 3] >> ((_bitOffset + bit) & 7)) & 1) << bit;
        _bitOffset += _width;
        return code;
    }
    protected override void Dispose(bool disposing)
    { if (!_disposed && disposing && !_leaveOpen) _inner.Dispose(); _disposed = true; base.Dispose(disposing); }
    public override bool CanRead => !_disposed;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { ObjectDisposedException.ThrowIf(_disposed, this); _token.ThrowIfCancellationRequested(); }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

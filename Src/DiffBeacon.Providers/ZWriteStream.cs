namespace DiffBeacon.Providers;

// 16bit block形式。満杯の辞書は保持し、Dispose/Flushから終端を暗黙に出力しない。
internal sealed class ZWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly CancellationToken _token;
    private readonly bool _leaveOpen;
    private readonly Dictionary<int, int> _dictionary = new(65536);
    private readonly byte[] _group = new byte[16];
    private int _width = 9, _maximumCode = 511, _next = 257, _bits, _current = -1;
    private bool _complete, _failed, _disposed;
    public ZWriteStream(Stream inner, CancellationToken token, bool leaveOpen = false)
    {
        _inner = inner; _token = token; _leaveOpen = leaveOpen;
        token.ThrowIfCancellationRequested(); inner.Write([0x1f, 0x9d, 0x90]);
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) => WriteCore(buffer, default);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { WriteCore(buffer.Span, cancellationToken); return ValueTask.CompletedTask; }
    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed) throw new InvalidDataException("失敗したZストリームは再利用できません。");
        _token.ThrowIfCancellationRequested();
    }
    private void WriteCore(ReadOnlySpan<byte> buffer, CancellationToken callToken)
    {
        Check();
        if (_complete) throw new InvalidOperationException("完了したZストリームには書き込めません。");
        try
        {
            foreach (var value in buffer)
            {
                _token.ThrowIfCancellationRequested(); callToken.ThrowIfCancellationRequested();
                if (_current < 0) { _current = value; continue; }
                var key = (_current << 8) | value;
                if (_dictionary.TryGetValue(key, out var code)) _current = code;
                else
                {
                    Emit(_current);
                    if (_next < 65536) _dictionary.Add(key, _next++);
                    _current = value;
                }
            }
        }
        catch { _failed = true; throw; }
    }
    private void Emit(int code)
    {
        for (var bit = 0; bit < _width; bit++)
            _group[(_bits + bit) >> 3] |= (byte)(((code >> bit) & 1) << ((_bits + bit) & 7));
        _bits += _width;
        if (_bits == _width * 8) FlushGroup(_width);
        // encoderの辞書追加はcode出力後、decoderは次code読込み前に幅を更新する。
        if (_next > _maximumCode && _width < 16)
        {
            if (_bits != 0) FlushGroup(_width);
            _width++; _maximumCode = _width == 16 ? 65536 : (1 << _width) - 1;
        }
    }
    private void FlushGroup(int bytes)
    {
        _token.ThrowIfCancellationRequested(); _inner.Write(_group.AsSpan(0, bytes));
        Array.Clear(_group); _bits = 0;
    }
    public void Complete()
    {
        Check();
        if (_complete) return;
        try
        {
            if (_current >= 0) Emit(_current);
            if (_bits != 0) FlushGroup((_bits + 7) / 8);
            _inner.Flush(); _complete = true;
        }
        catch { _failed = true; throw; }
    }
    public override void Flush()
    { Check(); try { _inner.Flush(); } catch { _failed = true; throw; } }
    protected override void Dispose(bool disposing)
    { if (!_disposed && disposing && !_leaveOpen) _inner.Dispose(); _disposed = true; base.Dispose(disposing); }
    public override bool CanRead => false;
    public override bool CanWrite => !_disposed && !_complete && !_failed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

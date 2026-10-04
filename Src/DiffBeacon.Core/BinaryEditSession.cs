using System.Security.Cryptography;

namespace DiffBeacon.Core;

/// <summary>editorの可変bytesと非同期保存の不変捕捉を分離する。</summary>
public sealed class BinaryCapture
{
    private readonly byte[] _bytes;
    internal object Owner { get; }
    internal int Side { get; }
    internal ReadOnlyMemory<byte> Bytes => _bytes;
    public int Length => _bytes.Length;
    public string Sha256 { get; }
    internal BinaryCapture(object owner, int side, byte[] bytes)
    { Owner = owner; Side = side; _bytes = bytes; Sha256 = Convert.ToHexString(SHA256.HashData(bytes)); }
    public byte[] CopyBytes() => _bytes.ToArray();
}

public sealed class BinaryEditSession : IDisposable
{
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    public const int MaximumHistoryBytes = 64 * 1024 * 1024;
    public const int MaximumHistoryActions = 256;
    private readonly byte[][] _bytes;
    private readonly bool[] _readOnly;
    private readonly string[] _savedSha;
    private readonly string?[] _currentSha;
    private readonly long[] _revisions;
    private readonly object _identity = new();
    private readonly List<Change> _history = [];
    private int _cursor, _historyBytes;
    private bool _disposed;
    private sealed record Change(int Side, int Start, byte[] Before, byte[] After);

    public BinaryEditSession(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        : this([BoundedCopy(left), BoundedCopy(right)]) { }
    public BinaryEditSession(ReadOnlySpan<byte> left, ReadOnlySpan<byte> middle, ReadOnlySpan<byte> right)
        : this([BoundedCopy(left), BoundedCopy(middle), BoundedCopy(right)]) { }
    private static byte[] BoundedCopy(ReadOnlySpan<byte> bytes)
    { if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("16進比較の上限は各16 MiBです。"); return bytes.ToArray(); }
    private BinaryEditSession(byte[][] bytes)
    {
        if (bytes.Any(value => value.Length > MaximumFileBytes)) throw new InvalidDataException("16進比較の上限は各16 MiBです。");
        _bytes = bytes; _readOnly = new bool[bytes.Length]; _savedSha = new string[bytes.Length];
        _currentSha = new string?[bytes.Length]; _revisions = new long[bytes.Length];
        for (var side = 0; side < SideCount; side++) _savedSha[side] = CurrentSha(side);
    }
    public int SideCount => _bytes.Length;
    private void ValidateSide(int side) { ObjectDisposedException.ThrowIf(_disposed, this); if (side < 0 || side >= SideCount) throw new ArgumentOutOfRangeException(nameof(side)); }
    public int Length(int side) { ValidateSide(side); return _bytes[side].Length; }
    public long Revision(int side) { ValidateSide(side); return _revisions[side]; }
    public bool IsReadOnly(int side) { ValidateSide(side); return _readOnly[side]; }
    public void SetReadOnly(int side, bool value) { ValidateSide(side); _readOnly[side] = value; }
    private void Writable(int side) { ValidateSide(side); if (_readOnly[side]) throw new InvalidOperationException("この側は読取り専用です。"); }
    private string CurrentSha(int side) => _currentSha[side] ??= Convert.ToHexString(SHA256.HashData(_bytes[side]));
    public bool IsDirty(int side) { ValidateSide(side); return CurrentSha(side) != _savedSha[side]; }
    public bool HasUnsavedChanges => Enumerable.Range(0, SideCount).Any(IsDirty);
    public bool CanUndo => !_disposed && _cursor > 0;
    public bool CanRedo => !_disposed && _cursor < _history.Count;
    public BinaryCapture Capture(int side) { ValidateSide(side); return new(_identity, side, _bytes[side].ToArray()); }
    public byte[] Page(int side, int start, int maximum = 4096)
    {
        ValidateSide(side); if (start < 0 || maximum < 0 || maximum > MaximumFileBytes) throw new ArgumentOutOfRangeException(nameof(start));
        start = Math.Min(start, _bytes[side].Length); return _bytes[side].AsSpan(start, Math.Min(maximum, _bytes[side].Length - start)).ToArray();
    }
    public void ApplyHexPage(int side, int start, string hex)
    {
        Writable(side); ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length > 16_384) throw new FormatException("16進編集の文字数上限を超えています。");
        var replacement = Convert.FromHexString(string.Concat(hex.Where(value => !char.IsWhiteSpace(value))));
        if (start < 0 || start > _bytes.Max(bytes => bytes.Length)) throw new ArgumentOutOfRangeException(nameof(start));
        var offset = Math.Min(start, _bytes[side].Length); var length = Math.Min(4096, _bytes[side].Length - offset);
        if (replacement.Length != length) throw new FormatException("編集前と同じバイト数を入力してください。");
        var candidate = _bytes[side].ToArray(); replacement.CopyTo(candidate, offset); Commit(side, candidate);
    }
    public void CopyRange(int source, int destination, int start, int length)
    {
        ValidateSide(source); Writable(destination);
        if (source == destination || start < 0 || length < 0 || (long)start + length > Math.Max(_bytes[source].Length, _bytes[destination].Length)) throw new ArgumentOutOfRangeException(nameof(start));
        if (length == 0) return;
        var a = _bytes[source]; var b = _bytes[destination];
        var sourceCount = Math.Min(length, Math.Max(0, a.Length - start));
        var newLength = (long)start + length >= a.Length && (long)start + length >= b.Length ? start + sourceCount : b.Length;
        if (newLength > MaximumFileBytes) throw new InvalidDataException("バイナリ保存の上限は各16 MiBです。");
        var candidate = new byte[newLength]; b.AsSpan(0, Math.Min(b.Length, candidate.Length)).CopyTo(candidate);
        a.AsSpan(Math.Min(start, a.Length), sourceCount).CopyTo(candidate.AsSpan(Math.Min(start, candidate.Length)));
        Commit(destination, candidate);
    }
    private void Commit(int side, byte[] candidate)
    {
        var current = _bytes[side]; if (current.AsSpan().SequenceEqual(candidate)) return;
        var prefix = 0; while (prefix < current.Length && prefix < candidate.Length && current[prefix] == candidate[prefix]) prefix++;
        var suffix = 0; while (suffix < current.Length - prefix && suffix < candidate.Length - prefix && current[^(suffix + 1)] == candidate[^(suffix + 1)]) suffix++;
        var change = new Change(side, prefix, current.AsSpan(prefix, current.Length - prefix - suffix).ToArray(), candidate.AsSpan(prefix, candidate.Length - prefix - suffix).ToArray());
        var retainedBytes = _history.Take(_cursor).Sum(Cost);
        if (_cursor >= MaximumHistoryActions || (long)retainedBytes + Cost(change) > MaximumHistoryBytes)
            throw new InvalidDataException("バイナリ履歴は共有64 MiB、256操作までです。履歴と本文は変更していません。");
        for (var index = _history.Count - 1; index >= _cursor; index--) { _historyBytes -= Cost(_history[index]); _history.RemoveAt(index); }
        _history.Add(change); _cursor++; _historyBytes += Cost(change); SetBytes(side, candidate);
    }
    private static int Cost(Change change) => checked(change.Before.Length + change.After.Length);
    private void SetBytes(int side, byte[] bytes) { _bytes[side] = bytes; _currentSha[side] = null; _revisions[side]++; }
    private void Restore(Change change, bool undo)
    {
        Writable(change.Side); var removed = undo ? change.After : change.Before; var inserted = undo ? change.Before : change.After;
        var current = _bytes[change.Side]; var candidate = new byte[checked(current.Length - removed.Length + inserted.Length)];
        current.AsSpan(0, change.Start).CopyTo(candidate); inserted.CopyTo(candidate, change.Start);
        current.AsSpan(change.Start + removed.Length).CopyTo(candidate.AsSpan(change.Start + inserted.Length)); SetBytes(change.Side, candidate);
    }
    public bool Undo() { if (!CanUndo) return false; Restore(_history[_cursor - 1], true); _cursor--; return true; }
    public bool Redo() { if (!CanRedo) return false; Restore(_history[_cursor], false); _cursor++; return true; }
    public void MarkSaved(int side, BinaryCapture capture)
    {
        ValidateSide(side); ArgumentNullException.ThrowIfNull(capture);
        if (!ReferenceEquals(capture.Owner, _identity) || capture.Side != side) throw new InvalidOperationException("保存元のバイナリが変更されました。");
        _savedSha[side] = capture.Sha256;
    }
    public void MarkClean() { ObjectDisposedException.ThrowIf(_disposed, this); for (var side = 0; side < SideCount; side++) _savedSha[side] = CurrentSha(side); }
    public void AdoptSavedBytes(int side, ReadOnlySpan<byte> bytes)
    {
        ValidateSide(side); if (IsDirty(side)) throw new InvalidOperationException("未保存のバイナリを別tabの保存で置換できません。");
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("16進比較の上限は各16 MiBです。");
        for (var index = _history.Count - 1; index >= 0; index--)
            if (_history[index].Side == side) { _historyBytes -= Cost(_history[index]); _history.RemoveAt(index); if (index < _cursor) _cursor--; }
        SetBytes(side, bytes.ToArray()); _savedSha[side] = CurrentSha(side);
    }
    public IReadOnlyList<(int Start, int Length)> Differences(int maximumRanges, out int total)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maximumRanges < 0) throw new ArgumentOutOfRangeException(nameof(maximumRanges));
        var ranges = new List<(int Start, int Length)>(); total = 0;
        var maximumLength = _bytes.Max(bytes => bytes.Length);
        bool EqualAt(int offset)
        {
            if (offset >= _bytes[0].Length) return false;
            var value = _bytes[0][offset];
            for (var side = 1; side < SideCount; side++)
                if (offset >= _bytes[side].Length || _bytes[side][offset] != value) return false;
            return true;
        }
        for (var index = 0; index < maximumLength; index++)
        {
            if (EqualAt(index)) continue;
            var start = index; while (index + 1 < maximumLength && !EqualAt(index + 1)) index++;
            total++; if (ranges.Count < maximumRanges) ranges.Add((start, index - start + 1));
        }
        return ranges;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; for (var side = 0; side < SideCount; side++) _bytes[side] = []; _history.Clear(); _cursor = _historyBytes = 0; }
}

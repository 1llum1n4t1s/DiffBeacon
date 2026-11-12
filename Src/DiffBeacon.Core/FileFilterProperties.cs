namespace DiffBeacon.Core;

public enum FileFilterPropertyKind { None, Integer, Double, String, Boolean, Timestamp, Array }

/// <summary>Windows の PROPVARIANT から移した値。Core は OS の property store に依存しない。</summary>
public sealed record FileFilterPropertyValue(FileFilterPropertyKind Kind, long Integer = 0,
    double Double = 0, string? Text = null, bool Boolean = false, IReadOnlyList<FileFilterPropertyValue>? Items = null)
{
    public static FileFilterPropertyValue None { get; } = new(FileFilterPropertyKind.None);
}

public interface IFileFilterPropertyService
{
    void ValidateNames(IReadOnlyList<string> canonicalNames);
    // 戻り値は store の取得状態。hash-only では false でも値が存在する。
    bool Read(string path, IReadOnlyList<string> canonicalNames, CancellationToken token,
        out IReadOnlyList<FileFilterPropertyValue> values);
}

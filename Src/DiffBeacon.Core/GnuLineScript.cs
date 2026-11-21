namespace DiffBeacon.Core;

/// <summary>GNU入力準備後の同値クラスに対する変更script。</summary>
public readonly record struct GnuScriptChange(int LeftStart, int RightStart, int LeftCount, int RightCount);

public sealed record GnuScriptResult(IReadOnlyList<GnuScriptChange> Changes, int WorkUsed, bool Fallback, string? FallbackReason);

/// <summary>入力変換と行算法を分けて照合するための、同値クラスの比較経路。</summary>
public static class GnuLineScript
{
    public static GnuScriptResult Compare(int[] left, int[] right, int classCount,
        int maxWork = 4_000_000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentOutOfRangeException.ThrowIfNegative(classCount);
        ArgumentOutOfRangeException.ThrowIfNegative(maxWork);
        cancellationToken.ThrowIfCancellationRequested();
        if (left.Length > 262_144 || right.Length > 262_144)
            throw new ArgumentException("同値クラス入力は片側262,144行までです。");
        foreach (var values in new[] { left, right })
            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((uint)value >= (uint)classCount)
                    throw new ArgumentException("同値クラスIDがclassCountの範囲外です。");
            }
        var budget = Math.Min(maxWork, GnuLineDiffer.MaximumWork);
        var copyWork = left.Length + right.Length;
        if (copyWork > budget)
            return new(Array.AsReadOnly(left.Length == 0 && right.Length == 0 ? Array.Empty<GnuScriptChange>()
                : [new(0, 0, left.Length, right.Length)]), budget, true, "work-limit:input-snapshot");
        // 呼出し後の変更を処理中の同値列へ持ち込まず、コピーも共有予算へ計上する。
        var a = (int[])left.Clone();
        cancellationToken.ThrowIfCancellationRequested();
        var b = (int[])right.Clone();
        cancellationToken.ThrowIfCancellationRequested();
        var result = GnuLineDiffer.Compare(a, b, classCount, budget - copyWork, cancellationToken);
        var used = copyWork + result.WorkUsed;
        if (result.Fallback)
            return new(Array.AsReadOnly([new GnuScriptChange(0, 0, left.Length, right.Length)]), used, true, result.FallbackReason);
        if (result.Changes.Count > budget - used)
            return new(Array.AsReadOnly(left.Length == 0 && right.Length == 0 ? Array.Empty<GnuScriptChange>()
                : [new(0, 0, left.Length, right.Length)]), budget, true, "work-limit:result-snapshot");
        var changes = new GnuScriptChange[result.Changes.Count];
        for (var index = 0; index < changes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = result.Changes[index];
            changes[index] = new(change.LeftStart, change.RightStart, change.LeftCount, change.RightCount);
        }
        return new(Array.AsReadOnly(changes), used + changes.Length, result.Fallback, result.FallbackReason);
    }
}

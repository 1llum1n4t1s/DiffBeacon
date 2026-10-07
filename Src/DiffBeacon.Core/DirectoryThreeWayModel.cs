namespace DiffBeacon.Core;

public enum DirectorySide { Left, Middle, Right }
[Flags]
public enum DirectoryPresence
{
    None = 0, Left = 1, Middle = 2, Right = 4,
    LeftMiddle = Left | Middle, LeftRight = Left | Right, MiddleRight = Middle | Right, All = Left | Middle | Right
}
// 原本の DIFFALL=0 は、全体が差分の場合だけ有効な分類である。
public enum DirectoryThreeWayClassification { AllChanged = 0, OnlyLeft = 1, OnlyMiddle = 2, OnlyRight = 3 }
public sealed record DirectoryPairComparison(DirectorySide First, DirectorySide Second,
    DirectoryDifferenceKind Status, string? Error = null);
public sealed record DirectoryThreeWayComparison(DirectoryPresence Presence,
    DirectoryPairComparison MiddleLeft, DirectoryPairComparison MiddleRight,
    DirectoryPairComparison LeftRight, DirectoryThreeWayClassification? Classification);

public static class DirectorySideMapping
{
    public static (DirectorySide Source, DirectorySide Destination) GetSides(DirectoryCopyDirection direction) => direction switch
    {
        DirectoryCopyDirection.LeftToRight => (DirectorySide.Left, DirectorySide.Right),
        DirectoryCopyDirection.RightToLeft => (DirectorySide.Right, DirectorySide.Left),
        DirectoryCopyDirection.LeftToMiddle => (DirectorySide.Left, DirectorySide.Middle),
        DirectoryCopyDirection.MiddleToLeft => (DirectorySide.Middle, DirectorySide.Left),
        DirectoryCopyDirection.MiddleToRight => (DirectorySide.Middle, DirectorySide.Right),
        DirectoryCopyDirection.RightToMiddle => (DirectorySide.Right, DirectorySide.Middle),
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    public static string GetRoot(DirectoryComparisonResult comparison, DirectorySide side) => side switch
    {
        DirectorySide.Left => comparison.LeftPath,
        DirectorySide.Right => comparison.RightPath,
        DirectorySide.Middle => comparison.MiddlePath
            ?? throw new ArgumentException("二者比較では中央をコピー元・コピー先に指定できません。", nameof(side)),
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    };

    public static DirectorySideSnapshot? GetState(DirectoryEntry entry, DirectorySide side) => side switch
    {
        DirectorySide.Left => entry.LeftState,
        DirectorySide.Middle => entry.MiddleState,
        DirectorySide.Right => entry.RightState,
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    };

    // GetDirCompareFlags3Way の分類集約。Error の親伝播は比較側で別に行う。
    public static DirectoryThreeWayClassification AggregateClassification(DirectoryPresence presence,
        IEnumerable<DirectoryEntry> children, CancellationToken cancellationToken = default)
    {
        DirectoryThreeWayClassification? value = presence switch
        {
            DirectoryPresence.Left or DirectoryPresence.MiddleRight => DirectoryThreeWayClassification.OnlyLeft,
            DirectoryPresence.Middle or DirectoryPresence.LeftRight => DirectoryThreeWayClassification.OnlyMiddle,
            DirectoryPresence.Right or DirectoryPresence.LeftMiddle => DirectoryThreeWayClassification.OnlyRight,
            _ => null
        };
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.IsFiltered || child.Status is not (DirectoryDifferenceKind.Modified or DirectoryDifferenceKind.LeftOnly
                or DirectoryDifferenceKind.MiddleOnly or DirectoryDifferenceKind.RightOnly or DirectoryDifferenceKind.TypeConflict)) continue;
            var classification = child.ThreeWay?.Classification ?? DirectoryThreeWayClassification.AllChanged;
            value = value is null ? classification : value == classification ? value : DirectoryThreeWayClassification.AllChanged;
        }
        return value ?? DirectoryThreeWayClassification.AllChanged;
    }
}

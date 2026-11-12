namespace DiffBeacon.Core;

public sealed record LineTransformListDependency(string? Path, bool Dynamic);

/// <summary>置換リストのホスト読込み。Coreはパスやファイルを解決しない。</summary>
public interface ILineTransformListEnvironment
{
    string ReadList(string path, LineTransformBudget budget, CancellationToken cancellationToken);
}

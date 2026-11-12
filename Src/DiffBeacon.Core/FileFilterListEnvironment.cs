namespace DiffBeacon.Core;

/// <summary>フォルダーフィルターの明示的な置換リスト読込み。パスの解決とI/Oはホストが所有する。</summary>
public interface IFileFilterListEnvironment
{
    string ReadList(string path, FileFilterListReadContext context);
}

/// <summary>リスト読込みも、そのpredicateの共有作業量と取消へ接続する。</summary>
public sealed class FileFilterListReadContext
{
    private readonly Action<int> charge;
    public CancellationToken CancellationToken { get; }

    internal FileFilterListReadContext(Action<int> charge, CancellationToken cancellationToken)
    { this.charge = charge; CancellationToken = cancellationToken; }

    public void Charge(long amount)
    {
        CancellationToken.ThrowIfCancellationRequested();
        charge(amount is >= 0 and <= int.MaxValue ? (int)amount : -1);
    }
}

using DiffBeacon.Providers;

namespace DiffBeacon.App;

// revisionやタブ置換から独立した所有情報。公開/読込assetと検証済みcredentialはwindow終了で解放する。
internal sealed class ArchiveWindowLifetime
{
    private readonly HashSet<string> _assets = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, string?[]> _credentials = new(StringComparer.Ordinal);
    internal string[] Assets => _assets.ToArray();
    internal int CredentialCount => _credentials.Count;
    internal void RegisterAsset(string path) => _assets.Add(Path.GetFullPath(path));
    internal void EnsureOutput(string path)
    {
        if (_assets.Any(asset => ArchivePaths.SameFile(asset, path)))
            throw new InvalidOperationException("公開または読込み済みの作業snapshotを上書きできません。");
    }
    internal static string RouteKey(ArchiveProjectInput input)
    {
        var path = Path.GetFullPath(input.RootPath);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return string.Concat(new[] { path, input.RootSha256?.ToUpperInvariant() ?? "" }.Concat(input.EntryChain.Select(ArchiveProjectInput.CanonicalEntry))
            .Concat(ArchivePayloadSettings.NormalizedChoices(input.ContainerNameCodePages, input.ContainerGZipPayloadKinds, input.EntryChain.Length + 1, compressionPayloadKinds: input.ContainerCompressionPayloadKinds)).Select(part => part.Length + ":" + part));
    }
    internal string?[]? Find(ArchiveProjectInput input) => _credentials.TryGetValue(RouteKey(input), out var values) ? values.ToArray() : null;
    internal void Remember(ArchiveProjectInput input, IReadOnlyList<string?> values)
    {
        if (input.RootSha256 is null || values.Count != input.EntryChain.Length + 1) throw new InvalidDataException("検証済みpassword階層が不正です。");
        var key = RouteKey(input);
        if (_credentials.Remove(key, out var old)) Array.Clear(old);
        // 閉じたtabのcredentialは保持件数を制限し、退避時は秘密配列を解放する。
        if (_credentials.Count >= 256)
        {
            var first = _credentials.First(); Array.Clear(first.Value); _credentials.Remove(first.Key);
        }
        _credentials.Add(key, values.ToArray());
    }
    internal void Clear()
    {
        _assets.Clear(); foreach (var values in _credentials.Values) Array.Clear(values); _credentials.Clear();
    }
}

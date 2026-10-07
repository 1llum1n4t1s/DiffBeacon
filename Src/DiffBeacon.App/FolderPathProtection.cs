using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static partial class FolderPathProtection
{
    internal static ProtectionContext CreateContext() => new();

    internal static bool SameContainer(string first, string second)
        => CreateContext().SameContainer(first, second);

    internal static bool Within(string path, string root)
        => CreateContext().Within(path, root);

    // 一回の公開直前検査だけで共有する。awaitや次の出力検査を越えて再利用しない。
    internal sealed class ProtectionContext
    {
        private const int MaximumPaths = 4096;
        private const int MaximumQueries = 4096;
        private const long MaximumPathCharacters = 8 * 1024 * 1024;
        private readonly Dictionary<string, string> normalized = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> parents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WindowsFileIdentity?> identities = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WindowsLocation> locations = new(StringComparer.Ordinal);
        private long pathCharacters;

        internal bool SameContainer(string first, string second)
        {
            if (!OperatingSystem.IsWindows()) return ArchivePaths.SameFile(ContainerPath(first), ContainerPath(second));
            var a = Normalize(first); var b = Normalize(second);
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
            var firstLocation = Locate(a); var secondLocation = Locate(b);
            return firstLocation.Identity == secondLocation.Identity
                && firstLocation.Tail.Equals(secondLocation.Tail, StringComparison.OrdinalIgnoreCase);
        }

        internal bool Within(string path, string root)
        {
            if (!OperatingSystem.IsWindows())
            {
                root = TrimDirectorySeparators(ContainerPath(root));
                for (var current = TrimDirectorySeparators(ContainerPath(path)); current is not null; current = Path.GetDirectoryName(current))
                    if (ArchivePaths.SameFile(current, root)) return true;
                return false;
            }
            var full = Normalize(path); var rootPath = Normalize(root);
            if (HasPathPrefix(full.AsSpan(), rootPath.AsSpan())) return true;
            var protectedLocation = Locate(rootPath);
            for (var current = full; current is not null; current = Parent(current))
            {
                var candidate = Locate(current);
                if (candidate.Identity == protectedLocation.Identity && HasPathPrefix(candidate.Tail, protectedLocation.Tail)) return true;
            }
            return false;
        }

        private string Normalize(string path)
        {
            if (normalized.TryGetValue(path, out var full)) return full;
            if (normalized.Count >= MaximumPaths) throw BudgetExceeded();
            ConsumeCharacters(path.Length);
            full = TrimDirectorySeparators(ContainerPath(path));
            ConsumeCharacters(full.Length);
            normalized.Add(path, full);
            return full;
        }

        private string? Parent(string path)
        {
            if (parents.TryGetValue(path, out var parent)) return parent;
            // 深いパスの祖先substringも共有し、累積文字数を作成前に制限する。
            ConsumeCharacters(path.Length);
            parent = Path.GetDirectoryName(path);
            if (parent is not null) parent = TrimDirectorySeparators(parent);
            parents.Add(path, parent);
            return parent;
        }

        private WindowsLocation Locate(string path)
        {
            if (locations.TryGetValue(path, out var known)) return known;
            var missing = new List<string>();
            var current = path;
            WindowsLocation anchor;
            while (true)
            {
                if (locations.TryGetValue(current, out anchor)) break;
                var identity = Identity(current);
                if (identity is { } existing)
                {
                    anchor = new(existing, current, current.Length);
                    locations.Add(current, anchor);
                    break;
                }
                missing.Add(current);
                current = Parent(current) ?? throw new IOException("出力保護のための既存ボリュームまたは共有ルートを確認できません。");
            }
            // 不在項目のsuffixは原文spanで保持する。深さごとに連結・切り出ししない。
            var tailStart = anchor.TailStart;
            if (tailStart == anchor.Path.Length && !Path.EndsInDirectorySeparator(anchor.Path)) tailStart++;
            foreach (var absent in missing) locations.Add(absent, new(anchor.Identity, absent, tailStart));
            return locations[path];
        }

        private WindowsFileIdentity? Identity(string path)
        {
            if (identities.TryGetValue(path, out var identity)) return identity;
            if (identities.Count >= MaximumQueries) throw BudgetExceeded();
            ConsumeCharacters(path.Length);
            identity = ReadWindowsIdentity(path);
            identities.Add(path, identity);
            return identity;
        }

        private void ConsumeCharacters(int count)
        {
            if (pathCharacters + count > MaximumPathCharacters) throw BudgetExceeded();
            pathCharacters += count;
        }

        private static IOException BudgetExceeded() => new("出力保護のパス確認上限を超えました。比較タブ数またはパスの深さを減らしてください。");

        private static bool HasPathPrefix(ReadOnlySpan<char> path, ReadOnlySpan<char> root)
            => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (path.Length == root.Length || root.IsEmpty || Path.EndsInDirectorySeparator(root)
                    || Path.EndsInDirectorySeparator(path.Slice(root.Length, 1)));

        private readonly record struct WindowsLocation(WindowsFileIdentity Identity, string Path, int TailStart)
        {
            internal ReadOnlySpan<char> Tail => Path.AsSpan(TailStart);
        }
    }

    private static string TrimDirectorySeparators(string path)
    {
        // extended表記では複数の末尾separatorが残る。volume rootを保ち一度だけ切り出す。
        var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        var length = path.Length;
        while (length > rootLength && Path.EndsInDirectorySeparator(path.AsSpan(0, length))) length--;
        return length == path.Length ? path : path[..length];
    }

    internal static string ContainerPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) return full;
        // extended/DOS deviceのdrive・UNC別名を、保護比較では通常の絶対pathに統一する。
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            if (full.AsSpan(4).StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
                full = @"\\" + full[8..];
            else if (full.Length >= 7 && char.IsAsciiLetter(full[4]) && full[5] == ':' && full[6] == '\\')
                full = full[4..];
            else
                throw new InvalidOperationException("このデバイス名前空間は出力保護で扱えません。ドライブ文字またはUNCのパスを指定してください。");
        }
        // basefileの置換は、そのfileを入力とする全named streamにも影響する。
        var rootLength = Path.GetPathRoot(full)!.Length;
        var streamSeparator = full.IndexOf(':', rootLength);
        return streamSeparator < 0 ? full : full[..streamSeparator];
    }
}

using System.Text;

namespace DiffBeacon.Core;

// 固定74c2ae io.c find_and_hash_each_line、util.c line_cmpのC-profile接続。
// GNU GPL-2.0-or-later。原本のbucket候補順（新class先頭）と長さ条件を保つ。
internal static class RawByteEquivalence
{
    internal sealed record Observation(uint Hash, int Length, int ClassId);
    internal sealed record Classified(GnuLineKey[] Left, GnuLineKey[] Right,
        PreparedUtf8 A, PreparedUtf8 B, Observation[][] Observations);

    internal static Classified Classify(string a, string b, ComparisonOptions options,
        Action<long> spend, Action<long> reserve,
        FourPaneTextProfile textProfile = FourPaneTextProfile.LegacyRawUtf8,
        CancellationToken token = default)
    {
        var flags = FourPaneByteBridge.NativeFlags(options, textProfile);
        var productBody = textProfile == FourPaneTextProfile.DecodedBody;
        if (options.IgnoreFinalNewLine && !productBody)
            throw new ArgumentException("fixed GNU has no IgnoreFinalNewLine flag");
        var pa = PreparedUtf8.Prepare(a, flags.HasFlag(NativeLineFlags.IgnoreEol), spend, reserve, textProfile);
        var pb = PreparedUtf8.Prepare(b, flags.HasFlag(NativeLineFlags.IgnoreEol), spend, reserve, textProfile);
        if (productBody) return ClassifyProductBody(pa, pb, options, spend, reserve, token);
        var varies = flags.HasFlag(NativeLineFlags.IgnoreAllSpace) || flags.HasFlag(NativeLineFlags.IgnoreSpaceChange);
        var buckets = new Dictionary<uint, List<(byte[] Bytes, int Length, int Id)>>();
        int next = 1;
        var observations = new Observation[2][];
        var ka = One(pa, 0); var kb = One(pb, 1);
        return new(ka, kb, pa, pb, observations);

        GnuLineKey[] One(PreparedUtf8 p, int side)
        {
            reserve(checked(48L * p.Lines.Length));
            var keys = new GnuLineKey[p.Lines.Length];
            observations[side] = new Observation[p.Lines.Length];
            for (int i = 0; i < p.Lines.Length; i++)
            {
                spend(1);
                var bytes = p.Lines[i];
                var hash = Hash(bytes, flags, spend);
                var length = bytes.Length - (i == p.Lines.Length - 1 && p.MissingNewline ? 1 : 0);
                if (!buckets.TryGetValue(hash, out var list)) buckets.Add(hash, list = []);
                var id = 0;
                for (int k = list.Count - 1; k >= 0; k--)
                {
                    spend(1);
                    var entry = list[k];
                    if ((varies || entry.Length == length) &&
                        LegacyLineComparator.Compare(entry.Bytes.AsSpan(0, entry.Length), bytes.AsSpan(0, length),
                            flags, () => spend(1)) == 0)
                    { id = entry.Id; break; }
                }
                if (id == 0) { id = next++; list.Add((bytes, length, id)); }
                // string APIへ同値classだけを注入。Unicode正規化は呼ばない。
                keys[i] = new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), "", false);
                observations[side][i] = new(hash, length, id);
            }
            return keys;
        }
    }

    private static Classified ClassifyProductBody(PreparedUtf8 a, PreparedUtf8 b, ComparisonOptions options,
        Action<long> spend, Action<long> reserve, CancellationToken token)
    {
        var classes = new Dictionary<GnuLineKey, int>();
        var observations = new Observation[2][];
        var left = One(a, 0); var right = One(b, 1);
        return new(left, right, a, b, observations);

        GnuLineKey[] One(PreparedUtf8 input, int side)
        {
            reserve(checked(48L * input.Lines.Length));
            var keys = new GnuLineKey[input.Lines.Length];
            observations[side] = new Observation[input.Lines.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                token.ThrowIfCancellationRequested(); spend(1);
                var key = LegacyLineComparator.ProductKey(input.Lines[i],
                    i == keys.Length - 1 && input.MissingNewline, options, spend, reserve, token);
                // 製品キーのhash/equalityも同じ共有作業枠へ計上する。
                spend((long)key.Content.Length + key.Ending.Length + 1);
                if (!classes.TryGetValue(key, out var id))
                {
                    reserve(32); id = classes.Count + 1; classes.Add(key, id);
                }
                uint hash = 2166136261;
                foreach (var c in key.Content) { spend(1); hash = unchecked((hash ^ c) * 16777619); }
                foreach (var c in key.Ending) { spend(1); hash = unchecked((hash ^ c) * 16777619); }
                spend(1); hash = unchecked((hash ^ (key.MissingFinalNewline ? 1u : 0u)) * 16777619);
                keys[i] = new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), "", false);
                observations[side][i] = new(hash, input.Lines[i].Length, id);
            }
            return keys;
        }
    }

    internal static uint Hash(byte[] bytes, NativeLineFlags flags, Action<long> spend)
    {
        uint hash = 0;
        var index = 0;
        while (index < bytes.Length)
        {
            spend(1);
            var c = bytes[index++];
            var next = index < bytes.Length ? bytes[index] : (byte)0;
            if (c == 10 || c == 13 && next != 10) break;
            if (flags.HasFlag(NativeLineFlags.IgnoreAllSpace))
            {
                if (LegacyLineComparator.IsHorizontalSpace(c)) continue;
            }
            else if (flags.HasFlag(NativeLineFlags.IgnoreSpaceChange) && LegacyLineComparator.IsHorizontalSpace(c))
            {
                do { spend(1); c = index < bytes.Length ? bytes[index++] : (byte)0; }
                while (LegacyLineComparator.IsHorizontalSpace(c));
                if (c == 10) break;
                if (c != 13) Add(32);
            }
            if (!(flags.HasFlag(NativeLineFlags.IgnoreNumbers) && LegacyLineComparator.IsCDigit(c)))
                Add(flags.HasFlag(NativeLineFlags.IgnoreCase) ? LegacyLineComparator.CToLower(c) : c);
            if (flags.HasFlag(NativeLineFlags.IgnoreSpaceChange) && c == 13 &&
                (index >= bytes.Length || bytes[index] != 10)) break;
        }
        return hash;
        void Add(byte c) => hash = unchecked(c + ((hash << 7) | (hash >> 25)));
    }

    internal static TextLine[] BoundaryLines(PreparedUtf8 p)
        => p.Lines.Select((bytes, i) => new TextLine(Encoding.Latin1.GetString(bytes,
            0, bytes.Length - (i == p.Lines.Length - 1 && p.MissingNewline ? 1 : 0)), "")).ToArray();
}

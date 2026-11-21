namespace DiffBeacon.Providers;

public sealed partial class ManagedArchive
{
    private static bool IsSingleCompressionName(string name) =>
        name.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".bzip2", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".Z", StringComparison.OrdinalIgnoreCase);

    private static string? DetectSingleCompression(Stream input)
    {
        input.Position = 0;
        Span<byte> header = stackalloc byte[4];
        var count = input.ReadAtLeast(header, 4, throwOnEndOfStream: false);
        input.Position = 0;
        if (count == 4 && header[..3].SequenceEqual("BZh"u8) && header[3] is >= (byte)'1' and <= (byte)'9')
            return "bzip2";
        return count >= 2 && header[0] == 0x1f && header[1] == 0x9d ? "Z" : null;
    }

    private ManagedArchiveManifest ReadCompression(Stream input, string logicalName, string format,
        CompressionPayloadKind payloadKind, CancellationToken token, Func<ManagedArchiveEntry, bool>? capture,
        Action<ManagedArchiveEntry, MemoryStream?>? consume, long? captureLimit, bool prefixOnly,
        ArchiveReadBudget? sharedBudget, OwnedEntryCapture? ownedCapture)
    {
        var budget = sharedBudget ?? new ArchiveReadBudget(_limits);
        budget.Layers(1);
        using var workInput = sharedBudget is null ? new WorkReadStream(input, budget, token) : null;
        var compressed = workInput ?? input;
        // Zにはchecksum・宣言長・明示EOFがない。既存decoderの構造検査と入力末尾を境界とする。
        // 正規Zの未使用padding bitをzero必須にせず、BZip2は全member/CRCを既存decoderで検査する。
        if (format == "Z") budget.Item();
        using Stream decoder = format == "bzip2" ? new VerifiedBZip2Stream(compressed, token, budget)
            : new ZReadStream(compressed, token, leaveOpen: true);
        var buffer = new byte[64 * 1024];
        var count = decoder.ReadAtLeast(buffer.AsSpan(0, 512), 512, throwOnEndOfStream: false);
        if (payloadKind == CompressionPayloadKind.Tar ||
            payloadKind == CompressionPayloadKind.Auto && count == 512 && IsTarHeader(buffer.AsSpan(0, 512)))
            return ReadCompressionTar(compressed, decoder, format, buffer, count, token, capture, consume,
                captureLimit, prefixOnly, budget, ownedCapture);

        // BZip2/ZにはFNAMEがない。旧GetDefaultNameと既存gzipのfallbackと同じ物理名境界。
        var decodedName = Path.GetFileName(logicalName.Replace('\\', '/'));
        var dot = decodedName.LastIndexOf('.');
        decodedName = dot >= 0 ? decodedName[..dot] : "noname";
        var name = ValidateEntryPath(decodedName);
        var names = new EntryNames(_limits.MaximumEntries, _limits.MaximumPathCharacters, budget);
        names.Add(name, false);
        var provisional = new ManagedArchiveEntry(name, false, 0, "", false, null);
        var wants = ownedCapture?.Wants(provisional) == true || capture?.Invoke(provisional) == true;
        var limit = Math.Min(_limits.MaximumEntryBytes, _limits.MaximumDecodedBytes);
        var retainedLimit = captureLimit ?? _limits.MaximumPreviewBytes;
        MemoryStream? content = wants ? prefixOnly ? new PrefixMemoryStream(checked((int)retainedLimit)) : new BoundedCaptureStream(checked((int)Math.Min(limit, retainedLimit))) : null;
        var transferred = false;
        try
        {
            using var sink = new DecodedSink(content, limit, token, budget);
            while (count != 0)
            {
                sink.Write(buffer, 0, count);
                count = decoder.Read(buffer);
            }
            token.ThrowIfCancellationRequested();
            if (compressed.Position != compressed.Length)
                throw new InvalidDataException("BZip2/Zの後続内容を完全に検証できませんでした。");
            decoder.Dispose();
            var entry = provisional with { Size = sink.Length, Sha256 = sink.Hash() };
            if (content is not null) content.Position = 0;
            if (ownedCapture is not null) transferred = ownedCapture.Take(entry, content);
            else consume?.Invoke(entry, content);
            return new(format, new[] { entry });
        }
        finally { if (!transferred) content?.Dispose(); }
    }

    private ManagedArchiveManifest ReadCompressionTar(Stream input, Stream decoder, string format, byte[] buffer,
        int count, CancellationToken token, Func<ManagedArchiveEntry, bool>? capture,
        Action<ManagedArchiveEntry, MemoryStream?>? consume, long? captureLimit, bool prefixOnly,
        ArchiveReadBudget budget, OwnedEntryCapture? ownedCapture)
    {
        // 全復号の完走前にはTARのconsume/owned captureを公開しない。単entry上限でTAR全体は制限しない。
        using var decoded = new MemoryStream();
        var limit = Math.Min(_limits.MaximumDecodedBytes, int.MaxValue);
        while (count != 0)
        {
            token.ThrowIfCancellationRequested();
            if (count > limit - decoded.Length) throw new InvalidDataException("展開サイズの上限を超えました。");
            budget.Decoded(count);
            var needed = checked((int)decoded.Length + count);
            if (needed > decoded.Capacity)
                decoded.Capacity = (int)Math.Min(limit, Math.Max(needed, (long)decoded.Capacity * 2));
            decoded.Write(buffer, 0, count);
            var remaining = Math.Min(limit - decoded.Length, budget.DecodedRemaining);
            count = decoder.Read(buffer, 0, remaining < buffer.Length ? checked((int)remaining + 1) : buffer.Length);
        }
        token.ThrowIfCancellationRequested();
        if (input.Position != input.Length)
            throw new InvalidDataException("BZip2/Zの後続内容を完全に検証できませんでした。");
        decoder.Dispose();
        decoded.Position = 0;
        using var tarInput = new WorkReadStream(decoded, budget, token);
        return ReadTar(tarInput, "tar", token, capture, consume, captureLimit, prefixOnly,
            budget, ownedCapture, inputAlreadyDecoded: true) with { Format = format == "bzip2" ? "tar.bz2" : "tar.Z" };
    }
}
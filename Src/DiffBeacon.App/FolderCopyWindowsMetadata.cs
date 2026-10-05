using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DiffBeacon.App;

// source/destination/parents と再照合を含む一つの計画から、同じ共有予約先を渡す。
// 拒否済み予約を許可へ読み替えない。途中失敗の予約は呼出し元が計画ごと破棄する。
internal interface IFolderCopyWindowsMetadataBudget
{
    void ReserveRetainedBytes(long bytes);
    void ReserveQueryWork(long bytes, int nativeQueries);
}

/// <summary>
/// ローカル NTFS の観測値。転送・公開の資格を示さず、SACL と128bit IDは取得しない。
/// raw値は保存用、内容比較はoffset/空きpaddingを除いた正規形で行う。
/// </summary>
internal sealed unsafe partial class FolderCopyWindowsMetadata : IEquatable<FolderCopyWindowsMetadata>
{
    internal const int MaximumSecurityBytes = 65536;
    internal const int MaximumEaBytes = 65536;
    internal const uint CapturedSecurityInformation = 7; // OWNER | GROUP | DACL。SACLは不在とみなさない。
    internal const ushort MeaningfulSecurityControlMask = 0x150F; // owner/group default + DACL present/default/auto-request/auto-inherited/protected。
    internal const string QualificationScope = "LocalNTFS;VolumeSerial32+FileIndex64;OwnerGroupDacl;DirectoryLogicalSizeZero;SaclUnobserved;OwnerChangeUnqualified;NonAsciiEaCaseUnqualified;NoPublicationQualification";
    internal const string CloseFailureDataKey = "FolderCopyWindowsMetadata.CloseHandleFailure";
    private const int MaximumEaEntries = MaximumEaBytes / 10 + 1;
    internal const int RetainedObjectAllowance = 1024;
    internal const int MaximumCallerScratchBytes = MaximumSecurityBytes + MaximumEaBytes + MaximumEaEntries * sizeof(int);
    // 既存PortableWindowsFileAttributesと同じ集合。Normalは他属性なしの場合だけ適用可能。
    private const uint OrdinaryAttributeMask = 0x1 | 0x2 | 0x4 | 0x20 | 0x80 | 0x100 | 0x1000 | 0x2000;
    private const uint ReadMetadataAccess = 0x00020000 | 0x00000008 | 0x00000080; // READ_CONTROL/FILE_READ_EA/FILE_READ_ATTRIBUTES。
    [ThreadStatic] private static bool _observing;
    private readonly byte[] _rawSecurity, _rawEa, _security, _ea;

    private FolderCopyWindowsMetadata(NativeFileInformation info, byte[] rawSecurity, byte[] rawEa,
        byte[] security, byte[] ea, long retainedBytes)
    {
        // private factoryだけが新規所有配列を渡す。外部配列の所有権を取得するAPIは設けない。
        CreationTime = info.Creation.Value;
        LastWriteTime = info.Write.Value;
        Size = checked((long)info.Size);
        ObservedAttributes = (FileAttributes)info.Attributes;
        VolumeSerial = info.Volume;
        FileIndex = info.Index;
        _rawSecurity = rawSecurity;
        _rawEa = rawEa;
        _security = security;
        _ea = ea;
        RetainedBytes = retainedBytes;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> fields = stackalloc byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(fields, CreationTime);
        BinaryPrimitives.WriteUInt64LittleEndian(fields[8..], LastWriteTime);
        BinaryPrimitives.WriteInt64LittleEndian(fields[16..], Size);
        BinaryPrimitives.WriteUInt32LittleEndian(fields[24..], info.Attributes);
        BinaryPrimitives.WriteUInt32LittleEndian(fields[28..], VolumeSerial);
        BinaryPrimitives.WriteUInt64LittleEndian(fields[32..], FileIndex);
        hash.AppendData(fields);
        hash.AppendData(_security);
        hash.AppendData(_ea);
        Digest = Convert.ToHexString(hash.GetHashAndReset());
    }

    internal ulong CreationTime { get; }
    internal ulong LastWriteTime { get; }
    internal long Size { get; }
    internal FileAttributes ObservedAttributes { get; }
    internal FileAttributes OrdinaryAttributes => ObservedAttributes & (FileAttributes)OrdinaryAttributeMask;
    internal bool IsDirectory => (ObservedAttributes & FileAttributes.Directory) != 0;
    internal uint VolumeSerial { get; }
    internal ulong FileIndex { get; }
    internal ushort SecurityControl => BinaryPrimitives.ReadUInt16LittleEndian(_security.AsSpan(2));
    internal bool SaclCaptured => false;
    internal long RetainedBytes { get; }
    internal long RetainedPayloadBytes => (long)_rawSecurity.Length + _rawEa.Length + _security.Length + _ea.Length;
    internal string Digest { get; }
    internal ReadOnlySpan<byte> RawSecurityDescriptor => _rawSecurity;
    internal ReadOnlySpan<byte> RawExtendedAttributes => _rawEa;
    internal ReadOnlySpan<byte> CanonicalSecurity => _security;
    internal ReadOnlySpan<byte> CanonicalExtendedAttributes => _ea;

    internal static FolderCopyWindowsMetadata Capture(string validatedAbsolutePath,
        IFolderCopyWindowsMetadataBudget budget, CancellationToken token, Action? validateContext = null) =>
        Observe(validatedAbsolutePath, budget, token, validateContext, null).Snapshot!;

    internal static void RequireUnchanged(string validatedAbsolutePath, FolderCopyWindowsMetadata expected,
        IFolderCopyWindowsMetadataBudget budget, CancellationToken token, Action? validateContext = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        // 単件fixed caller buffersで直接比較し、expected/全計画の配列を再複製しない。
        _ = Observe(validatedAbsolutePath, budget, token, validateContext, expected);
    }

    internal static void RequireUnchanged(string validatedAbsolutePath, FolderCopyWindowsMetadata expected,
        ulong expectedWriteTimeOverride, IFolderCopyWindowsMetadataBudget budget, CancellationToken token, Action? validateContext = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        _ = Observe(validatedAbsolutePath, budget, token, validateContext, expected, expectedWriteTimeOverride);
    }

    internal static ulong RefreshOwnedParentMtime(string validatedAbsolutePath, FolderCopyWindowsMetadata expected,
        ulong currentExpectedWriteTime, IFolderCopyWindowsMetadataBudget budget, CancellationToken token, Action? validateContext = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!expected.IsDirectory) throw new ArgumentException("既知namespace変更後の親mtime更新はdirectory専用です。", nameof(expected));
        // mtimeだけを既知変更として受け入れる。外部mtime変更との識別は保証しない。
        return Observe(validatedAbsolutePath, budget, token, validateContext, expected, currentExpectedWriteTime, true).WriteTime;
    }

    private readonly record struct Observation(FolderCopyWindowsMetadata? Snapshot, ulong WriteTime);

    private static Observation Observe(string path, IFolderCopyWindowsMetadataBudget budget,
        CancellationToken token, Action? context, FolderCopyWindowsMetadata? expected, ulong? writeOverride = null, bool refreshWrite = false)
    {
        // 共有予算/context callbackからの再入も、大きいcaller frameを重ねる前に拒否する。
        if (_observing) throw new InvalidOperationException("metadata capture中の同一threadへの再入はできません。");
        _observing = true;
        try { return ObserveCore(path, budget, token, context, expected, writeOverride, refreshWrite); }
        finally { _observing = false; }
    }

    private static Observation ObserveCore(string path, IFolderCopyWindowsMetadataBudget budget,
        CancellationToken token, Action? context, FolderCopyWindowsMetadata? expected, ulong? writeOverride, bool refreshWrite)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows metadata captureはWindows専用です。");
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentException.ThrowIfNullOrEmpty(path);
        Guard(token, context);
        // パス変換と祖先文字列にも共有work予約を先に要求する。
        budget.ReserveQueryWork(checked(4L * path.Length + 64), 0);
        Guard(token, context);
        var nativePath = FolderCopyWindowsStreams.ExtendedPath(path);
        if (nativePath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("SMB/UNCのWindows metadata captureは未資格です。");
        ValidateLocalPath(nativePath);
        BeforeQuery(budget, 4, token, context);
        var driveType = ReadDriveType(nativePath[..7]);
        Guard(token, context);
        if (driveType is not (2 or 3)) throw new NotSupportedException("ローカルの固定/リムーバブルドライブ以外はmetadata capture未資格です。");
        RejectReparseAncestors(nativePath, budget, token, context);
        BeforeOwnedOpen(budget, token, context);
        var handle = OpenFile(nativePath, ReadMetadataAccess, 7, 0, 3, 0x00200000 | 0x02000000, 0);
        var openError = Marshal.GetLastPInvokeError();
        if (handle == -1)
        {
            Guard(token, context);
            throw NativeFailure("CreateFileW(metadata)", openError);
        }
        Exception? primaryFailure = null;
        try
        {
            Guard(token, context);
            RequireNtfs(handle, budget, token, context);
            var before = QueryInformation(handle, budget, token, context);
            ValidateInformation(before);
            // security queryの共有予約は大きい単件scratch frameへ入る前に済ませる。
            BeforeQuery(budget, MaximumSecurityBytes, token, context);
            var result = ObserveOpened(handle, before, budget, token, context, expected, writeOverride, refreshWrite);
            Guard(token, context);
            RejectReparseAncestors(nativePath, budget, token, context);
            RequirePathIdentity(nativePath, before, budget, token, context);
            return new(result, before.Write.Value);
        }
        catch (Exception exception) { primaryFailure = exception; throw; }
        finally { CheckedClose(handle, primaryFailure); }
    }

    private static FolderCopyWindowsMetadata? ObserveOpened(nint handle, NativeFileInformation before,
        IFolderCopyWindowsMetadataBudget budget, CancellationToken token, Action? context, FolderCopyWindowsMetadata? expected,
        ulong? writeOverride = null, bool refreshWrite = false)
    {
        // 非再帰の単件処理。同時stack scratchの上限は約154KiB (64KiB+64KiB+6554*sizeof(int))。
        // query予約拒否時はこの大きいframeへ入る前/各native call前に止める。
        Span<byte> securityBuffer = stackalloc byte[MaximumSecurityBytes];
        int securityResult;
        uint securityLength;
        fixed (byte* p = securityBuffer)
            securityResult = ReadSecurity(handle, CapturedSecurityInformation, p, MaximumSecurityBytes, out securityLength);
        var securityError = Marshal.GetLastPInvokeError();
        Guard(token, context);
        if (securityResult == 0) throw NativeFailure("GetKernelObjectSecurity(caller buffer)", securityError);
        if (securityLength is < 20 or > MaximumSecurityBytes) throw Malformed("security descriptorの返却長");
        var rawSecurity = securityBuffer[..(int)securityLength];
        var securityLayout = ValidateSecurity(rawSecurity);

        BeforeQuery(budget, MaximumEaBytes, token, context);
        Span<byte> eaBuffer = stackalloc byte[MaximumEaBytes];
        NativeIoStatus io = default;
        int eaStatus;
        fixed (byte* p = eaBuffer)
            eaStatus = ReadExtendedAttributes(handle, ref io, p, MaximumEaBytes, 0, 0, 0, 0, 1);
        Guard(token, context);
        int eaLength;
        if (unchecked((uint)eaStatus) == 0xC0000052) eaLength = 0; // STATUS_NO_EAS_ON_FILEだけが空EA。
        else
        {
            if (eaStatus != 0) throw new IOException($"NtQueryEaFile(caller buffer)に失敗しました (NTSTATUS {unchecked((uint)eaStatus):X8})。");
            if (io.Information > MaximumEaBytes) throw Malformed("EAの返却長");
            eaLength = checked((int)io.Information);
        }
        var rawEa = eaBuffer[..eaLength];
        Span<int> offsets = stackalloc int[MaximumEaEntries];
        var eaLayout = ValidateEa(rawEa, offsets);
        var entries = offsets[..eaLayout.Entries];
        // 並べ替え/比較の最悪名長も共有workへ計上する。SD/EAは最大64KiB。
        var levels = eaLayout.Entries <= 1 ? 0 : 32 - int.LeadingZeroCount(eaLayout.Entries);
        budget.ReserveQueryWork(checked(4L * securityLength + 4L * eaLength + 512L * eaLayout.Entries * levels), 0);
        Guard(token, context);
        SortEa(rawEa, entries);
        for (var index = 1; index < entries.Length; index++)
            if (CompareEaNames(rawEa, entries[index - 1], entries[index]) == 0) throw Malformed("EA名の重複");
        var after = QueryInformation(handle, budget, token, context);
        if (!SameInformation(before, after)) throw new IOException("metadata取得中にidentity/creation/mtime/size/属性が変わりました。");
        if (expected is not null)
        {
            var securityComparison = CanonicalSink.Compare(expected._security);
            WriteSecurity(rawSecurity, securityLayout, ref securityComparison);
            var eaComparison = CanonicalSink.Compare(expected._ea);
            WriteEa(rawEa, entries, ref eaComparison);
            Guard(token, context);
            if (!expected.Matches(before, writeOverride, refreshWrite) || !securityComparison.Equal || !eaComparison.Equal)
                throw new IOException("Windows metadataが確認値から変わりました。");
            return null;
        }

        var retained = checked((long)rawSecurity.Length + rawEa.Length + securityLayout.CanonicalLength + eaLayout.CanonicalLength + RetainedObjectAllowance);
        Guard(token, context);
        budget.ReserveRetainedBytes(retained);
        Guard(token, context);
        // 可変長配列/正規形/digestは全計画共有予約の成功後だけ作る。
        var ownedSecurity = rawSecurity.ToArray();
        var ownedEa = rawEa.ToArray();
        var canonicalSecurity = new byte[securityLayout.CanonicalLength];
        var securityWriter = new CanonicalSink(canonicalSecurity);
        WriteSecurity(rawSecurity, securityLayout, ref securityWriter);
        var canonicalEa = new byte[eaLayout.CanonicalLength];
        var eaWriter = new CanonicalSink(canonicalEa);
        WriteEa(rawEa, entries, ref eaWriter);
        Guard(token, context);
        return new(before, ownedSecurity, ownedEa, canonicalSecurity, canonicalEa, retained);
    }

    private readonly record struct SecurityLayout(int Owner, int OwnerLength, int Group, int GroupLength,
        int Dacl, int Aces, int CanonicalLength, byte AclRevision, byte DaclKind, ushort Control);
    private readonly record struct EaLayout(int Entries, int CanonicalLength);

    private static SecurityLayout ValidateSecurity(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 20 || raw[0] != 1) throw Malformed("security descriptor revision/header");
        var control = BinaryPrimitives.ReadUInt16LittleEndian(raw[2..]);
        if ((control & 0x8000) == 0) throw Malformed("self-relativeではないsecurity descriptor");
        // 特殊private/RM controlの適用意味は未資格。無言で落とさない。
        if ((control & 0x40C0) != 0) throw new NotSupportedException("RM/private security descriptor controlのcaptureは未資格です。");
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]) != 0)
            throw new NotSupportedException("要求していないSACLが含まれるsecurity descriptorは採用できません。");
        var owner = Offset(raw, 4);
        var group = Offset(raw, 8);
        var ownerLength = ValidateSid(raw, owner);
        var groupLength = ValidateSid(raw, group);
        RejectPartialOverlap(owner, ownerLength, group, groupLength);
        var dacl = Offset(raw, 16, true);
        var kind = (control & 4) == 0 ? (byte)0 : dacl == 0 ? (byte)1 : (byte)2;
        if (kind == 0 && dacl != 0) throw Malformed("DACL presentとoffsetの矛盾");
        var count = 0;
        byte revision = 0;
        var canonicalLength = checked(20 + ownerLength + groupLength);
        if (dacl != 0)
        {
            if (dacl > raw.Length - 8) throw Malformed("ACL header範囲");
            revision = raw[dacl];
            if (revision is not (2 or 4)) throw Malformed("ACL revision");
            var size = BinaryPrimitives.ReadUInt16LittleEndian(raw[(dacl + 2)..]);
            if (size < 8 || dacl > raw.Length - size) throw Malformed("ACL size範囲");
            RejectPartialOverlap(owner, ownerLength, dacl, size, allowExact: false);
            RejectPartialOverlap(group, groupLength, dacl, size, allowExact: false);
            count = BinaryPrimitives.ReadUInt16LittleEndian(raw[(dacl + 4)..]);
            var cursor = dacl + 8;
            for (var index = 0; index < count; index++)
            {
                if (cursor > dacl + size - 4) throw Malformed("ACE header範囲");
                var length = BinaryPrimitives.ReadUInt16LittleEndian(raw[(cursor + 2)..]);
                if (length < 4 || (length & 3) != 0 || cursor > dacl + size - length) throw Malformed("ACE size範囲");
                // 型/flagsを含む各ACEの全bytesと順序を保持し、ACL末尾の空き領域は捨てる。
                canonicalLength = checked(canonicalLength + 4 + length);
                cursor += length;
            }
        }
        return new(owner, ownerLength, group, groupLength, dacl, count, canonicalLength, revision, kind,
            (ushort)(control & MeaningfulSecurityControlMask));
    }

    private static int Offset(ReadOnlySpan<byte> raw, int headerOffset, bool allowNull = false)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(raw[headerOffset..]);
        if (value == 0 && allowNull) return 0;
        if (value < 20 || value >= raw.Length || (value & 3) != 0) throw Malformed("security component offset");
        return checked((int)value);
    }

    private static int ValidateSid(ReadOnlySpan<byte> raw, int offset)
    {
        if (offset > raw.Length - 8 || raw[offset] != 1 || raw[offset + 1] > 15) throw Malformed("SID header範囲/revision");
        var length = 8 + 4 * raw[offset + 1];
        if (offset > raw.Length - length) throw Malformed("SID subauthority範囲");
        return length;
    }

    private static void RejectPartialOverlap(int left, int leftLength, int right, int rightLength, bool allowExact = true)
    {
        if (allowExact && left == right && leftLength == rightLength) return;
        if (left < right + rightLength && right < left + leftLength) throw Malformed("security component範囲の重複");
    }

    private static void WriteSecurity(scoped ReadOnlySpan<byte> raw, SecurityLayout layout, ref CanonicalSink writer)
    {
        writer.Byte(1); writer.Byte(0); writer.Ushort(layout.Control);
        writer.Uint((uint)layout.OwnerLength); writer.Uint((uint)layout.GroupLength);
        writer.Byte(layout.DaclKind); writer.Byte(layout.AclRevision); writer.Ushort(0); writer.Uint((uint)layout.Aces);
        writer.Bytes(raw.Slice(layout.Owner, layout.OwnerLength)); writer.Bytes(raw.Slice(layout.Group, layout.GroupLength));
        var cursor = layout.Dacl + 8;
        for (var index = 0; index < layout.Aces; index++)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(raw[(cursor + 2)..]);
            writer.Uint(length); writer.Bytes(raw.Slice(cursor, length)); cursor += length;
        }
    }

    private static EaLayout ValidateEa(ReadOnlySpan<byte> raw, Span<int> offsets)
    {
        var count = 0;
        var canonicalLength = 4;
        var cursor = 0;
        while (raw.Length != 0)
        {
            if (cursor > raw.Length - 8 || count >= offsets.Length) throw Malformed("EA header/count範囲");
            var next = BinaryPrimitives.ReadUInt32LittleEndian(raw[cursor..]);
            var flags = raw[cursor + 4];
            var nameLength = raw[cursor + 5];
            var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(raw[(cursor + 6)..]);
            var length = 9 + nameLength + valueLength;
            if (nameLength == 0 || flags is not (0 or 0x80) || cursor > raw.Length - length
                || raw[cursor + 8 + nameLength] != 0) throw Malformed("EA name/flags/value範囲");
            foreach (var character in raw.Slice(cursor + 8, nameLength))
                if (character == 0) throw Malformed("EA name内のNUL");
            offsets[count++] = cursor;
            canonicalLength = checked(canonicalLength + 4 + nameLength + valueLength);
            if (next == 0)
            {
                if (cursor + length != raw.Length) throw Malformed("最終EAの宣言外tail");
                break;
            }
            if ((next & 3) != 0 || next < length || next > raw.Length - cursor - 8) throw Malformed("EA next-entry範囲/整列");
            cursor += (int)next;
        }
        return new(count, canonicalLength);
    }

    private static byte FoldEaName(byte value) => value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;

    private static int CompareEaNames(ReadOnlySpan<byte> raw, int left, int right)
    {
        var leftLength = raw[left + 5]; var rightLength = raw[right + 5];
        for (var index = 0; index < Math.Min(leftLength, rightLength); index++)
        {
            var difference = FoldEaName(raw[left + 8 + index]) - FoldEaName(raw[right + 8 + index]);
            if (difference != 0) return difference;
        }
        return leftLength - rightLength;
    }

    private static void SortEa(ReadOnlySpan<byte> raw, Span<int> offsets)
    {
        // 単件fixed scratchのheapsort。比較delegate/辞書/entry配列を追加確保しない。
        for (var index = offsets.Length / 2 - 1; index >= 0; index--) SiftEa(raw, offsets, index, offsets.Length);
        for (var length = offsets.Length - 1; length > 0; length--)
        {
            (offsets[0], offsets[length]) = (offsets[length], offsets[0]);
            SiftEa(raw, offsets, 0, length);
        }
    }

    private static void SiftEa(ReadOnlySpan<byte> raw, Span<int> offsets, int root, int length)
    {
        while (root < length / 2)
        {
            var child = root * 2 + 1;
            if (child + 1 < length && CompareEaNames(raw, offsets[child], offsets[child + 1]) < 0) child++;
            if (CompareEaNames(raw, offsets[root], offsets[child]) >= 0) return;
            (offsets[root], offsets[child]) = (offsets[child], offsets[root]); root = child;
        }
    }

    private static void WriteEa(scoped ReadOnlySpan<byte> raw, scoped ReadOnlySpan<int> offsets, ref CanonicalSink writer)
    {
        writer.Uint((uint)offsets.Length);
        foreach (var offset in offsets)
        {
            var nameLength = raw[offset + 5]; var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(raw[(offset + 6)..]);
            writer.Byte(raw[offset + 4]); writer.Byte(nameLength); writer.Ushort(valueLength);
            foreach (var value in raw.Slice(offset + 8, nameLength)) writer.Byte(FoldEaName(value));
            writer.Bytes(raw.Slice(offset + 9 + nameLength, valueLength));
        }
    }

    private ref struct CanonicalSink
    {
        private Span<byte> _output;
        private ReadOnlySpan<byte> _expected;
        private bool _compare;
        private int _position;
        private bool _equal;
        internal CanonicalSink(Span<byte> output) { _output = output; _expected = default; _compare = false; _position = 0; _equal = true; }
        internal static CanonicalSink Compare(ReadOnlySpan<byte> expected) => new() { _expected = expected, _compare = true, _equal = true };
        internal readonly bool Equal => _equal && _position == _expected.Length;
        internal void Bytes(scoped ReadOnlySpan<byte> bytes)
        {
            if (_compare)
            {
                if (_position > _expected.Length - bytes.Length || !bytes.SequenceEqual(_expected.Slice(_position, bytes.Length))) _equal = false;
            }
            else bytes.CopyTo(_output[_position..]);
            _position = checked(_position + bytes.Length);
        }
        internal void Byte(byte value) { Span<byte> bytes = stackalloc byte[1]; bytes[0] = value; Bytes(bytes); }
        internal void Ushort(ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); Bytes(bytes); }
        internal void Uint(uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); Bytes(bytes); }
    }

    private static void ValidateLocalPath(string path)
    {
        var body = path.AsSpan(7);
        while (!body.IsEmpty)
        {
            var separator = body.IndexOf('\\');
            var segment = separator < 0 ? body : body[..separator];
            if (segment.SequenceEqual(".".AsSpan()) || segment.SequenceEqual("..".AsSpan()))
                throw new ArgumentException("正規化済みのWindows絶対パスを指定してください。");
            if (separator < 0) break;
            body = body[(separator + 1)..];
        }
    }

    private static void RejectReparseAncestors(string path, IFolderCopyWindowsMetadataBudget budget, CancellationToken token, Action? context)
    {
        var current = path.TrimEnd('\\');
        while (true)
        {
            if (current.Length < 7) current = path[..7];
            BeforeQuery(budget, 4, token, context);
            var attributes = ReadAttributes(current);
            var error = Marshal.GetLastPInvokeError();
            Guard(token, context);
            if (attributes == uint.MaxValue) throw NativeFailure("GetFileAttributesW(metadata ancestor)", error);
            if ((attributes & 0x400) != 0) throw new IOException("リンク/reparseを経由したmetadata captureは拒否します。");
            if (current.Length == 7) break;
            var length = current.LastIndexOf('\\');
            budget.ReserveQueryWork(checked(2L * length), 0);
            Guard(token, context);
            current = current[..length];
        }
    }

    private static void RequireNtfs(nint handle, IFolderCopyWindowsMetadataBudget budget, CancellationToken token, Action? context)
    {
        BeforeQuery(budget, 64, token, context);
        Span<ushort> name = stackalloc ushort[32];
        int result;
        uint flags;
        fixed (ushort* p = name) result = ReadVolumeInformation(handle, null, 0, out _, out _, out flags, p, (uint)name.Length);
        var error = Marshal.GetLastPInvokeError();
        Guard(token, context);
        if (result == 0) throw NativeFailure("GetVolumeInformationByHandleW", error);
        if (name[0] != 'N' || name[1] != 'T' || name[2] != 'F' || name[3] != 'S' || name[4] != 0
            || (flags & 0x00800008) != 0x00800008)
            throw new NotSupportedException("persistent ACL/EA対応のローカルNTFS以外はmetadata capture未資格です。");
    }

    private static NativeFileInformation QueryInformation(nint handle, IFolderCopyWindowsMetadataBudget budget, CancellationToken token, Action? context)
    {
        BeforeQuery(budget, sizeof(NativeFileInformation), token, context);
        var result = ReadFileInformation(handle, out var information);
        var error = Marshal.GetLastPInvokeError();
        Guard(token, context);
        if (result == 0) throw NativeFailure("GetFileInformationByHandle", error);
        // NTFS directoryの返却sizeにはindex格納量が入る場合がある。コピー本文はfileだけを数え、
        // directoryは比較モデルと同じ論理size 0へ揃える。identity/日時/属性/SD/EAは照合を続ける。
        if ((information.Attributes & (uint)FileAttributes.Directory) != 0)
            information.SizeHigh = information.SizeLow = 0;
        return information;
    }

    private static void ValidateInformation(NativeFileInformation information)
    {
        if ((information.Attributes & 0x400) != 0) throw new IOException("metadata handleの対象がreparseです。");
        if ((information.Attributes & 0x4000) != 0) throw new NotSupportedException("EFS metadataの転送経路は未資格です。");
        if (information.Size > long.MaxValue || information.Index == 0) throw new NotSupportedException("file size/64bit identityを表現できません。");
    }

    private static bool SameInformation(NativeFileInformation left, NativeFileInformation right) =>
        left.Creation.Value == right.Creation.Value && left.Write.Value == right.Write.Value && left.Size == right.Size
        && left.Attributes == right.Attributes && left.Volume == right.Volume && left.Index == right.Index;

    private static void RequirePathIdentity(string nativePath, NativeFileInformation observed,
        IFolderCopyWindowsMetadataBudget budget, CancellationToken token, Action? context)
    {
        // metadata専用handleのshareはrenameを拘束しないため、末尾に同じpathを再openする。
        // 同一objectのhardlink名は許容する。再open後のABA/祖先差替え/hostile raceの完全防止ではない。
        BeforeOwnedOpen(budget, token, context);
        var handle = OpenFile(nativePath, ReadMetadataAccess, 7, 0, 3, 0x00200000 | 0x02000000, 0);
        var error = Marshal.GetLastPInvokeError();
        if (handle == -1)
        {
            Guard(token, context);
            throw NativeFailure("CreateFileW(metadata path recheck)", error);
        }
        Exception? primaryFailure = null;
        try
        {
            Guard(token, context);
            var current = QueryInformation(handle, budget, token, context);
            ValidateInformation(current);
            if (!SameInformation(observed, current))
                throw new IOException("Windows metadataのpathが取得したidentity/basic情報から変わりました。");
        }
        catch (Exception exception) { primaryFailure = exception; throw; }
        finally { CheckedClose(handle, primaryFailure); }
        Guard(token, context);
    }

    private bool Matches(NativeFileInformation value, ulong? writeOverride = null, bool refreshWrite = false) =>
        CreationTime == value.Creation.Value && (refreshWrite || (writeOverride ?? LastWriteTime) == value.Write.Value)
        && Size == checked((long)value.Size) && (uint)ObservedAttributes == value.Attributes && VolumeSerial == value.Volume && FileIndex == value.Index;

    private static void Guard(CancellationToken token, Action? context) { token.ThrowIfCancellationRequested(); context?.Invoke(); token.ThrowIfCancellationRequested(); }
    private static void BeforeQuery(IFolderCopyWindowsMetadataBudget budget, long bytes, CancellationToken token, Action? context)
    { Guard(token, context); budget.ReserveQueryWork(bytes, 1); Guard(token, context); }
    private static void BeforeOwnedOpen(IFolderCopyWindowsMetadataBudget budget, CancellationToken token, Action? context)
    {
        // closeも同じ共有operation予算へ先に含め、open後の予算拒否/取消でもcleanupを必ず行う。
        Guard(token, context); budget.ReserveQueryWork(0, 2); Guard(token, context);
    }
    private static InvalidDataException Malformed(string detail) => new($"Windows metadataが不正です: {detail}。");
    private static IOException NativeFailure(string operation, int error) => new($"{operation}に失敗しました (Win32 error {error})。", new Win32Exception(error));
    private static void CheckedClose(nint handle, Exception? primaryFailure)
    {
        // 成否にかかわらず一度のみ。取消/一次例外をcleanup failureで置換しない。
        if (CloseFile(handle) != 0) return;
        var failure = NativeFailure("CloseHandle(metadata)", Marshal.GetLastPInvokeError());
        if (primaryFailure is null) throw failure;
        AttachCleanupFailure(primaryFailure, failure);
    }

    private static void AttachCleanupFailure(Exception primary, Exception cleanup)
    {
        var previous = primary.Data[CloseFailureDataKey] as Exception;
        primary.Data[CloseFailureDataKey] = previous is null ? cleanup : new AggregateException(previous, cleanup);
    }

    public bool Equals(FolderCopyWindowsMetadata? other) => other is not null
        && CreationTime == other.CreationTime && LastWriteTime == other.LastWriteTime && Size == other.Size
        && ObservedAttributes == other.ObservedAttributes && VolumeSerial == other.VolumeSerial && FileIndex == other.FileIndex
        && _security.AsSpan().SequenceEqual(other._security) && _ea.AsSpan().SequenceEqual(other._ea);
    public override bool Equals(object? obj) => obj is FolderCopyWindowsMetadata other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Digest);
}

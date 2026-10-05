using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiffBeacon.App;

internal sealed unsafe partial class FolderCopyWindowsMetadata
{
    internal const string StagingCleanupFailureDataKey = "FolderCopyWindowsMetadata.StagingCleanupFailure";
    internal static OwnedStaging OpenStaging(string validatedAbsolutePath, FolderCopyWindowsMetadata initial,
        IFolderCopyWindowsMetadataBudget budget, CancellationToken token, Action? validateContext = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(budget);
        if (initial.IsDirectory || initial._rawEa.Length != 0)
            throw new IOException("stagingはfreshなEA未設定の通常fileに限定します。");
        RequireUnchanged(validatedAbsolutePath, initial, budget, token, validateContext);
        Guard(token, validateContext);
        budget.ReserveQueryWork(checked(4L * validatedAbsolutePath.Length + 64), 0);
        budget.ReserveRetainedBytes(256); // lease/stateの保守的object allowance。payloadとは別。
        Guard(token, validateContext);
        var path = FolderCopyWindowsStreams.ExtendedPath(validatedAbsolutePath);
        // DACL/readonly適用後の予算枯渇でも未公開tempを破棄できるよう、owned open前に先払いする。
        // 最大3operation（情報query/readonly解除/disposition）。公開済みでも予約を返却しない。
        Guard(token, validateContext);
        budget.ReserveQueryWork(sizeof(NativeFileInformation) + sizeof(NativeBasicInformation) + 1, 3);
        Guard(token, validateContext);
        // 独立FILE_OBJECT二つのopen/closeを、どちらを取得するよりも前に予約する。
        budget.ReserveQueryWork(0, 4);
        Guard(token, validateContext);
        var handle = OpenFile(path, ReadMetadataAccess | 0x40000 | 0x10000 | 0x10 | 0x100, 7, 0, 3, 0x00200000, 0);
        var error = Marshal.GetLastPInvokeError();
        if (handle == -1) { Guard(token, validateContext); throw NativeFailure("CreateFileW(owned staging)", error); }
        nint finalizer = 0;
        try
        {
            Guard(token, validateContext);
            // rename/EAを行うprimaryのcloseがArchiveを追加するため、後でbasicだけを設定する独立handle。
            finalizer = OpenFile(path, ReadMetadataAccess | 0x100, 7, 0, 3, 0x00200000, 0);
            error = Marshal.GetLastPInvokeError();
            if (finalizer == -1) { finalizer = 0; Guard(token, validateContext); throw NativeFailure("CreateFileW(staging finalizer)", error); }
            Guard(token, validateContext);
            _ = ObserveOwned(handle, initial, budget, token, validateContext);
            var primaryInfo = QueryInformation(handle, budget, token, validateContext);
            var finalizerInfo = QueryInformation(finalizer, budget, token, validateContext);
            if (!SameInformation(primaryInfo, finalizerInfo)) throw new IOException("staging finalizerのobjectが一致しません。");
            RequirePathIdentity(path, primaryInfo, budget, token, validateContext);
            return new(handle, finalizer, initial, budget);
        }
        catch (Exception primary)
        {
            CheckedClose(handle, primary);
            if (finalizer != 0) CheckedClose(finalizer, primary);
            throw;
        }
    }

    private static FolderCopyWindowsMetadata? ObserveOwned(nint handle, FolderCopyWindowsMetadata? expected,
        IFolderCopyWindowsMetadataBudget budget, CancellationToken token, Action? context)
    {
        if (_observing) throw new InvalidOperationException("owned metadata captureの再入はできません。");
        _observing = true;
        try
        {
            Guard(token, context);
            var before = QueryInformation(handle, budget, token, context);
            ValidateInformation(before);
            RequireNtfs(handle, budget, token, context);
            BeforeQuery(budget, MaximumSecurityBytes, token, context);
            return ObserveOpened(handle, before, budget, token, context, expected);
        }
        finally { _observing = false; }
    }

    internal sealed class OwnedStaging
    {
        private nint _handle;
        private nint _finalizerHandle;
        private uint _attributes;
        private readonly FolderCopyWindowsMetadata _initial;
        private readonly IFolderCopyWindowsMetadataBudget _budget;
        private FolderCopyWindowsMetadata? _prepared;
        private FolderCopyWindowsMetadata? _old;
        private bool _applyAttempted, _renameAttempted, _busy;
        internal bool Published { get; private set; }
        internal bool Finalized { get; private set; }
        internal FolderCopyWindowsMetadata PreparedSnapshot => _prepared ?? throw new InvalidOperationException("staging未検証です。");
        internal const string Qualification = "LocalNTFSCurrentOwnerGroup;OwnerGroupDaclOnly;SaclUnobserved;NoCreationHandleBinding;NoHostileRaceGuarantee";

        internal OwnedStaging(nint handle, nint finalizerHandle, FolderCopyWindowsMetadata initial, IFolderCopyWindowsMetadataBudget budget)
        { _handle = handle; _finalizerHandle = finalizerHandle; _initial = initial; _budget = budget; }

        private void Enter()
        {
            if (_handle == 0) throw new ObjectDisposedException(nameof(OwnedStaging));
            if (_busy) throw new InvalidOperationException("staging operationの再入はできません。");
            _busy = true;
        }

        internal void ApplySourceMetadata(FolderCopyWindowsMetadata source, FolderCopyWindowsMetadata? overwriteTarget,
            CancellationToken token, Action? validateContext = null)
        {
            ArgumentNullException.ThrowIfNull(source);
            Enter();
            try
            {
                Guard(token, validateContext);
                if (_applyAttempted || _renameAttempted) throw new InvalidOperationException("metadata適用は一度だけです。");
                if (source.IsDirectory || overwriteTarget?.IsDirectory == true) throw new IOException("file stagingにdirectoryを指定できません。");
                if (overwriteTarget is not null && (overwriteTarget.ObservedAttributes & FileAttributes.ReadOnly) != 0)
                    throw new IOException("既存readonly targetを置換できません。");
                var security = overwriteTarget ?? _initial;
                if (!SameOwnerGroup(_initial, security)) throw new NotSupportedException("owner/group変更のstagingは未資格です。");
                _ = ObserveOwned(_handle, _initial, _budget, token, validateContext);
                if (_initial.Size != source.Size) throw new IOException("staging DATAサイズがsourceと一致しません。");
                _old = overwriteTarget;
                _applyAttempted = true;
                if (source._rawEa.Length != 0)
                {
                    BeforeQuery(_budget, source._rawEa.Length, token, validateContext);
                    NativeIoStatus io = default;
                    int status;
                    fixed (byte* p = source._rawEa) status = SetExtendedAttributes(_handle, ref io, p, (uint)source._rawEa.Length);
                    Guard(token, validateContext);
                    if (status != 0) throw new IOException($"NtSetEaFile(staging)に失敗しました (NTSTATUS {unchecked((uint)status):X8})。");
                }
                if (overwriteTarget is not null) ApplySecurity(security, token, validateContext);
                var attributes = (uint)source.OrdinaryAttributes;
                if ((attributes & 0x80) != 0 && attributes != 0x80) attributes &= ~0x80u;
                if (attributes == 0) attributes = 0x80;
                _attributes = attributes;
                var basic = new NativeBasicInformation
                {
                    Creation = checked((long)(overwriteTarget?.CreationTime ?? _initial.CreationTime)),
                    Write = checked((long)source.LastWriteTime), Attributes = attributes
                };
                BeforeQuery(_budget, sizeof(NativeBasicInformation), token, validateContext);
                int ok = SetFileInformation(_handle, 0, (byte*)&basic, (uint)sizeof(NativeBasicInformation));
                int error = Marshal.GetLastPInvokeError();
                Guard(token, validateContext);
                if (ok == 0) throw NativeFailure("SetFileInformationByHandle(staging basic)", error);
                var actual = ObserveOwned(_handle, null, _budget, token, validateContext)!;
                if (actual.VolumeSerial != _initial.VolumeSerial || actual.FileIndex != _initial.FileIndex
                    || actual.Size != source.Size || actual.CreationTime != (overwriteTarget?.CreationTime ?? _initial.CreationTime)
                    || actual.LastWriteTime != source.LastWriteTime || ((uint)actual.OrdinaryAttributes & ~0x80u) != (attributes & ~0x80u)
                    || !actual._security.AsSpan().SequenceEqual(security._security) || !actual._ea.AsSpan().SequenceEqual(source._ea))
                    throw new IOException("staging metadataの厳密readbackが期待値と一致しません。");
                _prepared = actual;
            }
            finally { _busy = false; }
        }

        private static bool SameOwnerGroup(FolderCopyWindowsMetadata left, FolderCopyWindowsMetadata right)
        {
            var leftSize = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(left._security.AsSpan(4))
                + BinaryPrimitives.ReadUInt32LittleEndian(left._security.AsSpan(8))));
            var rightSize = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(right._security.AsSpan(4))
                + BinaryPrimitives.ReadUInt32LittleEndian(right._security.AsSpan(8))));
            return leftSize == rightSize && left._security.AsSpan(4, 8).SequenceEqual(right._security.AsSpan(4, 8))
                && left._security.AsSpan(20, leftSize).SequenceEqual(right._security.AsSpan(20, rightSize));
        }

        private void ApplySecurity(FolderCopyWindowsMetadata expected, CancellationToken token, Action? context)
        {
            if (_observing) throw new InvalidOperationException("security setter中の再入はできません。");
            _observing = true;
            try { ApplySecurityCore(expected, token, context); }
            finally { _observing = false; }
        }

        private void ApplySecurityCore(FolderCopyWindowsMetadata expected, CancellationToken token, Action? context)
        {
            // 入力SDだけをbounded scratchへ複製。source/old immutable配列は変更しない。
            BeforeQuery(_budget, expected._rawSecurity.Length, token, context);
            Span<byte> sd = stackalloc byte[MaximumSecurityBytes];
            expected._rawSecurity.CopyTo(sd);
            fixed (byte* p = sd)
            {
                ushort bits = expected.SecurityControl;
                if ((bits & 0x400) != 0) bits |= 0x100;
                int ok = SetDescriptorControl(p, 0x1500, (ushort)(bits & 0x1500));
                int error = Marshal.GetLastPInvokeError();
                Guard(token, context);
                if (ok == 0) throw NativeFailure("SetSecurityDescriptorControl(staging input)", error);
                BeforeQuery(_budget, expected._rawSecurity.Length, token, context);
                int status = SetSecurityObject(_handle, 4u | ((bits & 0x1000) != 0 ? 0x80000000u : 0u), p);
                Guard(token, context);
                if (status != 0) throw new IOException($"NtSetSecurityObject(staging DACL)に失敗しました (NTSTATUS {unchecked((uint)status):X8})。");
            }
        }

        internal void Publish(string validatedTargetPath, FolderCopyWindowsMetadata? overwriteTarget,
            Action closeDataAndObservers, Action onPublished, CancellationToken token, Action? validateContext = null)
        {
            ArgumentNullException.ThrowIfNull(closeDataAndObservers);
            ArgumentNullException.ThrowIfNull(onPublished);
            Enter();
            try
            {
                Guard(token, validateContext);
                var expected = PreparedSnapshot;
                if (_renameAttempted || !ReferenceEquals(_old, overwriteTarget)) throw new InvalidOperationException("公開は一度だけ、同じold確認値で行います。");
                if (overwriteTarget is not null && (overwriteTarget.ObservedAttributes & FileAttributes.ReadOnly) != 0) throw new IOException("既存readonly targetを置換できません。");
                _budget.ReserveQueryWork(checked(4L * validatedTargetPath.Length + 64), 0);
                Guard(token, validateContext);
                var target = FolderCopyWindowsStreams.ExtendedPath(validatedTargetPath);
                ValidateLocalPath(target);
                if (target.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("SMB publicationは未資格です。");
                BeforeQuery(_budget, 4, token, validateContext);
                var driveType = ReadDriveType(target[..7]);
                Guard(token, validateContext);
                if (driveType is not (2 or 3)) throw new NotSupportedException("mapped remote等のpublicationは未資格です。");
                // fresh targetにはleafがないため、既存parentまでを検査する。
                RejectReparseAncestors(overwriteTarget is null ? Path.GetDirectoryName(target)! : target, _budget, token, validateContext);
                _ = ObserveOwned(_handle, expected, _budget, token, validateContext);
                closeDataAndObservers();
                Guard(token, validateContext);
                int length = checked(20 + checked(target.Length * sizeof(char)));
                int bufferLength = Math.Max(24, checked(length + sizeof(char)));
                _budget.ReserveQueryWork(bufferLength, 1);
                _budget.ReserveRetainedBytes(checked(bufferLength + 32));
                Guard(token, validateContext);
                byte[] rename = new byte[bufferLength];
                rename[0] = overwriteTarget is null ? (byte)0 : (byte)1;
                BinaryPrimitives.WriteUInt32LittleEndian(rename.AsSpan(16), (uint)(length - 20));
                // Win32へ渡したpathと同じUTF-16 code unitを保持する。encoderのfallbackで別名にしない。
                MemoryMarshal.AsBytes(target.AsSpan()).CopyTo(rename.AsSpan(20)); // 後続2byteはNUL、NameLengthへ含めない。
                _renameAttempted = true;
                int ok;
                fixed (byte* p = rename) ok = SetFileInformation(_handle, 3, p, (uint)rename.Length);
                int error = Marshal.GetLastPInvokeError();
                if (ok != 0) { Published = true; onPublished(); }
                Guard(token, validateContext);
                if (ok == 0) throw NativeFailure("SetFileInformationByHandle(staging rename)", error);
                _ = ObserveOwned(_handle, expected, _budget, token, validateContext);
                // renameのFILE_OBJECTを先にcloseする。Archive付与後の最終属性は独立handleで戻す。
                var primaryHandle = _handle;
                _handle = 0;
                CheckedClose(primaryHandle, null);
                Guard(token, validateContext);
                var afterCleanup = QueryInformation(_finalizerHandle, _budget, token, validateContext);
                if (afterCleanup.Volume != expected.VolumeSerial || afterCleanup.Index != expected.FileIndex
                    || afterCleanup.Creation.Value != expected.CreationTime || afterCleanup.Write.Value != expected.LastWriteTime
                    || afterCleanup.Size != (ulong)expected.Size
                    || (afterCleanup.Attributes & ~0xA0u) != ((uint)expected.ObservedAttributes & ~0xA0u))
                    throw new IOException("primary close後のstaging情報がArchive以外で変わりました。");
                var finalBasic = new NativeBasicInformation { Attributes = _attributes };
                BeforeQuery(_budget, sizeof(NativeBasicInformation), token, validateContext);
                ok = SetFileInformation(_finalizerHandle, 0, (byte*)&finalBasic, (uint)sizeof(NativeBasicInformation));
                error = Marshal.GetLastPInvokeError();
                Guard(token, validateContext);
                if (ok == 0) throw NativeFailure("SetFileInformationByHandle(staging final attributes)", error);
                _ = ObserveOwned(_finalizerHandle, expected, _budget, token, validateContext);
                var finalizerHandle = _finalizerHandle;
                _finalizerHandle = 0;
                CheckedClose(finalizerHandle, null);
                Finalized = true;
            }
            finally { _busy = false; }
        }

        internal void RequirePreparedUnchanged(CancellationToken token, Action? validateContext = null)
        {
            Enter();
            try { _ = ObserveOwned(_handle, PreparedSnapshot, _budget, token, validateContext); }
            finally { _busy = false; }
        }

        internal bool DiscardUnpublished(Exception? primaryFailure = null)
        {
            if (_busy) throw new InvalidOperationException("staging operation中には破棄できません。");
            if (_handle == 0 && _finalizerHandle == 0) return false;
            bool marked = false;
            Exception? cleanupFailure = null;
            var priorCloseFailure = primaryFailure?.Data[CloseFailureDataKey];
            _busy = true;
            try
            {
                if (!Published)
                {
                    // 取消/失効contextを再実行せず、製品が所有する未公開tempの寿命だけを閉じる。
                    // query/readonly解除/dispositionはOpenStagingで先払い済み。ここでは追加予約しない。
                    int ok = ReadFileInformation(_handle, out var information);
                    int error = Marshal.GetLastPInvokeError();
                    if (ok == 0) throw NativeFailure("GetFileInformationByHandle(staging discard)", error);
                    if ((information.Attributes & 1) != 0)
                    {
                        uint attributes = information.Attributes & ~1u;
                        if (attributes == 0) attributes = 0x80;
                        var basic = new NativeBasicInformation { Attributes = attributes };
                        ok = SetFileInformation(_handle, 0, (byte*)&basic, (uint)sizeof(NativeBasicInformation));
                        error = Marshal.GetLastPInvokeError();
                        if (ok == 0) throw NativeFailure("SetFileInformationByHandle(staging discard readonly)", error);
                    }
                    // FILE_DISPOSITION_INFOのBOOLEANは1byte。同じowned DELETE handleだけを使う。
                    byte delete = 1;
                    ok = SetFileInformation(_handle, 4, &delete, 1);
                    error = Marshal.GetLastPInvokeError();
                    if (ok == 0) throw NativeFailure("SetFileInformationByHandle(staging discard disposition)", error);
                    marked = true;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            { cleanupFailure = exception; }
            finally
            {
                _busy = false;
                // open時に予約済みのcloseを必ず一度試す。Publishedの場合はここだけを実行する。
                try { Close(primaryFailure ?? cleanupFailure); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                { cleanupFailure ??= exception; }
            }
            if (cleanupFailure is not null)
            {
                if (primaryFailure is null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                var previous = primaryFailure.Data[StagingCleanupFailureDataKey] as Exception;
                primaryFailure.Data[StagingCleanupFailureDataKey] = previous is null
                    ? cleanupFailure : new AggregateException(previous, cleanupFailure);
                return false;
            }
            return marked && (primaryFailure is null || ReferenceEquals(priorCloseFailure, primaryFailure.Data[CloseFailureDataKey]));
        }

        internal void Close(Exception? primaryFailure = null)
        {
            if (_busy) throw new InvalidOperationException("staging operation中にはcloseできません。");
            var handle = _handle;
            var finalizer = _finalizerHandle;
            _handle = 0;
            _finalizerHandle = 0;
            Exception? cleanup = null;
            try { if (handle != 0) CheckedClose(handle, primaryFailure); }
            catch (Exception failure) { cleanup = failure; }
            if (finalizer != 0) CheckedClose(finalizer, primaryFailure ?? cleanup);
            if (cleanup is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
        }
    }
}

using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using DiffBeacon.Core;

namespace DiffBeacon.App;

/// <summary>通常ファイル一件をOS標準ごみ箱へ移す。完全削除への退避は行わない。</summary>
internal static partial class FolderTrashNative
{
    internal static void MoveFileToTrash(string absolutePath, Action validateImmediatelyBeforeMove,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validateImmediatelyBeforeMove);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("ごみ箱移動には絶対パスが必要です。", nameof(absolutePath));
        var path = Path.GetFullPath(absolutePath);
        cancellationToken.ThrowIfCancellationRequested();
        RequireOrdinaryFile(path);
        if (OperatingSystem.IsWindows())
            MoveWindowsOnSta(path, validateImmediatelyBeforeMove, cancellationToken);
        else if (OperatingSystem.IsMacOS())
            MoveMac(path, validateImmediatelyBeforeMove, cancellationToken);
        else
            throw new PlatformNotSupportedException("このOSの標準ごみ箱移動には対応していません。");
    }

    [SupportedOSPlatform("windows")]
    private static void MoveWindowsOnSta(string path, Action validate, CancellationToken cancellationToken)
    {
        // 呼出し元のUI apartmentと分離し、COMの生成・操作・解放を同じSTAで行う。
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { MoveWindows(path, validate, cancellationToken); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        }) { IsBackground = true, Name = "DiffBeacon folder trash" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // 移動が始まった後は、取消よりもOSの確定結果の回収を優先する。
        thread.Join();
        failure?.Throw();
    }

    private static void RequireOrdinaryFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("フォルダーまたはリンクをごみ箱移動の対象にできません。");
        // 対象だけでなく、祖先のjunction・symlinkによる別namespaceへの誘導も拒否する。
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("リンクを含むパスをごみ箱移動の対象にできません。");
        if (OperatingSystem.IsMacOS())
            FolderCopyMacFileKind.Require(path, DirectoryEntryKind.File);
    }

    private static void RequireSourceAbsent(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException("ごみ箱移動後も元パスに項目が存在します。");
    }

    [SupportedOSPlatform("windows")]
    private static unsafe void MoveWindows(string path, Action validate, CancellationToken cancellationToken)
    {
        ThrowIfFailed(CoInitializeEx(0, 2)); // COINIT_APARTMENTTHREADED
        nint operationPointer = 0, itemPointer = 0;
        void* sinkPointer = null;
        ITrashFileOperation? operation = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classId = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09");
            var operationId = new Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
            ThrowIfFailed(CoCreateInstance(in classId, 0, 1, in operationId, out operationPointer));
            operation = UniqueComInterfaceMarshaller<ITrashFileOperation>.ConvertToManaged((void*)operationPointer)
                ?? throw new IOException("標準ごみ箱のCOM操作を生成できませんでした。");
            var shellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            ThrowIfFailed(SHCreateItemFromParsingName(path, 0, in shellItemId, out itemPointer));
            var sink = new TrashProgressSink(path, validate, cancellationToken);
            sinkPointer = ComInterfaceMarshaller<ITrashProgressSink>.ConvertToUnmanaged(sink);
            // RECYCLEONDELETE / ADDUNDORECORD / NOERRORUI / EARLYFAILURE / SILENT / NOCONFIRMATION。
            ThrowIfFailed(operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00000400 | 0x00100000 | 0x4 | 0x10));
            ThrowIfFailed(operation.DeleteItem(itemPointer, (nint)sinkPointer));
            var result = operation.PerformOperations();
            var abortedResult = operation.GetAnyOperationsAborted(out var aborted);
            sink.Failure?.Throw();
            ThrowIfFailed(result);
            ThrowIfFailed(abortedResult);
            if (aborted != 0 || !sink.PreDeleteConfirmed || !sink.PostDeleteConfirmed)
                throw new IOException("OSがごみ箱移動の完了を確認しませんでした。");
            RequireSourceAbsent(path);
            GC.KeepAlive(sink);
        }
        finally
        {
            // UniqueInstanceのwrapperをSTA内で明示解放する。
            if ((object?)operation is ComObject comObject)
            {
                // UniqueComInterfaceMarshallerへ渡した初期参照はwrapperが所有する。
                operationPointer = 0;
                comObject.FinalRelease();
            }
            if (sinkPointer != null) ComInterfaceMarshaller<ITrashProgressSink>.Free(sinkPointer);
            if (itemPointer != 0) Marshal.Release(itemPointer);
            if (operationPointer != 0) Marshal.Release(operationPointer);
            CoUninitialize();
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    private static unsafe string GetShellItemPath(nint item)
    {
        if (item == 0) throw new IOException("OS通知に対象のShell項目がありません。");
        // IShellItem::GetDisplayName(SIGDN_FILESYSPATH)。借用pointerを通知中だけ使う。
        var vtable = *(nint**)item;
        var getDisplayName = (delegate* unmanaged[Stdcall]<nint, uint, out nint, int>)vtable[5];
        ThrowIfFailed(getDisplayName(item, 0x80058000, out var text));
        try { return Marshal.PtrToStringUni(text) ?? throw new IOException("対象のOSパスを取得できません。"); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    [GeneratedComClass]
    private sealed partial class TrashProgressSink(string path, Action validate, CancellationToken cancellationToken)
        : ITrashProgressSink
    {
        internal ExceptionDispatchInfo? Failure { get; private set; }
        internal bool PreDeleteConfirmed { get; private set; }
        internal bool PostDeleteConfirmed { get; private set; }

        private int Guard(Action action)
        {
            try { action(); return 0; }
            catch (Exception exception)
            {
                Failure ??= ExceptionDispatchInfo.Capture(exception);
                return unchecked((int)0x80004004); // E_ABORT: 例外をCOM境界へ出さず停止する。
            }
        }

        private void RequireMatchingItem(nint item)
        {
            if (!string.Equals(Path.GetFullPath(GetShellItemPath(item)), path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("OS通知の対象パスが移動対象と一致しません。");
        }

        public int PreDeleteItem(uint flags, nint item) => Guard(() =>
        {
            if (PreDeleteConfirmed || (flags & 0x80) == 0) // TSF_DELETE_RECYCLE_IF_POSSIBLE
                throw new IOException("OSが対象のごみ箱移動を許可していません。");
            RequireMatchingItem(item);
            cancellationToken.ThrowIfCancellationRequested();
            RequireOrdinaryFile(path);
            validate();
            cancellationToken.ThrowIfCancellationRequested();
            RequireOrdinaryFile(path);
            PreDeleteConfirmed = true;
        });

        public int PostDeleteItem(uint flags, nint item, int result, nint newItem) => Guard(() =>
        {
            RequireMatchingItem(item);
            ThrowIfFailed(result);
            if (!PreDeleteConfirmed || PostDeleteConfirmed || newItem == 0)
                throw new IOException("ごみ箱内の新しいShell項目を確認できませんでした。");
            RequireSourceAbsent(path);
            PostDeleteConfirmed = true;
        });

        public int StartOperations() => 0;
        public int FinishOperations(int result) => Guard(() => ThrowIfFailed(result));
        public int PreRenameItem(uint flags, nint item, nint name) => unchecked((int)0x80004004);
        public int PostRenameItem(uint flags, nint item, nint name, int result, nint newItem) => 0;
        public int PreMoveItem(uint flags, nint item, nint destination, nint name) => unchecked((int)0x80004004);
        public int PostMoveItem(uint flags, nint item, nint destination, nint name, int result, nint newItem) => 0;
        public int PreCopyItem(uint flags, nint item, nint destination, nint name) => unchecked((int)0x80004004);
        public int PostCopyItem(uint flags, nint item, nint destination, nint name, int result, nint newItem) => 0;
        public int PreNewItem(uint flags, nint destination, nint name) => unchecked((int)0x80004004);
        public int PostNewItem(uint flags, nint destination, nint name, nint template, uint attributes, int result, nint newItem) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    // 未使用slotもSDKの宣言順・ABIを維持する。文字列とinterfaceは借用native pointer。
    [GeneratedComInterface]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    internal partial interface ITrashFileOperation
    {
        [PreserveSig] int Advise(nint sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage(nint message);
        [PreserveSig] int SetProgressDialog(nint dialog);
        [PreserveSig] int SetProperties(nint properties);
        [PreserveSig] int SetOwnerWindow(nint window);
        [PreserveSig] int ApplyPropertiesToItem(nint item);
        [PreserveSig] int ApplyPropertiesToItems(nint items);
        [PreserveSig] int RenameItem(nint item, nint name, nint sink);
        [PreserveSig] int RenameItems(nint items, nint name);
        [PreserveSig] int MoveItem(nint item, nint destination, nint name, nint sink);
        [PreserveSig] int MoveItems(nint items, nint destination);
        [PreserveSig] int CopyItem(nint item, nint destination, nint name, nint sink);
        [PreserveSig] int CopyItems(nint items, nint destination);
        [PreserveSig] int DeleteItem(nint item, nint sink);
        [PreserveSig] int DeleteItems(nint items);
        [PreserveSig] int NewItem(nint destination, uint attributes, nint name, nint template, nint sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted(out int aborted);
    }

    [GeneratedComInterface]
    [Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
    internal partial interface ITrashProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, nint item, nint name);
        [PreserveSig] int PostRenameItem(uint flags, nint item, nint name, int result, nint newItem);
        [PreserveSig] int PreMoveItem(uint flags, nint item, nint destination, nint name);
        [PreserveSig] int PostMoveItem(uint flags, nint item, nint destination, nint name, int result, nint newItem);
        [PreserveSig] int PreCopyItem(uint flags, nint item, nint destination, nint name);
        [PreserveSig] int PostCopyItem(uint flags, nint item, nint destination, nint name, int result, nint newItem);
        [PreserveSig] int PreDeleteItem(uint flags, nint item);
        [PreserveSig] int PostDeleteItem(uint flags, nint item, int result, nint newItem);
        [PreserveSig] int PreNewItem(uint flags, nint destination, nint name);
        [PreserveSig] int PostNewItem(uint flags, nint destination, nint name, nint template, uint attributes, int result, nint newItem);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint context, in Guid interfaceId, out nint item);

    private static void MoveMac(string path, Action validate, CancellationToken cancellationToken)
    {
        // Foundationを読み込んでObjective-Cクラスを登録し、autoreleaseの寿命をこの操作に限定する。
        _ = MacFoundation.Handle;
        var pool = ObjcSend(ObjcSend(ObjcClass("NSAutoreleasePool"), Selector("alloc")), Selector("init"));
        if (pool == 0) throw new IOException("macOSのごみ箱操作を準備できませんでした。");
        try
        {
            var nativePath = ObjcSendUtf8(ObjcClass("NSString"), Selector("stringWithUTF8String:"), path);
            var url = ObjcSendArgument(ObjcClass("NSURL"), Selector("fileURLWithPath:"), nativePath);
            var manager = ObjcSend(ObjcClass("NSFileManager"), Selector("defaultManager"));
            if (nativePath == 0 || url == 0 || manager == 0)
                throw new IOException("macOSの対象URLを生成できませんでした。");
            cancellationToken.ThrowIfCancellationRequested();
            RequireOrdinaryFile(path);
            validate();
            cancellationToken.ThrowIfCancellationRequested();
            RequireOrdinaryFile(path);
            var success = ObjcTrash(manager, Selector("trashItemAtURL:resultingItemURL:error:"), url,
                out var resultUrl, out var error);
            if (success == 0)
            {
                var description = error == 0 ? 0 : ObjcSend(error, Selector("localizedDescription"));
                var bytes = description == 0 ? 0 : ObjcSend(description, Selector("UTF8String"));
                throw new IOException("macOSのごみ箱移動に失敗しました。 " + Marshal.PtrToStringUTF8(bytes));
            }
            if (resultUrl == 0 || ObjcBool(resultUrl, Selector("isFileURL")) == 0)
                throw new IOException("macOSのごみ箱移動先URLを確認できませんでした。");
            var resultingPath = ObjcSend(resultUrl, Selector("path"));
            var resultingBytes = resultingPath == 0 ? 0 : ObjcSend(resultingPath, Selector("UTF8String"));
            var trashedPath = Marshal.PtrToStringUTF8(resultingBytes);
            if (string.IsNullOrWhiteSpace(trashedPath) || !Path.IsPathFullyQualified(trashedPath)
                || Path.GetFullPath(trashedPath).Equals(path, StringComparison.Ordinal))
                throw new IOException("macOSのごみ箱移動先パスを確認できませんでした。");
            var trashedAttributes = File.GetAttributes(trashedPath);
            if ((trashedAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("macOSのごみ箱移動先が通常ファイルではありません。");
            RequireSourceAbsent(path);
        }
        finally { ObjcSendVoid(pool, Selector("drain")); }
    }

    private static class MacFoundation
    {
        internal static readonly nint Handle = NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
    }

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ObjcClass(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint ObjcSend(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void ObjcSendVoid(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial byte ObjcBool(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint ObjcSendArgument(nint receiver, nint selector, nint argument);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ObjcSendUtf8(nint receiver, nint selector, string argument);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial byte ObjcTrash(nint receiver, nint selector, nint url, out nint resultingUrl, out nint error);
}

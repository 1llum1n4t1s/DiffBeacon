namespace DiffBeacon.App;

public enum EditorOrigin { Keyboard, Paste, Cut, SelectedText, Composition }
public readonly record struct EditorSelection(int Caret, int Start, int End);
public sealed record EditorSnapshot(string Text, long Version, EditorSelection Selection);
public sealed record ExactEdit(int Order, int OwnerId, int Start, string Removed, string Inserted);
public sealed record EditorTransaction(long ExpectedVersion, string GroupId, EditorOrigin Origin,
    EditorSelection Before, EditorSelection After, IReadOnlyList<ExactEdit> Edits, string ExactFinalText);

// モデルの原文・owner・予算・readonly・履歴を検証し、成功時だけ一括公開する。
public interface IResultEditorHost
{
    EditorSnapshot Capture();
    bool TryHostResolveOwner(EditorSnapshot original, IReadOnlyList<ExactEdit> precedingEdits,
        int start, int end, out int ownerId, out string reason);
    bool TryCommit(EditorTransaction transaction, out EditorSnapshot adopted, out string reason);
    bool TryUndo(long expectedVersion, out EditorSnapshot adopted, out string reason);
    bool TryRedo(long expectedVersion, out EditorSnapshot adopted, out string reason);
}

public interface IEditorClipboard
{
    Task<string?> ReadTextAsync();
    Task WriteTextAsync(string text);
}

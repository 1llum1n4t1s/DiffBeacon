using Avalonia.Controls;

namespace DiffBeacon.App;

/// <summary>
/// 本家WinMergeと同じく、差分タブの全ペインで一つの「元に戻す／やり直す」履歴を共有する。
///
/// 設計の要点:
/// - 履歴は比較画面に一つだけ持ち、各側の正本（<see cref="TextBox"/>、<see cref="TextEditor(int)"/>）の本物の本文の変更を記録する。
///   エディター（AvaloniaEdit）側の履歴は使わない（<c>UndoStack.SizeLimit = 0</c>）。ゴースト行の並びは比較結果から作る表示の状態で、
///   履歴には入れない。エディターごとの履歴だと、ある側を元に戻した後の再比較で他の側のゴースト行が入れ替わり、その側のやり直しが失われるため。
/// - 記録するのは、エディターへの入力（同じ側への続けての入力は、再比較が走るまで一つの単位にまとめる）と、差分のコピー。
///   それ以外の経路（読込み・再読込み・プロジェクト・表の編集など）で正本が変わったら、履歴の前提が崩れるので履歴を消す。
/// - 一つの記録は、変更前後の本文の共通の先頭・末尾を除いた差し替え（側・位置・消した文字列・入れた文字列）だけを持つ。
///   元に戻す前に、正本のその位置が記録どおりかを確かめ、合わなければ何も変えずに履歴を消す。
/// - 読取り専用の側・他の操作の実行中は、元に戻す／やり直すを断り、本文と履歴をそのまま残す。
/// </summary>
public sealed partial class ComparisonPane
{
    private readonly TextEditHistory _textHistory = new();
    // 履歴の操作・記録済みの経路による正本の書込み中か（この間の正本の変更では履歴を消さない）。
    private int _textHistoryWriting;
    // 続けての入力をまとめている途中の単位（側と、まとめ始めた時点の正本の本文）。
    private (int Side, string Before)? _typingGroup;

    /// <summary>元に戻せる変更があるか（まとめている途中の入力を含む）。</summary>
    internal bool CanUndoText => _typingGroup is { } group && TextEditor(group.Side).Text != group.Before || _textHistory.CanUndo;
    /// <summary>やり直せる変更があるか（新しい入力を始めた時点で、やり直しは消える）。</summary>
    internal bool CanRedoText => _textHistory.CanRedo;
    internal int TextHistoryUndoCount => _textHistory.UndoCount;
    internal int TextHistoryRedoCount => _textHistory.RedoCount;

    /// <summary>正本の変更を監視し、記録していない経路の変更なら履歴を消す。</summary>
    private void AttachTextHistory()
    {
        foreach (var source in new[] { LeftEditor, MiddleEditor, RightEditor })
            source.PropertyChanged += (_, args) =>
            {
                if (args.Property != TextBox.TextProperty || _textHistoryWriting > 0) return;
                if (Equals(args.OldValue, args.NewValue)) return;
                ClearTextHistory();
            };
    }

    private void ClearTextHistory() { _typingGroup = null; _textHistory.Clear(); }

    /// <summary>エディターへの入力を正本へ書き戻す。同じ側への続けての入力は一つの単位にまとめる。</summary>
    private void WriteTypedText(int side, string text)
    {
        var editor = TextEditor(side);
        if (_typingGroup is { } group && group.Side != side) CloseTypingGroup();
        _typingGroup ??= (side, editor.Text ?? "");
        // 新しい入力を始めたら、やり直しは消す（本家と同じ）。
        _textHistory.ClearRedo();
        _textHistoryWriting++;
        try { if (editor.Text != text) editor.Text = text; }
        finally { _textHistoryWriting--; }
    }

    /// <summary>まとめている入力を一つの記録として確定する（再比較・別の側への入力・コピー・元に戻す前）。</summary>
    private void CloseTypingGroup()
    {
        if (_typingGroup is not { } group) return;
        _typingGroup = null;
        _textHistory.Push(TextEdit.Between(group.Side, group.Before, TextEditor(group.Side).Text ?? ""));
    }

    /// <summary>差分のコピーなど、操作による正本の書換えを一つの記録として残して書き込む。</summary>
    private void WriteRecordedText(int side, string text)
    {
        CloseTypingGroup();
        var editor = TextEditor(side);
        var before = editor.Text ?? "";
        _textHistoryWriting++;
        try { editor.Text = text; }
        finally { _textHistoryWriting--; }
        _textHistory.Push(TextEdit.Between(side, before, text));
    }

    internal bool UndoText() => StepTextHistory(undo: true);
    internal bool RedoText() => StepTextHistory(undo: false);

    /// <summary>
    /// 共有の履歴を一つ戻す（進める）。どのペインで操作しても、全ペインを通して最後の変更から順に戻す。
    /// 戻した側の正本を書き換えて比較し直し、変更した位置へその側のキャレットを移す。
    /// </summary>
    private bool StepTextHistory(bool undo)
    {
        if (_disposed) return false;
        CloseTypingGroup();
        var edit = undo ? _textHistory.PeekUndo() : _textHistory.PeekRedo();
        if (edit is null) return false;
        var verb = undo ? "元に戻せません" : "やり直せません";
        if (!CompareButton.IsEnabled) { _status.Text = $"他の操作の実行中は{verb}。"; return false; }
        var editor = TextEditor(edit.Side);
        if (editor.IsReadOnly || !TextSideWritable(edit.Side)) { _status.Text = $"{TextSideName(edit.Side)}は読取り専用のため{verb}。"; return false; }
        var current = editor.Text ?? "";
        if (!edit.Matches(current, applied: undo))
        {
            // 記録と正本が食い違う（想定外の経路の変更）。誤った位置を書き換えないよう、何も変えずに履歴を消す。
            ClearTextHistory();
            _status.Text = $"本文が履歴と一致しないため{verb}。履歴を消去しました。";
            return false;
        }
        var text = edit.Apply(current, undo);
        _diffEditTimer?.Stop();
        _textHistoryWriting++;
        try { editor.Text = text; }
        finally { _textHistoryWriting--; }
        if (undo) _textHistory.MoveUndoToRedo(); else _textHistory.MoveRedoToUndo();
        CompareEditors();
        // 変更した位置（戻した・進めた文字列の末尾）へキャレットを移す。
        var (line, column) = LineAndColumn(text, edit.Start + (undo ? edit.Removed.Length : edit.Inserted.Length));
        FocusDiffEditorRealLine(edit.Side, line, column);
        return true;
    }

    private static string TextSideName(int side) => side switch { 0 => "左", 1 => "中央", _ => "右" };

    /// <summary>本文の文字位置の、本物の行（0始まり）と列（1始まり）。改行は CRLF・LF・CR のどれでも1つと数える。</summary>
    private static (int Line, int Column) LineAndColumn(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var line = 0; var lineStart = 0;
        for (var index = 0; index < offset; index++)
        {
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') { index++; line++; lineStart = index + 1; }
            else if (text[index] is '\r' or '\n') { line++; lineStart = index + 1; }
        }
        // CRLF の間を指す位置は、次の行の先頭として扱う。
        return (line, Math.Max(1, offset - lineStart + 1));
    }

    /// <summary>一つの変更（側・位置・消した文字列・入れた文字列）。</summary>
    internal sealed record TextEdit(int Side, int Start, string Removed, string Inserted)
    {
        internal int Size => Removed.Length + Inserted.Length;

        /// <summary>変更前後の本文から、共通の先頭・末尾を除いた差し替えを作る。変わっていなければ null。</summary>
        internal static TextEdit? Between(int side, string before, string after)
        {
            if (string.Equals(before, after, StringComparison.Ordinal)) return null;
            var prefix = 0; var limit = Math.Min(before.Length, after.Length);
            while (prefix < limit && before[prefix] == after[prefix]) prefix++;
            var suffix = 0;
            while (suffix < limit - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
            return new(side, prefix, before.Substring(prefix, before.Length - prefix - suffix), after.Substring(prefix, after.Length - prefix - suffix));
        }

        /// <summary>本文がこの変更を当てた後（applied）または当てる前の状態か。</summary>
        internal bool Matches(string text, bool applied)
        {
            var expected = applied ? Inserted : Removed;
            return Start + expected.Length <= text.Length && string.CompareOrdinal(text, Start, expected, 0, expected.Length) == 0;
        }

        internal string Apply(string text, bool undo)
        {
            var (remove, insert) = undo ? (Inserted, Removed) : (Removed, Inserted);
            return string.Concat(text.AsSpan(0, Start), insert, text.AsSpan(Start + remove.Length));
        }
    }

    /// <summary>
    /// 共有の履歴。記録の数と文字数に上限を持ち、超えたら古い記録から捨てる（巨大な本文のコピーを繰り返してもメモリを使い切らない）。
    /// </summary>
    internal sealed class TextEditHistory(int maxEntries = 10_000, long maxCharacters = 32L * 1024 * 1024)
    {
        private readonly LinkedList<TextEdit> _undo = new();
        private readonly Stack<TextEdit> _redo = new();
        private long _characters;

        internal bool CanUndo => _undo.Count > 0;
        internal bool CanRedo => _redo.Count > 0;
        internal int UndoCount => _undo.Count;
        internal int RedoCount => _redo.Count;
        internal long Characters => _characters;

        internal void Push(TextEdit? edit)
        {
            if (edit is null) return;
            ClearRedo();
            _undo.AddLast(edit); _characters += edit.Size;
            Trim();
        }

        internal TextEdit? PeekUndo() => _undo.Last?.Value;
        internal TextEdit? PeekRedo() => _redo.Count > 0 ? _redo.Peek() : null;

        internal void MoveUndoToRedo()
        {
            var edit = _undo.Last!.Value; _undo.RemoveLast(); _redo.Push(edit);
        }

        internal void MoveRedoToUndo()
        {
            var edit = _redo.Pop(); _undo.AddLast(edit);
        }

        internal void ClearRedo()
        {
            while (_redo.Count > 0) _characters -= _redo.Pop().Size;
        }

        internal void Clear() { _undo.Clear(); _redo.Clear(); _characters = 0; }

        private void Trim()
        {
            // 上限を超えたら古い記録から捨てる。最後の1件は、それだけで上限を超えても残す（直前の操作は戻せるようにする）。
            while (_undo.Count > 1 && (_undo.Count > maxEntries || _characters > maxCharacters))
            {
                _characters -= _undo.First!.Value.Size; _undo.RemoveFirst();
            }
        }
    }
}

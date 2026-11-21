using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace DiffBeacon.App;

public class TransactionalResultEditor : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);
    private sealed class Candidate(EditorSnapshot before, EditorOrigin origin)
    {
        public EditorSnapshot Before { get; } = before;
        public EditorOrigin Origin { get; } = origin;
        public List<ExactEdit> Edits { get; } = [];
        public string Text { get; set; } = before.Text;
        public string? Failure { get; set; }
    }
    private IResultEditorHost? _host;
    private long _hostAttachmentSerial;
    private Candidate? _candidate;
    private bool _rendering;
    private bool _composing;
    private bool _preeditCleared;
    private int _depth;
    private Key? _editingKey;
    private WrappedInputClient? _client;
    public IEditorClipboard? ClipboardBridge { get; set; }
    public Task LastClipboardOperation { get; private set; } = Task.CompletedTask;
    public string? LastFailure { get; private set; }
    // 検証時だけ接続する。通常の編集で入力本文や操作記録を蓄積しない。
    internal Action<string>? TraceObserved { get; set; }
    internal void WriteTrace(string value) => TraceObserved?.Invoke(value);
    public bool HasPendingComposition => _composing;
    internal event Action? ModelAdopted;

    public TransactionalResultEditor()
    {
        IsUndoEnabled = false;
        AcceptsReturn = true; AcceptsTab = true; MaxLength = 0;
        AddHandler(PastingFromClipboardEvent, (_, e) =>
        { e.Handled = true; LastClipboardOperation = ClipboardEditAsync(false); });
        AddHandler(CuttingToClipboardEvent, (_, e) =>
        { e.Handled = true; LastClipboardOperation = ClipboardEditAsync(true); });
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) =>
        {
            if (e.Client is null || e.Client is WrappedInputClient) return;
            _client?.Dispose();
            _client = new WrappedInputClient(this, e.Client);
            e.Client = _client;
            WriteTrace("client-wrapped-default");
        });
    }

    public void AttachHost(IResultEditorHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_composing) throw new InvalidOperationException("未確定の入力を確定または取り消してから比較を切り替えてください。");
        Finish();
        var snapshot = host.Capture();
        var nextSerial = checked(_hostAttachmentSerial + 1);
        _host = host; _hostAttachmentSerial = nextSerial;
        _candidate = null; _composing = false; Render(snapshot);
    }
    public void CommitPendingInput()
    {
        if (_composing) throw new InvalidOperationException("未確定の入力を確定または取り消してください。");
        Finish();
        if (_host is not null && Text != _host.Capture().Text)
            throw new InvalidOperationException(LastFailure ?? "表示本文と採用済み結果が一致しません。");
    }
    public void NotifyEditContextChanged()
    {
        // 元diffの選択を戻しても、以前のclipboard要求を復活させない。
        if (!_composing) Finish();
        _hostAttachmentSerial = checked(_hostAttachmentSerial + 1);
        if (_candidate is not null) _candidate.Failure = "編集対象の差分が変更されました。";
    }
    public void DetachHost()
    {
        CommitPendingInput();
        _hostAttachmentSerial = checked(_hostAttachmentSerial + 1);
        _host = null; _candidate = null;
        _client?.Dispose(); _client = null;
        Render(new("", 0, new(0, 0, 0)));
    }
    public void RenderFromModel()
    {
        if (_host is null) return;
        // 古いcompositionを保持し、遅れて来るcommitをversion検査で拒否する。
        if (_composing && _candidate is not null)
            _candidate.Failure = "モデル再描画によりcompositionが古くなりました。";
        else _candidate = null;
        Render(_host.Capture());
    }
    private EditorSelection SelectionNow() => new(CaretIndex, SelectionStart, SelectionEnd);
    private void Render(EditorSnapshot snapshot)
    {
        _rendering = true;
        try
        {
            Text = snapshot.Text;
            CaretIndex = snapshot.Selection.Caret;
            SelectionStart = snapshot.Selection.Start; SelectionEnd = snapshot.Selection.End;
        }
        finally { _rendering = false; }
    }
    private Candidate Begin(EditorOrigin origin, string? oldText = null)
    {
        if (_candidate is not null) return _candidate;
        var snapshot = _host?.Capture() ?? new EditorSnapshot(oldText ?? Text ?? "", 0, SelectionNow());
        var captured = snapshot with { Selection = SelectionNow() };
        var c = new Candidate(captured, origin);
        if (snapshot.Text != (oldText ?? Text ?? "")) c.Failure = "表示本文とモデルが一致しません。";
        if (_host is null) c.Failure = "編集hostがありません。";
        if (IsReadOnly) c.Failure = "読取り専用です。";
        _candidate = c;
        WriteTrace($"begin:{origin}:{snapshot.Version}");
        return c;
    }
    private void Finish()
    {
        var c = _candidate; _candidate = null;
        if (c is null || _host is null) return;
        if (IsReadOnly) c.Failure ??= "読取り専用へ変更されました。";
        if (c.Failure is not null) { Reject(c.Failure, c.Before); return; }
        if (c.Edits.Count == 0) return;
        var transaction = new EditorTransaction(c.Before.Version, Guid.NewGuid().ToString("N"),
            c.Origin, c.Before.Selection, SelectionNow(), c.Edits.ToArray(), c.Text);
        try
        {
            if (!_host.TryCommit(transaction, out var adopted, out var reason)) { Reject(reason, c.Before); return; }
            Render(adopted); LastFailure = null; WriteTrace($"commit:{c.Origin}:{adopted.Version}:{c.Edits.Count}");
        }
        catch (Exception e) { Reject(e.Message, c.Before); }
        if (LastFailure is null) ModelAdopted?.Invoke();
    }
    private void Reject(string reason, EditorSnapshot? before = null)
    {
        LastFailure = reason; WriteTrace("reject:" + reason);
        if (before is null && _composing) return;
        if (_host is not null)
        {
            var current = _host.Capture();
            if (before is not null && current.Version == before.Version && current.Text == before.Text)
                current = current with { Selection = before.Selection };
            Render(current);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        // TextChangingもこの通知も代入後。baseのcaret coercionより先に元選択を採取する。
        if (change.Property == TextProperty && !_rendering && _host is not null)
        {
            var oldText = change.OldValue as string ?? "";
            var newText = change.NewValue as string ?? "";
            if (oldText != newText)
            {
                var c = Begin(EditorOrigin.SelectedText, oldText);
                try { CaptureReplacement(c, oldText, newText, SelectionNow()); }
                catch (Exception e) { c.Failure = "owner解決失敗:" + e.Message; }
                if (_depth == 0 && !_composing)
                    Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_candidate, c) && !_composing) Finish(); });
            }
        }
        base.OnPropertyChanged(change);
    }
    private void CaptureReplacement(Candidate c, string oldText, string newText, EditorSelection selection)
    {
        if (c.Failure is not null) return;
        if (oldText != c.Text) { c.Failure = "候補本文の連続性がありません。"; return; }
        int start = Math.Min(selection.Start, selection.End);
        int removedLength = Math.Abs(selection.End - selection.Start);
        if (removedLength == 0)
        {
            start = selection.Caret;
            if (newText.Length < oldText.Length)
            {
                removedLength = oldText.Length - newText.Length;
                if (_editingKey == Key.Back) start -= removedLength;
                else if (_editingKey != Key.Delete) { c.Failure = "選択のない未捕捉削除です。"; return; }
            }
        }
        int insertedLength = newText.Length - oldText.Length + removedLength;
        if (start < 0 || removedLength < 0 || start + removedLength > oldText.Length || insertedLength < 0
            || start + insertedLength > newText.Length
            || !oldText.AsSpan(0, start).SequenceEqual(newText.AsSpan(0, start))
            || !oldText.AsSpan(start + removedLength).SequenceEqual(newText.AsSpan(start + insertedLength)))
        { c.Failure = "捕捉選択/caretから正確な置換を証明できません。"; return; }
        if (!_host!.TryHostResolveOwner(c.Before, c.Edits, start, start + removedLength, out int owner, out var reason))
        { c.Failure = reason; return; }
        c.Edits.Add(new ExactEdit(c.Edits.Count, owner, start,
            oldText.Substring(start, removedLength), newText.Substring(start, insertedLength)));
        c.Text = newText;
    }
    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (e.Handled) return;
        if (!_composing) { Finish(); Begin(EditorOrigin.Keyboard); }
        _depth++;
        try { base.OnTextInput(e); }
        finally
        {
            _depth--; WriteTrace("text-input-commit:" + e.Text);
            _composing = false; _preeditCleared = false;
            Finish();
        }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (command && (e.Key == Key.Z || e.Key == Key.Y))
        { Replay(e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; return; }
        if (_composing && e.Key == Key.Escape)
        { CancelComposition("escape"); e.Handled = true; return; }
        // Windows PhysicalDeleteとmacOS PhysicalNone Backspaceを通常のbase経路で暫定採取。
        bool provisionalDelete = _composing && (e.Key == Key.Delete || e.Key == Key.Back);
        if (_composing && !provisionalDelete)
        {
            // navigation/IME処理はmodel採用せず、candidateが残る。
            _depth++; try { base.OnKeyDown(e); } finally { _depth--; }
            return;
        }
        if (!_composing) { Finish(); Begin(EditorOrigin.Keyboard); }
        _editingKey = e.Key; _depth++;
        try { base.OnKeyDown(e); }
        finally { _depth--; _editingKey = null; if (!_composing) Finish(); }
    }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        // public callbackにはcancel/commit識別子がない。遅延commitを捨てるtimeoutを設けない。
        if (_composing) WriteTrace("focus-lost-pending:" + _preeditCleared);
        else Finish();
        base.OnLostFocus(e);
    }
    public void CancelComposition(string explicitReason)
    {
        var before = _candidate?.Before;
        _candidate = null; _composing = false; _preeditCleared = false;
        _rendering = true;
        try { _client?.ClearDefaultPreedit(); } finally { _rendering = false; }
        if (_host is not null)
        {
            var current = _host.Capture();
            if (before is not null && current.Version == before.Version && current.Text == before.Text)
                current = current with { Selection = before.Selection };
            Render(current);
        }
        WriteTrace("composition-cancel:" + explicitReason);
    }
    internal void Preedit(string? text, Action forward)
    {
        if (!_composing) { Finish(); Begin(EditorOrigin.Composition); _composing = true; }
        _preeditCleared = string.IsNullOrEmpty(text);
        WriteTrace("preedit:" + (text ?? "<null>"));
        _depth++; try { forward(); } finally { _depth--; }
    }
    internal void InputClientSelection(Action forward)
    {
        if (!_composing) { Finish(); Begin(EditorOrigin.Composition); _composing = true; }
        WriteTrace("client-selection");
        _depth++; try { forward(); } finally { _depth--; }
    }
    private void Replay(bool redo)
    {
        if (_host is null) return;
        if (_composing) { Reject("composition中の履歴操作を拒否しました。"); return; }
        Finish(); var snapshot = _host.Capture();
        bool ok = redo ? _host.TryRedo(snapshot.Version, out var after, out var reason)
            : _host.TryUndo(snapshot.Version, out after, out reason);
        if (ok) { Render(after); LastFailure = null; ModelAdopted?.Invoke(); } else Reject(reason);
    }
    public void UndoModel() => Replay(false);
    public void RedoModel() => Replay(true);
    public void ReplaceSelection(string text)
    {
        if (_composing) { Reject("composition中の外部置換を拒否しました。"); return; }
        Finish(); Begin(EditorOrigin.SelectedText); _depth++;
        try { base.SelectedText = text; } finally { _depth--; Finish(); }
    }
    private async Task ClipboardEditAsync(bool cut)
    {
        if (_host is not { } targetHost) { Reject("編集hostがありません。"); return; }
        if (_composing || IsReadOnly) { Reject("composition中または読取り専用です。"); return; }
        Finish();
        if (!ReferenceEquals(targetHost, _host)) { LastFailure = "編集hostが変更されました。"; return; }
        var attachmentSerial = _hostAttachmentSerial;
        var original = targetHost.Capture() with { Selection = SelectionNow() };
        int start = Math.Min(original.Selection.Start, original.Selection.End);
        int end = Math.Max(original.Selection.Start, original.Selection.End);
        if (cut && start == end) return;
        try
        {
            var bridge = ClipboardBridge;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (bridge is null && clipboard is null) { Reject("clipboardがありません。"); return; }
            string? inserted = "";
            if (cut)
            {
                var selected = original.Text[start..end];
                if (bridge is not null) await bridge.WriteTextAsync(selected); else await clipboard!.SetTextAsync(selected);
            }
            else inserted = bridge is not null ? await bridge.ReadTextAsync() : await clipboard!.TryGetTextAsync();
            // 値が同じ新比較や同一hostの再接続へ、古い非同期操作を渡さない。
            if (!ReferenceEquals(targetHost, _host) || attachmentSerial != _hostAttachmentSerial)
            { LastFailure = "clipboard完了の編集hostが古くなりました。"; WriteTrace("clipboard-host-stale"); return; }
            var current = targetHost.Capture();
            if (current.Version != original.Version || current.Text != original.Text || Text != original.Text
                || SelectionNow() != original.Selection || IsReadOnly || _composing)
            { LastFailure = "clipboard完了が古くなりました。"; WriteTrace("clipboard-stale"); return; }
            if (string.IsNullOrEmpty(inserted) && !cut) return;
            if (!targetHost.TryHostResolveOwner(original, [], start, end, out int owner, out var reason))
            { Reject(reason, original); return; }
            var edit = new ExactEdit(0, owner, start, original.Text[start..end], inserted ?? "");
            var final = original.Text[..start] + edit.Inserted + original.Text[end..];
            var afterSelection = new EditorSelection(start + edit.Inserted.Length, start + edit.Inserted.Length, start + edit.Inserted.Length);
            var transaction = new EditorTransaction(original.Version, Guid.NewGuid().ToString("N"),
                cut ? EditorOrigin.Cut : EditorOrigin.Paste, original.Selection, afterSelection, [edit], final);
            if (targetHost.TryCommit(transaction, out var adopted, out reason)) { Render(adopted); LastFailure = null; ModelAdopted?.Invoke(); }
            else Reject(reason, original);
        }
        catch (Exception e) { LastFailure = "clipboard失敗:" + e.Message; WriteTrace(LastFailure); }
    }
}

using Avalonia;
using Avalonia.Controls.Presenters;
using Avalonia.Input.TextInput;

namespace DiffBeacon.App;

public sealed class WrappedInputClient : TextInputMethodClient, IDisposable
{
    private readonly TransactionalResultEditor _editor;
    private readonly TextInputMethodClient _inner;
    public WrappedInputClient(TransactionalResultEditor editor, TextInputMethodClient inner)
    {
        _editor = editor; _inner = inner;
        inner.TextViewVisualChanged += VisualChanged;
        inner.CursorRectangleChanged += CursorChanged;
        inner.SurroundingTextChanged += SurroundingChanged;
        inner.SelectionChanged += SelectionChangedInner;
        inner.ResetRequested += Reset;
        inner.InputPaneActivationRequested += Activation;
    }
    public override Visual TextViewVisual => _inner.TextViewVisual;
    public override bool SupportsPreedit => _inner.SupportsPreedit;
    public override bool SupportsSurroundingText => _inner.SupportsSurroundingText;
    public override string SurroundingText => _inner.SurroundingText;
    public override Rect CursorRectangle => _inner.CursorRectangle;
    public override TextSelection Selection
    { get => _inner.Selection; set => _editor.InputClientSelection(() => _inner.Selection = value); }
    public override void SetPreeditText(string? text) => SetPreeditText(text, null);
    public override void SetPreeditText(string? text, int? cursorPos)
        => _editor.Preedit(text, () =>
        {
            _inner.SetPreeditText(text, cursorPos);
            // 既定selection削除のText/caret変更がPresenterのpreeditをclearする。
            // 同じclientへ暫定文字列を再提示し、既定描画と下線を維持する。
            if (!string.IsNullOrEmpty(text) && _inner.TextViewVisual is TextPresenter presenter
                && presenter.PreeditText != text)
                _inner.SetPreeditText(text, cursorPos);
        });
    public override void ExecuteContextMenuAction(ContextMenuAction action)
    { _editor.WriteTrace("client-context-menu:" + action); _inner.ExecuteContextMenuAction(action); }
    internal void ClearDefaultPreedit() => _inner.SetPreeditText(null, null);
    private void VisualChanged(object? s, EventArgs e) => RaiseTextViewVisualChanged();
    private void CursorChanged(object? s, EventArgs e) => RaiseCursorRectangleChanged();
    private void SurroundingChanged(object? s, EventArgs e) => RaiseSurroundingTextChanged();
    private void SelectionChangedInner(object? s, EventArgs e) => RaiseSelectionChanged();
    private void Reset(object? s, EventArgs e) => RequestReset();
    private void Activation(object? s, EventArgs e) => RaiseInputPaneActivationRequested();
    public void Dispose()
    {
        _inner.TextViewVisualChanged -= VisualChanged; _inner.CursorRectangleChanged -= CursorChanged;
        _inner.SurroundingTextChanged -= SurroundingChanged; _inner.SelectionChanged -= SelectionChangedInner;
        _inner.ResetRequested -= Reset; _inner.InputPaneActivationRequested -= Activation;
    }
}

namespace DiffBeacon.App
{
using DiffBeacon.Core;
public sealed partial class SessionEditorHost
{
    public ChoiceCatalog? Choices { get; set; }
    public string LastChoiceOutcome { get; private set; } = "";
    public ResultLineBuffer CaptureExport()
    { lock (_gate) { Cancellation.ThrowIfCancellationRequested(); return Session.Current; } }
    public bool TryCaptureSave(long expectedVersion, out ResultEditSession.SaveCapture? capture, out string reason)
    {
        lock (_gate)
        {
            capture = null; reason = "";
            try { Writable(); capture = Session.CaptureSave(expectedVersion); return true; }
            catch (Exception e) { reason = e.GetType().Name + ": " + e.Message; return false; }
        }
    }
    public bool TryCompleteSave(ResultEditSession.SaveCapture capture, bool publishedSuccessfully, out string reason)
    {
        lock (_gate)
        {
            reason = "";
            try { Writable(); if (Session.CompleteSave(capture, publishedSuccessfully)) return true; reason = "保存失敗または後発versionです。"; return false; }
            catch (Exception e) { reason = e.GetType().Name + ": " + e.Message; return false; }
        }
    }
    public bool TryChoose(ChoiceRequest request, out EditorSnapshot adopted, out string reason)
    {
        lock (_gate)
        {
            ResultEditSession.Candidate? c = null; adopted = Snapshot(Session.Current); reason = "";
            try
            {
                Writable(); var catalog = Choices ?? throw new InvalidOperationException("source catalog未接続です。");
                c = Session.PrepareChoice(catalog, request, Cancellation, out bool replaced);
                var p = new Position(0, 0); var prepared = new EditorSnapshot(c.Buffer.Text, checked(Session.Current.Version + 1), new(0, 0, 0));
                BeforePublish?.Invoke(); Writable();
                if (!ReferenceEquals(catalog, Choices)) throw new InvalidOperationException("source catalogの世代が古くなりました。");
                c.End(c.Buffer.Text, p, new(p, p));
                LastChoiceOutcome = replaced ? "Chosen" : "InvalidIntervalMarkedEdited"; adopted = prepared; return true;
            }
            catch (Exception e) { adopted = Snapshot(Session.Current); reason = e.GetType().Name + ": " + e.Message; return false; }
            finally { c?.Cancel(); }
        }
    }
    public bool TryToggle(long expectedVersion, int actualDiffIndex, int pane, out EditorSnapshot adopted, out string reason, bool mergeWithPrevious = false)
    {
        lock (_gate)
        {
            try { return TryChoose(Session.ToggleRequest(Choices ?? throw new InvalidOperationException("source catalog未接続です。"), expectedVersion, actualDiffIndex, pane, mergeWithPrevious), out adopted, out reason); }
            catch (Exception e) { adopted = Snapshot(Session.Current); reason = e.GetType().Name + ": " + e.Message; return false; }
        }
    }
}
}

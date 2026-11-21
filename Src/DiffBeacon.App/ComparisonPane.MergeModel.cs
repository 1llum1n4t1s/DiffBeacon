using DiffBeacon.Core;

namespace DiffBeacon.App;

// GUI表示専用の互換読取り面。編集と履歴はSessionEditorHostだけが所有する。
public sealed class MergeResultModel(SessionEditorHost host, int inputCount, bool hasAncestor)
{
    public int InputCount => inputCount;
    public bool HasAncestor => hasAncestor;
    public bool IsModified => host.Session.IsModified;
    public string Text => host.Session.Current.Text;
    public bool CanUndo => host.Session.UndoCount > 0;
    public bool CanRedo => host.Session.RedoCount > 0;
    public int UnresolvedCount => host.Session.Current.Segments.Count(segment => segment.IsPlaceholder);
    public int ConflictCount => host.Session.Current.Segments.Count(segment => segment.IsPlaceholder && segment.State == SegmentState.Conflict);
    public IReadOnlyList<MergeLineProvenance> LineProvenance
    {
        get
        {
            var buffer = host.Session.Current;
            return buffer.Lines.Select((line, index) =>
            {
                var marker = buffer.Marker(index);
                if (marker.Length == 0 && line.Origins.FirstOrDefault() is { } origin) marker = (origin.SourcePane + 1).ToString();
                return new MergeLineProvenance(index + 1, marker);
            }).ToArray();
        }
    }
    public IReadOnlyList<MergeResultRange> Sections => host.Session.Current.Segments.Where(segment => segment.OriginalDiffId >= 0)
        .Select(segment => new MergeResultRange(new(segment.OriginalDiffId, segment.State, segment.IsPlaceholder), segment.TextStart, segment.TextLength)).ToArray();
}
public sealed record MergeResultSection(int Id, SegmentState State, bool IsPending);
public sealed record MergeResultRange(MergeResultSection Section, int Start, int Length);

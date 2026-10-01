namespace DiffBeacon.App;

// WinIMerge v1.0.54 ImgMergeBuffer.hpp の領域コピー・共有履歴を移植。
// GPL-2.0-or-later。原本・採取境界は tests/Fixtures/ImageCopy を参照。
// 静止画専用。拡張の初期値は明示したzero-filled BGRA契約。
internal sealed class ImageEditSession
{
    internal const int MaximumHistoryRecords = 128;
    internal const long MaximumHistoryBytes = 256L * 1024 * 1024;
    internal const long MaximumWork = 256_000_000;
    private sealed record Edit(int Pane, ImageComparisonEngine.DecodedFrame Before,
        ImageComparisonEngine.DecodedFrame After, int[] Counts);
    private readonly bool[] _readOnly;
    private int _blockSize;
    private double _threshold;
    private readonly List<Edit> _history = [];
    private readonly int[] _counts;
    private readonly int[] _savePoints;
    private ImageComparisonEngine.DecodedFrame[] _frames;
    private ImageComparisonEngine.DecodedFrame[] _viewFrames;
    private ImageOrientation[] _orientations;
    private ImageOffset[] _offsets;
    private int _index = -1;
    private long _historyBytes;
    private long _work;
    internal ImageRegionDiffer.Result Regions { get; private set; }
    internal int PaneCount => _frames.Length;
    internal double Threshold => _threshold;
    internal int BlockSize => _blockSize;
    internal int HistoryIndex => _index;
    internal int HistoryCount => _history.Count;
    internal bool CanUndo => _index >= 0;
    internal bool CanRedo => _index < _history.Count - 1;

    internal ImageEditSession(IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames,
        IReadOnlyList<bool>? readOnly = null, int blockSize = 8, double threshold = 0,
        CancellationToken token = default)
    {
        if (readOnly is not null && readOnly.Count != frames.Count) throw new ArgumentException("読取り専用の指定数が入力数と一致しません。");
        Regions = ImageRegionDiffer.Compare(frames, blockSize, threshold, token);
        _frames = frames.Select(Clone).ToArray();
        _viewFrames = (ImageComparisonEngine.DecodedFrame[])_frames.Clone();
        _orientations = frames.Select(_ => new ImageOrientation()).ToArray();
        _offsets = new ImageOffset[frames.Count];
        _readOnly = readOnly?.ToArray() ?? new bool[frames.Count];
        _counts = new int[frames.Count]; _savePoints = new int[frames.Count];
        _blockSize = blockSize; _threshold = threshold;
        _work = ComparisonWork(_frames);
    }

    private ImageEditSession(ImageEditSession source)
    {
        // raw画素/Edit/Regionsは書き換えず候補へ置換する。変更可能な容器だけ分離する。
        _frames = (ImageComparisonEngine.DecodedFrame[])source._frames.Clone();
        _viewFrames = (ImageComparisonEngine.DecodedFrame[])source._viewFrames.Clone();
        _orientations = source._orientations.Select(value => value with { }).ToArray();
        _offsets = (ImageOffset[])source._offsets.Clone();
        _history = new List<Edit>(source._history);
        _readOnly = (bool[])source._readOnly.Clone();
        _counts = (int[])source._counts.Clone();
        _savePoints = (int[])source._savePoints.Clone();
        _blockSize = source._blockSize; _threshold = source._threshold;
        _index = source._index; _historyBytes = source._historyBytes; _work = source._work;
        Regions = source.Regions;
    }

    internal ImageEditSession Fork() => new(this);

    internal void SetBlockSize(int blockSize, CancellationToken token = default)
    {
        if (blockSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(blockSize));
        token.ThrowIfCancellationRequested();
        if (_blockSize == blockSize) return;
        var work = ComparisonWork(_viewFrames); CheckWork(work);
        var compared = ImageRegionDiffer.Compare(_viewFrames, blockSize, _threshold, token, _offsets);
        token.ThrowIfCancellationRequested();
        _blockSize = blockSize; Regions = compared; _work += work;
    }

    internal void SetThreshold(double threshold, CancellationToken token = default)
    {
        if (!double.IsFinite(threshold) || threshold < 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        token.ThrowIfCancellationRequested();
        var work = ComparisonWork(_viewFrames); CheckWork(work);
        var compared = ImageRegionDiffer.Compare(_viewFrames, _blockSize, threshold, token, _offsets);
        token.ThrowIfCancellationRequested();
        _threshold = threshold; Regions = compared; _work += work;
    }

    internal void SetReadOnly(int pane, bool readOnly)
    {
        if (!ValidPane(pane)) throw new ArgumentOutOfRangeException(nameof(pane));
        _readOnly[pane] = readOnly;
    }

    internal IReadOnlyList<ImageComparisonEngine.DecodedFrame> CaptureFrames() => _frames.Select(Clone).ToArray();
    internal IReadOnlyList<ImageComparisonEngine.DecodedFrame> CaptureViewFrames() => _viewFrames.Select(Clone).ToArray();
    internal IReadOnlyList<ImageOrientation> CaptureOrientations() => _orientations.Select(value => value with { }).ToArray();
    internal IReadOnlyList<ImageOffset> CaptureOffsets() => (ImageOffset[])_offsets.Clone();
    internal void SetOffsets(IReadOnlyList<ImageOffset> offsets, CancellationToken token = default)
        => SetViewTransforms(_orientations, offsets, token);
    internal void SetViewTransforms(IReadOnlyList<ImageOrientation> orientations, IReadOnlyList<ImageOffset> offsets, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ImageComparisonEngine.ValidateOrientations(orientations, PaneCount);
        var requestedOrientations = orientations.Select(value => value with { }).ToArray();
        var requested = ImageOffset.Validate(offsets, PaneCount);
        if (_offsets.SequenceEqual(requested) && _orientations.SequenceEqual(requestedOrientations)) return;
        var dimensions = _frames.Select((frame, index) => requestedOrientations[index].SwapsDimensions
            ? (Width: frame.Height, Height: frame.Width) : (frame.Width, frame.Height)).ToArray();
        var width = dimensions.Select((size, index) => size.Width + requested[index].X).Max();
        var height = dimensions.Select((size, index) => size.Height + requested[index].Y).Max();
        if ((long)width * height > ImageComparisonEngine.MaximumPixels) throw new InvalidOperationException("比較キャンバスが1600万ピクセルを超えます。");
        var work = checked((long)width * height * PaneCount);
        for (var pane = 0; pane < PaneCount; pane++) if (_orientations[pane] != requestedOrientations[pane])
            work = checked(work + (long)_frames[pane].Width * _frames[pane].Height);
        CheckWork(work);
        var views = (ImageComparisonEngine.DecodedFrame[])_viewFrames.Clone();
        for (var pane = 0; pane < PaneCount; pane++) if (_orientations[pane] != requestedOrientations[pane])
            views[pane] = requestedOrientations[pane].Apply(_frames[pane], token);
        var compared = ImageRegionDiffer.Compare(views, _blockSize, _threshold, token, requested);
        token.ThrowIfCancellationRequested();
        _offsets = requested; _orientations = requestedOrientations; _viewFrames = views; Regions = compared; _work += work;
    }
    internal void AddOffset(int pane, int dx, int dy, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var offsets = ImageOffset.Move(_offsets, pane, dx, dy);
        var work = ComparisonWork(_viewFrames, offsets); CheckWork(work);
        var compared = ImageRegionDiffer.Compare(_viewFrames, _blockSize, _threshold, token, offsets);
        token.ThrowIfCancellationRequested();
        _offsets = offsets; Regions = compared; _work += work;
    }
    internal void SetOrientation(int pane, ImageOrientation orientation, CancellationToken token = default)
    {
        if (!ValidPane(pane)) throw new ArgumentOutOfRangeException(nameof(pane));
        ImageOrientation.Validate(orientation); token.ThrowIfCancellationRequested();
        var orientations = _orientations.ToArray(); orientations[pane] = orientation;
        SetViewTransforms(orientations, _offsets, token);
    }
    internal ImageComparisonEngine.DecodedFrame CaptureFrame(int pane)
        => ValidPane(pane) ? Clone(_frames[pane]) : throw new ArgumentOutOfRangeException(nameof(pane));
    internal bool IsReadOnly(int pane) => ValidPane(pane) && _readOnly[pane];
    internal bool IsModified(int pane) => ValidPane(pane) && _savePoints[pane] != (_index < 0 ? 0 : _history[_index].Counts[pane]);
    internal int ModCount(int pane) => _counts[pane];
    internal int SavePoint(int pane) => ValidPane(pane) ? _savePoints[pane] : 0;
    internal void MarkSaved(int pane) { if (ValidPane(pane)) _savePoints[pane] = _index < 0 ? 0 : _history[_index].Counts[pane]; }
    internal void SetSavePoint(int pane, int point) { if (ValidPane(pane)) _savePoints[pane] = point; }
    private bool ValidPane(int pane) => pane >= 0 && pane < _frames.Length;
    private bool CanCopy(int source, int destination) => ValidPane(source) && ValidPane(destination)
        && source != destination && !_readOnly[destination];

    internal void Copy(int regionIndex, int source, int destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!CanCopy(source, destination) || regionIndex < 0 || regionIndex >= Regions.Regions.Count) return;
        Apply(destination, [(regionIndex, source)], token);
    }

    internal void CopyAll(int source, int destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!CanCopy(source, destination)) return;
        Apply(destination, Enumerable.Range(0, Regions.Regions.Count).Select(index => (index, source)).ToArray(), token);
    }

    internal int AutoMerge(int destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!ValidPane(destination) || _readOnly[destination]) return 0;
        var copies = new List<(int Index, int Source)>();
        for (var index = 0; index < Regions.Regions.Count; index++)
        {
            var source = Regions.Regions[index].Op switch
            {
                1 when destination == 1 => 0,
                2 when destination != 1 => 1,
                3 when destination == 1 && _frames.Length == 3 => 2,
                _ => -1
            };
            if (source >= 0) copies.Add((index, source));
        }
        Apply(destination, copies, token);
        return copies.Count;
    }

    private void Apply(int destination, IReadOnlyList<(int Index, int Source)> copies, CancellationToken token)
    {
        // 全領域を同じ比較時寸法で処理し、一件の履歴と再比較を確定する。
        var rawBefore = _frames[destination];
        var before = _viewFrames[destination];
        var width = before.Width; var height = before.Height;
        var offsets = (ImageOffset[])_offsets.Clone();
        long scanWork = 0;
        foreach (var (index, source) in copies)
        {
            token.ThrowIfCancellationRequested();
            var rc = Regions.Regions[index];
            var expansion = Expansion(rc, source, destination, before, offsets);
            width = checked(width + expansion.Dx); height = checked(height + expansion.Dy);
            offsets[destination] = new(offsets[destination].X - expansion.Ox, offsets[destination].Y - expansion.Oy);
            scanWork = checked(scanWork + (long)(rc.Right - rc.Left) * (rc.Bottom - rc.Top) * _blockSize * _blockSize);
        }
        var canvasWidth = width + offsets[destination].X;
        var canvasHeight = height + offsets[destination].Y;
        for (var pane = 0; pane < PaneCount; pane++) if (pane != destination)
        { canvasWidth = Math.Max(canvasWidth, _viewFrames[pane].Width + offsets[pane].X);
          canvasHeight = Math.Max(canvasHeight, _viewFrames[pane].Height + offsets[pane].Y); }
        if ((long)canvasWidth * canvasHeight > ImageComparisonEngine.MaximumPixels)
            throw new InvalidOperationException("編集キャンバスが1600万ピクセルを超えます。");
        var bytes = checked((long)width * height * 4);
        var retainedBytes = _historyBytes;
        for (var i = _history.Count - 1; i > _index; i--) retainedBytes -= RecordBytes(_history[i]);
        if (_index + 2 > MaximumHistoryRecords || retainedBytes + before.Pixels.LongLength + bytes > MaximumHistoryBytes)
            throw new InvalidOperationException("画像編集の履歴上限（128件・256 MiB）を超えます。");
        var comparisonWork = checked((long)canvasWidth * canvasHeight * _frames.Length);
        var transformationWork = _orientations[destination].IsIdentity ? 0 : checked((long)width * height);
        CheckWork(checked(scanWork + comparisonWork + transformationWork));
        var next = Clone(before);
        var copyOffsets = (ImageOffset[])_offsets.Clone();
        foreach (var (index, source) in copies) next = CopyRegion(index, source, destination, before, next, copyOffsets, token);
        var candidate = (ImageComparisonEngine.DecodedFrame[])_viewFrames.Clone(); candidate[destination] = next;
        var compared = ImageRegionDiffer.Compare(candidate, _blockSize, _threshold, token, copyOffsets);
        var counters = (int[])_counts.Clone();
        for (var i = _history.Count - 1; i > _index; i--) --counters[_history[i].Pane];
        ++counters[destination];
        var rawNext = _orientations[destination].Apply(next, token, inverse: true);
        var rawCandidate = (ImageComparisonEngine.DecodedFrame[])_frames.Clone(); rawCandidate[destination] = rawNext;
        var edit = new Edit(destination, rawBefore, rawNext, counters);
        token.ThrowIfCancellationRequested();
        if (_history.Count > _index + 1) _history.RemoveRange(_index + 1, _history.Count - _index - 1);
        _history.Add(edit); ++_index;
        Array.Copy(counters, _counts, counters.Length);
        _historyBytes = retainedBytes + RecordBytes(edit); _work += scanWork + comparisonWork + transformationWork;
        _frames = rawCandidate; _viewFrames = candidate; _offsets = copyOffsets; Regions = compared;
    }

    private (int Dx, int Dy, int Ox, int Oy) Expansion(ImageRegionDiffer.Region rc, int source, int destination,
        ImageComparisonEngine.DecodedFrame before, IReadOnlyList<ImageOffset> offsets)
    {
        var input = _viewFrames[source]; var src = offsets[source]; var dst = offsets[destination];
        var xmin = Math.Max(rc.Left * _blockSize, src.X); var ymin = Math.Max(rc.Top * _blockSize, src.Y);
        var xmax = Math.Min(rc.Right * _blockSize - 1, input.Width + src.X - 1);
        var ymax = Math.Min(rc.Bottom * _blockSize - 1, input.Height + src.Y - 1);
        var ox = Math.Max(0, dst.X - xmin); var oy = Math.Max(0, dst.Y - ymin);
        return (checked(ox + Math.Max(0, xmax - (before.Width + dst.X - 1))),
            checked(oy + Math.Max(0, ymax - (before.Height + dst.Y - 1))), ox, oy);
    }

    private ImageComparisonEngine.DecodedFrame CopyRegion(int index, int source, int destination,
        ImageComparisonEngine.DecodedFrame originalDestination, ImageComparisonEngine.DecodedFrame target,
        ImageOffset[] offsets, CancellationToken token)
    {
        var rc = Regions.Regions[index]; var input = _viewFrames[source];
        var expansion = Expansion(rc, source, destination, originalDestination, offsets);
        var width = target.Width + expansion.Dx;
        var height = target.Height + expansion.Dy;
        if (width != target.Width || height != target.Height)
        {
            var expanded = new byte[checked(width * height * 4)];
            for (var y = 0; y < target.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                target.Pixels.AsSpan(y * target.Width * 4, target.Width * 4)
                    .CopyTo(expanded.AsSpan(((y + expansion.Oy) * width + expansion.Ox) * 4));
            }
            target = new(target.Number, width, height, expanded);
            offsets[destination] = new(offsets[destination].X - expansion.Ox, offsets[destination].Y - expansion.Oy);
        }
        for (var by = rc.Top; by < rc.Bottom; by++)
        {
            token.ThrowIfCancellationRequested();
            for (var bx = rc.Left; bx < rc.Right; bx++)
            {
                if (Regions.RegionIds[by * Regions.Columns + bx] != index + 1) continue;
                for (var dy = 0; dy < _blockSize; dy++)
                {
                    var y = by * _blockSize + dy;
                    var sy = y - offsets[source].Y; var ty = y - offsets[destination].Y;
                    if (sy < 0 || sy >= input.Height || ty < 0 || ty >= originalDestination.Height) continue;
                    var x = Math.Max(bx * _blockSize, Math.Max(offsets[source].X, offsets[destination].X));
                    var end = Math.Min((bx + 1) * _blockSize,
                        Math.Min(input.Width + offsets[source].X, originalDestination.Width + offsets[destination].X));
                    var count = end - x;
                    // ConvertToRealPosは拡張前の前処理寸法を参照する。
                    if (count > 0) input.Pixels.AsSpan((sy * input.Width + x - offsets[source].X) * 4, count * 4)
                        .CopyTo(target.Pixels.AsSpan((ty * target.Width + x - offsets[destination].X) * 4, count * 4));
                }
            }
        }
        return target;
    }

    internal bool Undo(CancellationToken token = default) => MoveHistory(false, token);
    internal bool Redo(CancellationToken token = default) => MoveHistory(true, token);
    private bool MoveHistory(bool redo, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (redo ? !CanRedo : !CanUndo) return false;
        var edit = _history[redo ? _index + 1 : _index];
        var candidate = (ImageComparisonEngine.DecodedFrame[])_frames.Clone();
        candidate[edit.Pane] = redo ? edit.After : edit.Before;
        var views = (ImageComparisonEngine.DecodedFrame[])_viewFrames.Clone();
        var nextRaw = candidate[edit.Pane];
        var nextWidth = _orientations[edit.Pane].SwapsDimensions ? nextRaw.Height : nextRaw.Width;
        var nextHeight = _orientations[edit.Pane].SwapsDimensions ? nextRaw.Width : nextRaw.Height;
        var work = checked((long)Math.Max(nextWidth + _offsets[edit.Pane].X,
                views.Select((frame, index) => index == edit.Pane ? 0 : frame.Width + _offsets[index].X).Max())
            * Math.Max(nextHeight + _offsets[edit.Pane].Y,
                views.Select((frame, index) => index == edit.Pane ? 0 : frame.Height + _offsets[index].Y).Max()) * views.Length
            + (_orientations[edit.Pane].IsIdentity ? 0 : (long)nextRaw.Width * nextRaw.Height));
        CheckWork(work);
        views[edit.Pane] = _orientations[edit.Pane].Apply(nextRaw, token);
        var compared = ImageRegionDiffer.Compare(views, _blockSize, _threshold, token, _offsets);
        token.ThrowIfCancellationRequested();
        _frames = candidate; _viewFrames = views; Regions = compared; _index += redo ? 1 : -1; _work += work;
        return true;
    }

    private void CheckWork(long work)
    {
        if (work > MaximumWork - _work) throw new InvalidOperationException("画像編集の累積作業量が256Mピクセルを超えます。");
    }
    private long ComparisonWork(IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames, IReadOnlyList<ImageOffset>? offsets = null)
    { var size = ImageOffset.Canvas(frames, offsets ?? _offsets); return checked((long)size.Width * size.Height * frames.Count); }
    private static long RecordBytes(Edit edit) => edit.Before.Pixels.LongLength + edit.After.Pixels.LongLength;
    private static ImageComparisonEngine.DecodedFrame Clone(ImageComparisonEngine.DecodedFrame frame)
        => new(frame.Number, frame.Width, frame.Height, (byte[])frame.Pixels.Clone());
}

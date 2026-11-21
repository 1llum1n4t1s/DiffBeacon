namespace DiffBeacon.App;

// WinIMerge v1.0.54 ImgMergeBuffer.hpp の領域コピー・共有履歴を移植。
// GPL-2.0-or-later。原本・採取境界は tests/Fixtures/ImageCopy を参照。
// 静止画専用。拡張の初期値は明示したzero-filled BGRA契約。
internal sealed partial class ImageEditSession
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
    private ImageComparisonEngine.DecodedFrame[] _alignedFrames;
    private ImageLineAlignment.Result? _alignment;
    private int _insertionDeletionMode;
    private ImageOrientation[] _orientations;
    private ImageOffset[] _offsets;
    private int _index = -1;
    private long _historyBytes;
    private long _work;
    internal ImageRegionDiffer.Result Regions { get; private set; }
    internal int PaneCount => _frames.Length;
    internal double Threshold => _threshold;
    internal int BlockSize => _blockSize;
    internal int InsertionDeletionMode => _insertionDeletionMode;
    internal ImageLineAlignment.Result? Alignment => _alignment;
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
        _alignedFrames = (ImageComparisonEngine.DecodedFrame[])_viewFrames.Clone();
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
        _alignedFrames = (ImageComparisonEngine.DecodedFrame[])source._alignedFrames.Clone();
        _alignment = source._alignment; _insertionDeletionMode = source._insertionDeletionMode;
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
        var budget = NewBudget(token);
        var compared = CompareCandidate(_viewFrames, blockSize, _threshold, _offsets, _insertionDeletionMode, budget, token);
        token.ThrowIfCancellationRequested();
        _blockSize = blockSize; AdoptComparison(compared); _work += budget.Work;
    }

    internal void SetThreshold(double threshold, CancellationToken token = default)
    {
        if (!double.IsFinite(threshold) || threshold < 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        token.ThrowIfCancellationRequested();
        var budget = NewBudget(token);
        var compared = CompareCandidate(_viewFrames, _blockSize, threshold, _offsets, _insertionDeletionMode, budget, token);
        token.ThrowIfCancellationRequested();
        _threshold = threshold; AdoptComparison(compared); _work += budget.Work;
    }

    internal void SetInsertionDeletionMode(int mode, CancellationToken token = default)
    {
        if (mode is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(mode));
        token.ThrowIfCancellationRequested();
        if (_insertionDeletionMode == mode) return;
        var budget = NewBudget(token);
        var compared = CompareCandidate(_viewFrames, _blockSize, _threshold, _offsets, mode, budget, token);
        token.ThrowIfCancellationRequested();
        _insertionDeletionMode = mode; AdoptComparison(compared); _work += budget.Work;
    }

    internal void SetReadOnly(int pane, bool readOnly)
    {
        if (!ValidPane(pane)) throw new ArgumentOutOfRangeException(nameof(pane));
        _readOnly[pane] = readOnly;
    }

    internal IReadOnlyList<ImageComparisonEngine.DecodedFrame> CaptureFrames() => _frames.Select(Clone).ToArray();
    internal IReadOnlyList<ImageComparisonEngine.DecodedFrame> CaptureViewFrames() => _viewFrames.Select(Clone).ToArray();
    internal IReadOnlyList<ImageComparisonEngine.DecodedFrame> CaptureAlignedFrames() => _alignedFrames.Select(Clone).ToArray();
    internal ImageLineAlignment.Position ConvertToRealPosition(int pane, int x, int y, bool clamp = true)
    {
        if (!ValidPane(pane)) throw new ArgumentOutOfRangeException(nameof(pane));
        return ConvertPosition(pane, x, y, _offsets, clamp);
    }
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
        var budget = NewBudget(token);
        var views = (ImageComparisonEngine.DecodedFrame[])_viewFrames.Clone();
        for (var pane = 0; pane < PaneCount; pane++) if (_orientations[pane] != requestedOrientations[pane])
        {
            if (!requestedOrientations[pane].IsIdentity) budget.ReservePixels(_frames[pane].Pixels.Length);
            budget.Spend((long)_frames[pane].Width * _frames[pane].Height);
            views[pane] = requestedOrientations[pane].Apply(_frames[pane], token);
        }
        var compared = CompareCandidate(views, _blockSize, _threshold, requested, _insertionDeletionMode, budget, token);
        token.ThrowIfCancellationRequested();
        _offsets = requested; _orientations = requestedOrientations; _viewFrames = views;
        AdoptComparison(compared); _work += budget.Work;
    }
    internal void AddOffset(int pane, int dx, int dy, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var offsets = ImageOffset.Move(_offsets, pane, dx, dy);
        var budget = NewBudget(token);
        var compared = CompareCandidate(_viewFrames, _blockSize, _threshold, offsets, _insertionDeletionMode, budget, token);
        token.ThrowIfCancellationRequested();
        _offsets = offsets; AdoptComparison(compared); _work += budget.Work;
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
        // 原本のall/autoは比較時のRegions/区間を固定し、rawviewだけ逐次変更する。
        // 元viewは借用のまま保持し、candidateだけを書き換えて一件の履歴にする。
        var rawBefore = _frames[destination];
        var before = _viewFrames[destination];
        var retainedBytes = _historyBytes;
        for (var i = _history.Count - 1; i > _index; i--) retainedBytes -= RecordBytes(_history[i]);
        if (_index + 2 > MaximumHistoryRecords || retainedBytes + rawBefore.Pixels.LongLength + before.Pixels.LongLength > MaximumHistoryBytes)
            throw new InvalidOperationException("画像編集の履歴上限（128件・256 MiB）を超えます。");
        var budget = NewBudget(token);
        budget.ReservePixels(before.Pixels.Length);
        var next = Clone(before);
        var copyOffsets = (ImageOffset[])_offsets.Clone();
        foreach (var (index, source) in copies)
            next = CopyRegion(index, source, destination, next, copyOffsets, budget, token);
        if (retainedBytes + rawBefore.Pixels.LongLength + next.Pixels.LongLength > MaximumHistoryBytes)
            throw new InvalidOperationException("画像編集の履歴上限（128件・256 MiB）を超えます。");
        AdoptEdit(destination, next, copyOffsets, retainedBytes, budget, token);
    }

    private void AdoptEdit(int destination, ImageComparisonEngine.DecodedFrame next, ImageOffset[] copyOffsets,
        long retainedBytes, OperationBudget budget, CancellationToken token)
    {
        var rawBefore = _frames[destination];
        var candidate = (ImageComparisonEngine.DecodedFrame[])_viewFrames.Clone(); candidate[destination] = next;
        var compared = CompareCandidate(candidate, _blockSize, _threshold, copyOffsets, _insertionDeletionMode, budget, token);
        var counters = (int[])_counts.Clone();
        for (var i = _history.Count - 1; i > _index; i--) --counters[_history[i].Pane];
        ++counters[destination];
        if (!_orientations[destination].IsIdentity)
        { budget.ReservePixels(next.Pixels.Length); budget.Spend((long)next.Width * next.Height); }
        var rawNext = _orientations[destination].Apply(next, token, inverse: true);
        var rawCandidate = (ImageComparisonEngine.DecodedFrame[])_frames.Clone(); rawCandidate[destination] = rawNext;
        var edit = new Edit(destination, rawBefore, rawNext, counters);
        token.ThrowIfCancellationRequested();
        if (_history.Count > _index + 1) _history.RemoveRange(_index + 1, _history.Count - _index - 1);
        _history.Add(edit); ++_index;
        Array.Copy(counters, _counts, counters.Length);
        _historyBytes = retainedBytes + RecordBytes(edit); _work += budget.Work;
        _frames = rawCandidate; _viewFrames = candidate; _offsets = copyOffsets; AdoptComparison(compared);
    }

    private (int Dx, int Dy, int Ox, int Oy) Expansion(ImageRegionDiffer.Region rc, int source, int destination,
        ImageComparisonEngine.DecodedFrame before, IReadOnlyList<ImageOffset> offsets)
    {
        var input = _alignedFrames[source]; var src = offsets[source]; var dst = offsets[destination];
        var xmin = Math.Max(rc.Left * _blockSize, src.X); var ymin = Math.Max(rc.Top * _blockSize, src.Y);
        var xmax = Math.Min(rc.Right * _blockSize - 1, input.Width + src.X - 1);
        var ymax = Math.Min(rc.Bottom * _blockSize - 1, input.Height + src.Y - 1);
        var ox = Math.Max(0, dst.X - xmin); var oy = Math.Max(0, dst.Y - ymin);
        return (checked(ox + Math.Max(0, xmax - (before.Width + dst.X - 1))),
            checked(oy + Math.Max(0, ymax - (before.Height + dst.Y - 1))), ox, oy);
    }

    private ImageComparisonEngine.DecodedFrame CopyRegion(int index, int source, int destination,
        ImageComparisonEngine.DecodedFrame target, ImageOffset[] offsets, OperationBudget budget, CancellationToken token)
    {
        var rc = Regions.Regions[index]; var input = _viewFrames[source];
        var originalDestination = _alignedFrames[destination];
        var expansion = Expansion(rc, source, destination, originalDestination, offsets);
        var width = target.Width + expansion.Dx;
        var height = target.Height + expansion.Dy;
        if (width != target.Width || height != target.Height)
        {
            var expandedOffsets = (ImageOffset[])offsets.Clone();
            expandedOffsets[destination] = new(offsets[destination].X - expansion.Ox,
                offsets[destination].Y - expansion.Oy);
            ValidateEditCanvas(width, height, destination, expandedOffsets);
            var expanded = AllocatePixels(width, height, budget);
            budget.Spend((long)target.Width * target.Height);
            for (var y = 0; y < target.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                target.Pixels.AsSpan(y * target.Width * 4, target.Width * 4)
                    .CopyTo(expanded.AsSpan(((y + expansion.Oy) * width + expansion.Ox) * 4));
            }
            target = new(target.Number, width, height, expanded);
            offsets[destination] = new(offsets[destination].X - expansion.Ox, offsets[destination].Y - expansion.Oy);
        }
        budget.Spend(checked((long)(rc.Right - rc.Left) * (rc.Bottom - rc.Top) * _blockSize * _blockSize));
        for (var by = rc.Top; by < rc.Bottom; by++)
        {
            token.ThrowIfCancellationRequested();
            for (var bx = rc.Left; bx < rc.Right; bx++)
            {
                if (Regions.RegionIds[by * Regions.Columns + bx] != index + 1) continue;
                if (_alignment is not null)
                {
                    for (var dy = 0; dy < _blockSize; dy++)
                    for (var dx = 0; dx < _blockSize; dx++)
                    {
                        // 逆写像の区間scanを上から課金し、深い行対応も共有予算へ含める。
                        budget.Spend(2L * _alignment.LineDiffInfos.Count + 1);
                        var x = bx * _blockSize + dx; var y = by * _blockSize + dy;
                        var from = ConvertPosition(source, x, y, offsets);
                        if (!from.Inside) continue;
                        var to = ConvertPosition(destination, x, y, offsets);
                        if (!to.Inside) continue;
                        ValidatePixel(input, from.X, from.Y); ValidatePixel(target, to.X, to.Y);
                        input.Pixels.AsSpan((from.Y * input.Width + from.X) * 4, 4)
                            .CopyTo(target.Pixels.AsSpan((to.Y * target.Width + to.X) * 4, 4));
                    }
                    continue;
                }
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
        // 原本はpixelcopyを終えた後、全slotのoffsetが0の場合だけ構造編集する。
        if (_alignment is not null && offsets.All(value => value.X == 0 && value.Y == 0))
            target = CopyStructure(rc, source, destination, input, target, offsets, budget, token);
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
        var budget = NewBudget(token);
        if (!_orientations[edit.Pane].IsIdentity)
        { budget.ReservePixels(nextRaw.Pixels.Length); budget.Spend((long)nextRaw.Width * nextRaw.Height); }
        views[edit.Pane] = _orientations[edit.Pane].Apply(nextRaw, token);
        var compared = CompareCandidate(views, _blockSize, _threshold, _offsets, _insertionDeletionMode, budget, token);
        token.ThrowIfCancellationRequested();
        _frames = candidate; _viewFrames = views; AdoptComparison(compared);
        _index += redo ? 1 : -1; _work += budget.Work;
        return true;
    }

    // ImgMergeBuffer.hpp:640–688。元比較時の区間を逆順に処理する。
    private ImageComparisonEngine.DecodedFrame CopyStructure(ImageRegionDiffer.Region region,
        int source, int destination, ImageComparisonEngine.DecodedFrame input,
        ImageComparisonEngine.DecodedFrame target, ImageOffset[] offsets, OperationBudget budget, CancellationToken token)
    {
        var infos = _alignment!.LineDiffInfos;
        var horizontal = _insertionDeletionMode == 2;
        var start = (horizontal ? region.Left : region.Top) * _blockSize;
        var end = (horizontal ? region.Right : region.Bottom) * _blockSize;
        for (var i = infos.Count - 1; i >= 0; i--)
        {
            budget.Spend();
            var info = infos[i];
            if (start > info.DisplayBegin || info.DisplayEnd[source] >= end) continue;
            var delta = info.DisplayEnd[source] - info.DisplayEnd[destination];
            if (delta > 0)
            {
                target = ResizeAxis(target, info.End[destination] + 1, delta, horizontal, true, destination, offsets, budget, token);
                var length = info.End[source] + 1 - info.Begin[source];
                if (!horizontal)
                {
                    // 原本はdst幅をcopyする。src行を越える場合だけ明示拒否する。
                    if (target.Width > input.Width) throw UnsafeCopy();
                    for (var row = 0; row < length; row++)
                    {
                        budget.Spend(target.Width);
                        var sy = info.Begin[source] + row; var ty = info.Begin[destination] + row;
                        ValidatePixel(input, 0, sy); ValidatePixel(target, 0, ty);
                        input.Pixels.AsSpan(sy * input.Width * 4, target.Width * 4)
                            .CopyTo(target.Pixels.AsSpan(ty * target.Width * 4, target.Width * 4));
                    }
                }
                else
                {
                    if (input.Height > target.Height || info.Begin[source] < 0 || info.Begin[destination] < 0
                        || (long)info.Begin[source] + length > input.Width
                        || (long)info.Begin[destination] + length > target.Width) throw UnsafeCopy();
                    for (var row = 0; row < input.Height; row++)
                    {
                        budget.Spend(length);
                        input.Pixels.AsSpan((row * input.Width + info.Begin[source]) * 4, length * 4)
                            .CopyTo(target.Pixels.AsSpan((row * target.Width + info.Begin[destination]) * 4, length * 4));
                    }
                }
            }
            else if (delta < 0)
                target = ResizeAxis(target, info.End[destination] + 1 + delta, -delta, horizontal, false, destination, offsets, budget, token);
        }
        return target;
    }

    // ImgMergeBuffer.hpp:495–538。挿入部はzero-filled、元領域を前後へcopyする。
    private ImageComparisonEngine.DecodedFrame ResizeAxis(ImageComparisonEngine.DecodedFrame target,
        int position, int count, bool horizontal, bool insert, int destination, ImageOffset[] offsets,
        OperationBudget budget, CancellationToken token)
    {
        var axis = horizontal ? target.Width : target.Height;
        if (position < 0 || position > axis || count <= 0 || !insert && (long)position + count > axis) throw UnsafeCopy();
        var nextAxis = checked(axis + (insert ? count : -count));
        var width = horizontal ? nextAxis : target.Width;
        var height = horizontal ? target.Height : nextAxis;
        ValidateEditCanvas(width, height, destination, offsets);
        var pixels = AllocatePixels(width, height, budget);
        if (!horizontal)
        {
            budget.Spend((long)target.Width * position);
            target.Pixels.AsSpan(0, position * target.Width * 4).CopyTo(pixels);
            var oldStart = insert ? position : position + count;
            var nextStart = insert ? position + count : position;
            var length = target.Height - oldStart;
            budget.Spend((long)target.Width * length);
            target.Pixels.AsSpan(oldStart * target.Width * 4, length * target.Width * 4)
                .CopyTo(pixels.AsSpan(nextStart * width * 4));
        }
        else
        {
            var oldStart = insert ? position : position + count;
            var nextStart = insert ? position + count : position;
            var length = target.Width - oldStart;
            for (var row = 0; row < target.Height; row++)
            {
                token.ThrowIfCancellationRequested();
                budget.Spend(position + length);
                target.Pixels.AsSpan(row * target.Width * 4, position * 4)
                    .CopyTo(pixels.AsSpan(row * width * 4));
                target.Pixels.AsSpan((row * target.Width + oldStart) * 4, length * 4)
                    .CopyTo(pixels.AsSpan((row * width + nextStart) * 4));
            }
        }
        token.ThrowIfCancellationRequested();
        return new(target.Number, width, height, pixels);
    }

    private static byte[] AllocatePixels(int width, int height, OperationBudget budget)
    {
        if (width <= 0 || height <= 0 || (long)width * height > ImageComparisonEngine.MaximumPixels)
            throw new InvalidOperationException("編集キャンバスが1600万ピクセルを超えるか空になります。");
        var bytes = checked(width * height * 4);
        budget.ReservePixels(bytes);
        return new byte[bytes];
    }

    private void ValidateEditCanvas(int width, int height, int destination, IReadOnlyList<ImageOffset> offsets)
    {
        var canvasWidth = checked(width + offsets[destination].X);
        var canvasHeight = checked(height + offsets[destination].Y);
        for (var pane = 0; pane < PaneCount; pane++) if (pane != destination)
        {
            canvasWidth = Math.Max(canvasWidth, checked(_alignedFrames[pane].Width + offsets[pane].X));
            canvasHeight = Math.Max(canvasHeight, checked(_alignedFrames[pane].Height + offsets[pane].Y));
        }
        if ((long)canvasWidth * canvasHeight > ImageComparisonEngine.MaximumPixels)
            throw new InvalidOperationException("編集キャンバスが1600万ピクセルを超えます。");
    }

    private static void ValidatePixel(ImageComparisonEngine.DecodedFrame frame, int x, int y)
    {
        if ((uint)x >= (uint)frame.Width || (uint)y >= (uint)frame.Height) throw UnsafeCopy();
    }

    private static InvalidDataException UnsafeCopy() => new("画像構造コピーの原本経路が画像の範囲外を参照します。");

    private ImageLineAlignment.Position ConvertPosition(int pane, int x, int y, IReadOnlyList<ImageOffset> offsets, bool clamp = true)
    {
        var lx = (long)x - offsets[pane].X; var ly = (long)y - offsets[pane].Y;
        if (lx < int.MinValue || lx > int.MaxValue || ly < int.MinValue || ly > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(x), "画像位置を減算した座標が整数範囲外です。");
        x = (int)lx; y = (int)ly;
        if (_alignment is not null) return _alignment.ConvertToRealPosition(pane, x, y, clamp);
        var frame = _alignedFrames[pane];
        var inside = x >= 0 && x < frame.Width && y >= 0 && y < frame.Height;
        return new(inside, clamp ? Math.Clamp(x, 0, frame.Width - 1) : x, clamp ? Math.Clamp(y, 0, frame.Height - 1) : y);
    }

    private sealed record ComparisonCandidate(ImageComparisonEngine.DecodedFrame[] Aligned,
        ImageLineAlignment.Result? Alignment, ImageRegionDiffer.Result Regions);

    private ComparisonCandidate CompareCandidate(ImageComparisonEngine.DecodedFrame[] views,
        int blockSize, double threshold, ImageOffset[] offsets, int mode, OperationBudget budget, CancellationToken token)
    {
        ImageLineAlignment.Result? alignment = null;
        var aligned = views;
        if (mode != 0)
        {
            alignment = ImageLineAlignment.Align(views, mode == 2, threshold, token,
                Math.Min(ImageLineDiffer.MaximumWork, budget.Remaining));
            budget.Spend(alignment.Work);
            aligned = alignment.Frames.ToArray();
        }
        budget.Spend(ComparisonWork(aligned, offsets));
        var compared = ImageRegionDiffer.Compare(aligned, blockSize, threshold, token, offsets);
        token.ThrowIfCancellationRequested();
        return new(aligned, alignment, compared);
    }

    private void AdoptComparison(ComparisonCandidate candidate)
    { _alignedFrames = candidate.Aligned; _alignment = candidate.Alignment; Regions = candidate.Regions; }

    private OperationBudget NewBudget(CancellationToken token) => new(MaximumWork - _work, token);

    private sealed class OperationBudget(long limit, CancellationToken token)
    {
        internal long Work { get; private set; }
        internal long Remaining => limit - Work;
        private long allocatedPixels;
        private int calls;

        internal void Spend(long work = 1)
        {
            if ((calls++ & 1023) == 0) token.ThrowIfCancellationRequested();
            if (work < 0 || work > Remaining)
                throw new InvalidOperationException("画像編集の累積作業量が256Mピクセルを超えます。");
            Work += work;
        }

        internal void ReservePixels(int bytes)
        {
            token.ThrowIfCancellationRequested();
            if (bytes > MaximumHistoryBytes - allocatedPixels)
                throw new InvalidOperationException("画像編集候補の累積画素確保量が256 MiBを超えます。");
            Spend(bytes / 4);
            allocatedPixels += bytes;
        }
    }

    private long ComparisonWork(IReadOnlyList<ImageComparisonEngine.DecodedFrame> frames, IReadOnlyList<ImageOffset>? offsets = null)
    { var size = ImageOffset.Canvas(frames, offsets ?? _offsets); return checked((long)size.Width * size.Height * frames.Count); }
    private static long RecordBytes(Edit edit) => edit.Before.Pixels.LongLength + edit.After.Pixels.LongLength;
    private static ImageComparisonEngine.DecodedFrame Clone(ImageComparisonEngine.DecodedFrame frame)
        => new(frame.Number, frame.Width, frame.Height, (byte[])frame.Pixels.Clone());
}

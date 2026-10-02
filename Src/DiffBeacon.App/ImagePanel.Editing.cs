using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DiffBeacon.App;

public static partial class SpecializedViews
{
    public sealed partial class ImagePanel
    {
        private ImageEditSession? _editSession;
        private bool[] _readOnly = [];
        private string[] _paths = [];
        private readonly HashSet<string> _originPaths = new(StringComparer.Ordinal);
        private Func<IEnumerable<string>>? _protectedPaths;
        private Action<string>? _outputGuard;
        private Action<int, string>? _savedPath;
        private bool _saving, _resetEditing, _discarded;
        private CancellationTokenSource? _saveCancellation;
        private readonly ComboBox _copyDirection = new() { Name = "ImageCopyDirection", MinWidth = 135, SelectedIndex = 0 };
        private readonly ComboBox _editPane = new() { Name = "ImageEditPane", MinWidth = 90, SelectedIndex = 0 };
        private (int Source, int Destination)[] _copyPairs = [];
        private readonly List<(Button Button, string Kind)> _editButtons = [];
        // headless操作検証で、候補計算済み・画面採用前の境界を決定的に待機させる。
        internal Func<Task>? EditCandidateReady { get; set; }

        internal bool HasUnsavedChanges => !_discarded && _editSession is not null && Enumerable.Range(0, _counts.Length).Any(_editSession.IsModified);
        internal int HistoryIndex => _editSession?.HistoryIndex ?? -1;
        internal int HistoryCount => _editSession?.HistoryCount ?? 0;
        internal IReadOnlyList<string> SourceProtectionPaths => _originPaths.Concat(_paths).ToArray();
        internal bool PaneModified(int pane) => !_discarded && _editSession?.IsModified(pane) == true;
        internal void EnsureNotSaving()
        {
            if (_saving) throw new InvalidOperationException("PNG保存が完了してから比較を開き直すか編集を破棄してください。");
        }
        internal IReadOnlyList<ImageComparisonEngine.DecodedFrame> CaptureEditFrames()
        {
            RequireEditing();
            if (_operationCancellation is not null) throw new InvalidOperationException("画像の操作が完了してから画素を採取してください。");
            return _editSession!.CaptureFrames();
        }

        internal void ConfigureEditing(IReadOnlyList<string> paths, IReadOnlyList<bool> readOnly,
            Func<IEnumerable<string>>? protectedPaths = null, Action<string>? outputGuard = null, Action<int, string>? savedPath = null)
        {
            if (paths.Count != _counts.Length) throw new ArgumentException("画像パスの指定数が入力数と一致しません。");
            _paths = paths.ToArray();
            var initialProtection = paths.Concat(protectedPaths?.Invoke() ?? []).ToArray();
            foreach (var path in initialProtection) if (!string.IsNullOrWhiteSpace(path)) _originPaths.Add(path);
            _protectedPaths = protectedPaths; _outputGuard = outputGuard; _savedPath = savedPath;
            SetReadOnly(readOnly);
        }

        internal void SetReadOnly(IReadOnlyList<bool> values)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (values.Count != _counts.Length) throw new ArgumentException("読取り専用の指定数が入力数と一致しません。");
            for (var pane = 0; pane < values.Count; pane++) { _readOnly[pane] = values[pane]; _editSession?.SetReadOnly(pane, values[pane]); }
            if (_resizeMode != 0 && _dragPane >= 0 && _readOnly[_dragPane]) CancelRectangleInteraction();
            UpdateRectangleVisuals();
            UpdateNavigation();
        }

        private WrapPanel CreateEditControls(string[] labels)
        {
            _copyPairs = Enumerable.Range(0, labels.Length).SelectMany(source => Enumerable.Range(0, labels.Length)
                .Where(destination => source != destination).Select(destination => (source, destination))).ToArray();
            _copyDirection.ItemsSource = _copyPairs.Select(pair => labels[pair.Source] + " → " + labels[pair.Destination]).ToArray();
            _editPane.ItemsSource = labels;
            _copyDirection.SelectionChanged += (_, _) => UpdateEditControls();
            _editPane.SelectionChanged += (_, _) => { ++_rectangleEpoch; _clipboardCancellation?.Cancel(); UpdateEditControls(); };
            var panel = new WrapPanel();
            Add(panel, _copyDirection);
            AddEditButton(panel, "ImageCopyRegion", "選択領域をコピー", "copy", () => CopyChosenAsync(false));
            AddEditButton(panel, "ImageCopyAll", "全領域をコピー", "all", () => CopyChosenAsync(true));
            Add(panel, _editPane);
            Add(panel, new TextBlock { Text = "差分ブロック" }); Add(panel, _blockSizeControl);
            AddEditButton(panel, "ImageRotateLeft", "左90°", "orientation", () => RotateChosenAsync(90));
            AddEditButton(panel, "ImageRotateRight", "右90°", "orientation", () => RotateChosenAsync(-90));
            AddEditButton(panel, "ImageFlipHorizontal", "左右反転", "orientation", () => FlipChosenAsync(true));
            AddEditButton(panel, "ImageFlipVertical", "上下反転", "orientation", () => FlipChosenAsync(false));
            AddEditButton(panel, "ImageOffsetLeft", "位置 ←", "orientation", () => MoveChosenAsync(-1, 0));
            AddEditButton(panel, "ImageOffsetRight", "位置 →", "orientation", () => MoveChosenAsync(1, 0));
            AddEditButton(panel, "ImageOffsetUp", "位置 ↑", "orientation", () => MoveChosenAsync(0, -1));
            AddEditButton(panel, "ImageOffsetDown", "位置 ↓", "orientation", () => MoveChosenAsync(0, 1));
            AddEditButton(panel, "ImageAutoMerge", "競合以外を自動コピー", "auto", () => AutoMergeAsync(_editPane.SelectedIndex));
            AddEditButton(panel, "ImageUndo", "元に戻す", "undo", () => UndoEditAsync());
            AddEditButton(panel, "ImageRedo", "やり直す", "redo", () => RedoEditAsync());
            AddEditButton(panel, "ImageCancelEdit", "中止", "cancel", () => { _operationCancellation?.Cancel(); _saveCancellation?.Cancel(); CancelRectangleInteraction(); return Task.CompletedTask; });
            AddRectangleControls(panel);
            AddEditButton(panel, "ImageSavePng", "PNGで別名保存…", "save", PickPngAsync);
            return panel;
        }

        private Task CopyChosenAsync(bool all)
        {
            var index = _copyDirection.SelectedIndex;
            if (index < 0 || index >= _copyPairs.Length) return Task.CompletedTask;
            var pair = _copyPairs[index]; return CopyRegionAsync(pair.Source, pair.Destination, all);
        }

        private void AddEditButton(Panel panel, string name, string label, string kind, Func<Task> action)
        {
            var button = new Button { Name = name, Content = label }; _editButtons.Add((button, kind)); Add(panel, button);
            button.Click += async (_, _) =>
            {
                var generation = _generation;
                try { var task = action(); generation = _generation; await task; }
                catch (OperationCanceledException) { if (!_disposed && generation == _generation) _status.Text = "画像の操作を中止しました。"; }
                catch (Exception error) { if (!_disposed && generation == _generation) _status.Text = "画像の操作を完了できません: " + error.Message; }
                finally { if (!_disposed) UpdateEditControls(); }
            };
        }

        private void RequireEditing()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_saving) throw new InvalidOperationException("PNG保存が完了してから画像を編集してください。");
            if (_editSession is null || _resetEditing || _counts.Any(count => count != 1))
                throw new InvalidOperationException("画像コピーとPNG保存は、読込みが完了した静止画の比較で使用してください。");
        }

        private Task EditAsync(Action<ImageEditSession, CancellationToken> edit, int writablePane, CancellationToken token)
        {
            RequireEditing(); token.ThrowIfCancellationRequested();
            if (writablePane >= 0 && (writablePane >= _counts.Length || _readOnly[writablePane]))
                throw new InvalidOperationException("コピー先は読取り専用か、存在しない画像です。");
            CancelRectangleInteraction(preservePointerPress: true);
            _operationCancellation?.Cancel();
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token); _operationCancellation = cancel;
            CurrentFrameOperation = LoadFramesAsync(_numbers.ToArray(), _displayThreshold, _displayShowDifferences, ++_generation, cancel, edit, writablePane);
            UpdateEditControls(); return CurrentFrameOperation;
        }

        internal Task CopyRegionAsync(int source, int destination, bool all = false, CancellationToken token = default)
        {
            if (source < 0 || source >= _counts.Length || destination < 0 || destination >= _counts.Length || source == destination)
                throw new ArgumentException("異なる入力画像をコピー元・コピー先へ指定してください。");
            var selected = _selectedDiffIndex;
            if (!all && selected < 0) throw new InvalidOperationException("コピーする領域を選択してください。");
            return EditAsync((session, cancellation) => { if (all) session.CopyAll(source, destination, cancellation); else session.Copy(selected, source, destination, cancellation); }, destination, token);
        }
        internal Task AutoMergeAsync(int destination, CancellationToken token = default)
        {
            if (_counts.Length != 3) throw new InvalidOperationException("自動コピーは三者の静止画像で使用してください。");
            if (destination < 0 || destination >= _counts.Length) throw new ArgumentOutOfRangeException(nameof(destination));
            return EditAsync((session, cancellation) => session.AutoMerge(destination, cancellation), destination, token);
        }
        internal Task UndoEditAsync(CancellationToken token = default) => EditAsync((session, cancellation) => session.Undo(cancellation), -1, token);
        internal Task RedoEditAsync(CancellationToken token = default) => EditAsync((session, cancellation) => session.Redo(cancellation), -1, token);

        internal void DiscardChanges()
        {
            if (_disposed) return;
            EnsureNotSaving();
            _discarded = _resetEditing = true;
            _ = SelectFromControlsAsync();
        }

        private IEnumerable<string> ProtectedImagePaths() => _originPaths.Concat(_paths).Concat(_protectedPaths?.Invoke() ?? []);
        internal async Task SaveToAsync(int pane, string output, CancellationToken token = default)
        {
            RequireEditing();
            if (HasFloatingImage || _clipboardBusy) throw new InvalidOperationException("貼り付けを確定し、クリップボード操作が完了してからPNGを保存してください。");
            if (_operationCancellation is not null) throw new InvalidOperationException("画像の操作が完了してから保存してください。");
            if (pane < 0 || pane >= _counts.Length) throw new ArgumentOutOfRangeException(nameof(pane));
            if (_readOnly[pane]) throw new InvalidOperationException("読取り専用の画像を保存できません。");
            if (_paths.Length != _counts.Length) throw new InvalidOperationException("画像の入力パスを確定してから保存してください。");
            var target = ImagePngStore.ValidateTarget(output, ProtectedImagePaths());
            _outputGuard?.Invoke(target);
            var session = _editSession!; var frame = session.CaptureFrame(pane);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            _saveCancellation = cancel; _saving = true; UpdateNavigation();
            void Guard(string path)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_readOnly[pane]) throw new InvalidOperationException("保存する画像が読取り専用になりました。");
                _outputGuard?.Invoke(path);
            }
            try
            {
                var saved = await ImagePngStore.SaveAsync(target, frame, ProtectedImagePaths(), cancel.Token, ProtectedImagePaths, Guard);
                if (_disposed) return;
                // 保存中は編集・ページ変更を禁止し、成功したrevisionだけをcleanにする。
                _snapshots![pane] = saved; _paths[pane] = target;
                _savedPath?.Invoke(pane, target);
                session.MarkSaved(pane);
                _status.Text = "原画をPNGで保存しました。";
            }
            finally { _saving = false; _saveCancellation = null; if (!_disposed) UpdateNavigation(); }
        }

        private async Task PickPngAsync()
        {
            RequireEditing(); var top = TopLevel.GetTopLevel(this); if (top is null) return;
            var pane = _editPane.SelectedIndex;
            if (pane < 0 || pane >= _paths.Length) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "原画をPNGで別名保存", SuggestedFileName = Path.GetFileNameWithoutExtension(_paths[pane]) + "-edited.png",
                FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }], ShowOverwritePrompt = true
            });
            if (file is null) return;
            await SaveToAsync(pane, file.TryGetLocalPath() ?? throw new IOException("ローカルの保存先を選択してください。"));
        }

        private void UpdateEditControls()
        {
            var ready = !_disposed && !_saving && !_resetEditing && _editSession is not null && _operationCancellation is null;
            var pane = _editPane.SelectedIndex;
            var canWrite = pane >= 0 && pane < _readOnly.Length && !_readOnly[pane];
            var direction = _copyDirection.SelectedIndex;
            var copyWritable = direction >= 0 && direction < _copyPairs.Length && !_readOnly[_copyPairs[direction].Destination];
            foreach (var (button, kind) in _editButtons)
                button.IsEnabled = kind switch
                {
                    "cancel" => _saving || _operationCancellation is not null,
                    "orientation" => !_disposed && !_saving,
                    "copy" => ready && copyWritable && _selectedDiffIndex >= 0,
                    "all" => ready && copyWritable,
                    "auto" => ready && canWrite && _counts.Length == 3,
                    "undo" => ready && _editSession!.CanUndo,
                    "redo" => ready && _editSession!.CanRedo,
                    "save" => ready && canWrite && _paths.Length == _counts.Length,
                    "rectangle-select" => RectangleReady,
                    "rectangle-copy" => RectangleReady && RectangleSelection(pane) is not null,
                    "rectangle-cut" or "rectangle-delete" => RectangleReady && canWrite && RectangleSelection(pane) is not null,
                    "rectangle-paste" => RectangleReady && canWrite,
                    "rectangle-commit" => RectangleReady && HasFloatingImage && !_readOnly[_floatingPane],
                    "rectangle-cancel" => HasFloatingImage || _rectangles.Any(value => value is not null) || _clipboardBusy,
                    _ => false
                };
            _copyDirection.IsEnabled = _editPane.IsEnabled = !_saving;
        }
    }
}

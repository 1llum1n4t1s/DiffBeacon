using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiffBeacon.App;

// 実際の比較ペイン・表操作・保存経路を使い、検索結果と原文を成果物へ残す。
internal static class HeadlessTableSearchTests
{
    internal static void Run(MainWindow window, ComparisonPane pane, string output, Action<string, bool, string> check,
        Action<string> screenshot, Action<Task> pump, bool rawOnly = false)
    {
        var directory = Path.Combine(output, "table-search"); Directory.CreateDirectory(directory);
        var records = new List<(string Name, bool Passed, string Detail, string Left, string Right)>();
        var utf8 = new UTF8Encoding(false);
        string leftPath = "", rightPath = "", basePath = "";
        void Verify(string name, bool passed, string detail = "")
        { records.Add((name, passed, detail, pane.LeftEditor.Text ?? "", pane.RightEditor.Text ?? "")); check("table search " + name, passed, detail); }
        CheckBox Box(TablePanel panel, string label) => panel.GetVisualDescendants().OfType<CheckBox>().Single(box => Equals(box.Content, label));
        Button Button(TablePanel panel, string label) => panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label));
        void Click(TablePanel panel, string label)
        {
            Button(panel, label).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            if (panel.PendingOperation is { } operation) pump(operation);
            Dispatcher.UIThread.RunJobs();
        }
        bool Refused(Func<Task> operation)
        {
            try { pump(operation()); return false; }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or IOException or RegexMatchTimeoutException or OperationCanceledException)
            { return true; }
        }
        TablePanel Load(string name, string left, string? right = null, string? ancestor = null, bool leftReadOnly = false, bool allowNewlines = true, Encoding? encoding = null)
        {
            pane.DiscardChanges();
            leftPath = Path.Combine(directory, name + "-left.csv"); rightPath = Path.Combine(directory, name + "-right.csv");
            basePath = ancestor is null ? "" : Path.Combine(directory, name + "-base.csv");
            File.WriteAllText(leftPath, left, encoding ?? utf8); File.WriteAllText(rightPath, right ?? left, encoding ?? utf8);
            if (ancestor is not null) File.WriteAllText(basePath, ancestor, encoding ?? utf8);
            pane.ApplyProject(new ComparisonProject { LeftPath = leftPath, RightPath = rightPath, BasePath = basePath, Mode = "Table",
                TableDelimiter = ';', TableQuote = '\'', TableAllowNewlinesInQuotes = allowNewlines, LeftReadOnly = leftReadOnly });
            pane.DiscardChanges(); pump(pane.CompareProjectAsync()); Dispatcher.UIThread.RunJobs();
            Verify(name + " uses active real pane", ReferenceEquals(window.ActivePane, pane));
            return pane.GetVisualDescendants().OfType<TablePanel>().Single();
        }
        void Select(TablePanel panel, int side = 0, int row = 0, int column = 0)
        { panel.SelectCell(side, row, column); Box(panel, "全ペイン").IsChecked = false; }
        int Find(TablePanel panel, string text, int direction = 1)
        { panel.SearchText.Text = text; Click(panel, direction > 0 ? "次の一致" : "前の一致"); return panel.CellEditor.SelectionStart; }
        bool RawAt(TablePanel panel, int start, int length) => panel.SearchStatus.Contains($"CSV原文 offset {start} · {length}文字の一致", StringComparison.Ordinal);
        void RawChecks()
        {
            const string source = "alpha;'a;b';'raw''quote'\r\nlast;end";
            var rawPanel = Load("raw-literal", source); Select(rawPanel);
            Verify("raw mode is disabled by default", Box(rawPanel, "CSV原文を検索").IsChecked == false);
            Box(rawPanel, "CSV原文を検索").IsChecked = true;
            Find(rawPanel, ";"); Verify("raw finds separator without changing cell text", RawAt(rawPanel, 5, 1) && rawPanel.CellEditor.Text == "alpha" && rawPanel.CellEditor.SelectionStart == 5 && rawPanel.CellEditor.SelectionEnd == 5);
            Find(rawPanel, ";"); Verify("raw finds quoted delimiter at exact source offset", RawAt(rawPanel, 8, 1) && rawPanel.CellEditor.Text == "a;b" && rawPanel.CellEditor.SelectionStart == 1 && rawPanel.CellEditor.SelectionEnd == 2);
            Select(rawPanel); Find(rawPanel, "'a;b'");
            Verify("raw finds literal surrounding quotes and projects decoded selection", RawAt(rawPanel, 6, 5) && rawPanel.CellEditor.SelectionStart == 0 && rawPanel.CellEditor.SelectionEnd == 3);
            Select(rawPanel); Find(rawPanel, "''");
            Verify("raw finds escaped quote pair and selects one decoded quote", RawAt(rawPanel, source.IndexOf("''", StringComparison.Ordinal), 2) && rawPanel.CellEditor.Text == "raw'quote" && rawPanel.CellEditor.SelectionStart == 3 && rawPanel.CellEditor.SelectionEnd == 4);
            Select(rawPanel); Box(rawPanel, "正規表現").IsChecked = true;
            Find(rawPanel, @"(?s)a;b';.*?\r\nlast;");
            var multilineStart = source.IndexOf("a;b", StringComparison.Ordinal); var multilineEnd = source.IndexOf("end", StringComparison.Ordinal);
            Verify("raw regex spans cells and logical rows without decoded reconstruction", RawAt(rawPanel, multilineStart, multilineEnd - multilineStart) && rawPanel.CellEditor.SelectionEnd == 3);
            screenshot("table-search-raw-literal.png");

            rawPanel = Load("raw-directions", "a;a;a"); Select(rawPanel); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            Find(rawPanel, "a"); Verify("raw starts at first source match", RawAt(rawPanel, 0, 1));
            Find(rawPanel, "a"); Verify("raw next crosses source delimiter", RawAt(rawPanel, 2, 1));
            Find(rawPanel, "a", -1); Verify("raw reverse returns previous source match", RawAt(rawPanel, 0, 1));
            Find(rawPanel, "a", -1); Verify("raw reverse wraps to final source match", RawAt(rawPanel, 4, 1));
            Box(rawPanel, "折返し").IsChecked = false; Find(rawPanel, "a");
            Verify("raw no-wrap preserves current cell selection", rawPanel.SearchStatus == "一致する文字列はありません。" && rawPanel.CellEditor.Text == "a" && rawPanel.CellEditor.SelectionEnd == 1);
            Box(rawPanel, "折返し").IsChecked = true; Find(rawPanel, "a"); Verify("raw forward wraps to first source match", RawAt(rawPanel, 0, 1));
            Box(rawPanel, "全ペイン").IsChecked = true; Find(rawPanel, "a"); Find(rawPanel, "a"); Find(rawPanel, "a");
            Verify("raw all-panes advances to next document", RawAt(rawPanel, 0, 1) && rawPanel.GetVisualDescendants().OfType<ComboBox>().First().SelectedIndex == 1);

            rawPanel = Load("raw-structure", "left;right\r\nnext;tail"); Select(rawPanel); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            Find(rawPanel, ";"); rawPanel.ReplacementText.Text = "\r\n"; Click(rawPanel, "この一致を置換");
            const string changed = "left\r\nright\r\nnext;tail";
            Verify("raw current replacement changes row structure with one active pane", pane.LeftEditor.Text == changed && pane.RightEditor.Text == "left;right\r\nnext;tail" && rawPanel.Comparison.Documents[0].Rows.Count == 3);
            Click(rawPanel, "セル編集を戻す"); Verify("raw undo restores exact separators and CRLF", pane.LeftEditor.Text == "left;right\r\nnext;tail");
            Click(rawPanel, "セル編集をやり直す"); Verify("raw redo restores structural replacement", pane.LeftEditor.Text == changed);
            rawPanel.SearchText.Text = ";"; rawPanel.ReplacementText.Text = "\r\n"; Click(rawPanel, "ペイン内を全置換");
            Verify("raw all replacement parses changed table structure", pane.LeftEditor.Text == "left\r\nright\r\nnext\r\ntail" && rawPanel.Comparison.Documents[0].Rows.Count == 4);
            Click(rawPanel, "セル編集を戻す"); Verify("raw all replacement has one shared undo item", pane.LeftEditor.Text == changed);
            screenshot("table-search-raw-structure.png");

            rawPanel = Load("raw-remove-all", "a;b\r\nc;d"); Select(rawPanel, row: 1, column: 1); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            Box(rawPanel, "正規表現").IsChecked = true; rawPanel.SearchText.Text = "(?s).+"; rawPanel.ReplacementText.Text = "";
            Click(rawPanel, "ペイン内を全置換"); Verify("raw replace-all can remove entire table without stale selection", pane.LeftEditor.Text == "" && rawPanel.Comparison.Documents[0].Rows.Count == 0 && !rawPanel.HasPendingCellEdit);
            Click(rawPanel, "セル編集を戻す"); Verify("raw empty table undo restores source", pane.LeftEditor.Text == "a;b\r\nc;d");
            Click(rawPanel, "セル編集をやり直す"); Verify("raw empty table redo has valid coordinates", pane.LeftEditor.Text == "");

            rawPanel = Load("raw-fixed", "prefix;'hit hit';hit"); Select(rawPanel, column: 1); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            rawPanel.CellEditor.SelectionStart = 0; rawPanel.CellEditor.SelectionEnd = 7; Click(rawPanel, "検索範囲を固定");
            rawPanel.SearchText.Text = "hit"; rawPanel.ReplacementText.Text = "long"; Click(rawPanel, "ペイン内を全置換");
            Verify("raw fixed selection excludes quotes separators and outside cells", pane.LeftEditor.Text == "prefix;'long long';hit");
            rawPanel.ReplacementText.Text = "x"; rawPanel.SearchText.Text = "long"; Click(rawPanel, "ペイン内を全置換");
            Verify("raw fixed selection tracks exact replacement length", pane.LeftEditor.Text == "prefix;'x x';hit");
            Click(rawPanel, "セル編集を戻す"); Verify("raw scope and shared undo restore prior range", pane.LeftEditor.Text == "prefix;'long long';hit");
            rawPanel.ReplacementText.Text = "y"; Click(rawPanel, "ペイン内を全置換");
            Verify("raw undo restores expanded fixed range", pane.LeftEditor.Text == "prefix;'y y';hit");

            rawPanel = Load("raw-captures", "a;b"); Select(rawPanel); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            Box(rawPanel, "正規表現").IsChecked = true; rawPanel.SearchText.Text = "(?<separator>;)"; rawPanel.ReplacementText.Text = "${separator}$`$'";
            Click(rawPanel, "ペイン内を全置換"); Verify("raw regex replacement expands full source prefix suffix and group", pane.LeftEditor.Text == "a;abb");
            rawPanel = Load("raw-flags", "Cat;cat;scatter"); Select(rawPanel); Box(rawPanel, "CSV原文を検索").IsChecked = true; Box(rawPanel, "単語単位").IsChecked = true;
            rawPanel.SearchText.Text = "cat"; rawPanel.ReplacementText.Text = "dog"; Box(rawPanel, "大小文字を区別").IsChecked = true;
            Click(rawPanel, "ペイン内を全置換"); Verify("raw case and whole-word settings use full source context", pane.LeftEditor.Text == "Cat;dog;scatter");

            rawPanel = Load("raw-readonly", "a;b", leftReadOnly: true); Select(rawPanel); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            Find(rawPanel, ";"); rawPanel.ReplacementText.Text = "\n";
            Verify("raw read-only permits search and refuses current and all replacement", RawAt(rawPanel, 1, 1) && Refused(rawPanel.ReplaceCurrentAsync) && Refused(rawPanel.ReplaceAllAsync) && pane.LeftEditor.Text == "a;b");
            rawPanel = Load("raw-malformed", "a;'quoted';b"); Select(rawPanel); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            Find(rawPanel, "'quoted'"); rawPanel.ReplacementText.Text = "'broken";
            Verify("raw malformed current replacement preserves source model file and selection", Refused(rawPanel.ReplaceCurrentAsync) && pane.LeftEditor.Text == "a;'quoted';b" && rawPanel.Comparison.Documents[0].SourceText == "a;'quoted';b" && File.ReadAllText(leftPath) == "a;'quoted';b" && RawAt(rawPanel, 2, 8));
            Verify("raw malformed all replacement preserves source", Refused(rawPanel.ReplaceAllAsync) && pane.LeftEditor.Text == "a;'quoted';b");
            Click(rawPanel, "セル編集を戻す"); Verify("raw refusal adds no history", pane.LeftEditor.Text == "a;'quoted';b");
            rawPanel = Load("raw-forbidden-newline", "'quoted'", allowNewlines: false); Select(rawPanel); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            rawPanel.SearchText.Text = "quoted"; rawPanel.ReplacementText.Text = "first\nlast";
            Verify("raw forbidden quoted newline is rejected before publishing", Refused(rawPanel.ReplaceAllAsync) && pane.LeftEditor.Text == "'quoted'" && rawPanel.Comparison.Documents[0].SourceText == "'quoted'");
            rawPanel = Load("raw-regex-timeout", new string('a', 100_000) + "!"); Select(rawPanel); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            Box(rawPanel, "正規表現").IsChecked = true; rawPanel.SearchText.Text = "(a+)+$"; rawPanel.ReplacementText.Text = "X";
            var timedOut = false; try { pump(rawPanel.ReplaceAllAsync()); } catch (RegexMatchTimeoutException) { timedOut = true; }
            Verify("raw regex timeout preserves source and comparison", timedOut && pane.LeftEditor.Text == new string('a', 100_000) + "!" && rawPanel.Comparison.Documents[0].SourceText == pane.LeftEditor.Text);
            rawPanel = Load("raw-crlf-unicode", "😀\r\nend"); Select(rawPanel); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            Box(rawPanel, "正規表現").IsChecked = true; rawPanel.SearchText.Text = "\\r|\\n|[\\uD800-\\uDFFF]"; rawPanel.ReplacementText.Text = "X";
            Click(rawPanel, "ペイン内を全置換"); Verify("raw replacement does not split surrogate or CRLF boundaries", pane.LeftEditor.Text == "😀\r\nend");
            rawPanel.SearchText.Text = "(?=)"; rawPanel.ReplacementText.Text = "|";
            Click(rawPanel, "ペイン内を全置換"); Verify("raw empty matches preserve Unicode and CRLF scalar boundaries", pane.LeftEditor.Text == "|😀|\r\n|e|n|d|");
            var cancellationSource = string.Concat(Enumerable.Repeat("a;a;a;a\n", 50_000));
            rawPanel = Load("raw-pending-cancellation", cancellationSource); Select(rawPanel); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            rawPanel.SearchText.Text = "a"; rawPanel.ReplacementText.Text = "b";
            Button(rawPanel, "ペイン内を全置換").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            var pending = rawPanel.PendingOperation ?? throw new InvalidOperationException("原文置換操作のTaskがありません。");
            Verify("raw cancellation exercises pending real button operation", !pending.IsCompleted);
            pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止")).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            var canceled = false; try { pump(pending); } catch (OperationCanceledException) { canceled = true; }
            Dispatcher.UIThread.RunJobs();
            Verify("raw cancellation preserves source model file and history", canceled && pane.LeftEditor.Text == cancellationSource && rawPanel.Comparison.Documents[0].SourceText == cancellationSource && File.ReadAllText(leftPath) == cancellationSource);
            Click(rawPanel, "セル編集を戻す"); Verify("raw canceled operation adds no undo item", pane.LeftEditor.Text == cancellationSource);
            rawPanel = Load("raw-stale-query", cancellationSource); Select(rawPanel); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            rawPanel.SearchText.Text = "a"; rawPanel.ReplacementText.Text = "b";
            var staleOperation = rawPanel.ReplaceAllAsync(); rawPanel.SearchText.Text = "changed";
            Verify("raw changed query rejects stale replacement plan", Refused(() => staleOperation) && pane.LeftEditor.Text == cancellationSource && rawPanel.Comparison.Documents[0].SourceText == cancellationSource);

            rawPanel = Load("raw-post-write-cancel", "a;a;a;a\n"); Select(rawPanel); Box(rawPanel, "CSV原文を検索").IsChecked = true;
            rawPanel.SearchText.Text = "a"; rawPanel.ReplacementText.Text = "b";
            var rawWritten = false;
            var rawStopAfterWrite = pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止"));
            void ObserveRawWrite(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs args)
            {
                if (!rawWritten && args.Property == TextBox.TextProperty && pane.LeftEditor.Text == "b;b;b;b\n")
                { rawWritten = true; rawStopAfterWrite.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); }
            }
            pane.LeftEditor.PropertyChanged += ObserveRawWrite;
            try
            {
                Verify("raw cancellation after write rolls back source model and file", Refused(rawPanel.ReplaceAllAsync) && rawWritten
                    && pane.LeftEditor.Text == "a;a;a;a\n" && rawPanel.Comparison.Documents[0].SourceText == "a;a;a;a\n" && File.ReadAllText(leftPath) == "a;a;a;a\n");
            }
            finally { pane.LeftEditor.PropertyChanged -= ObserveRawWrite; }
            Click(rawPanel, "セル編集を戻す"); Verify("raw rollback adds no undo item", pane.LeftEditor.Text == "a;a;a;a\n");
            screenshot("table-search-raw-preserved.png");
        }
        try
        {
            RawChecks();
            if (rawOnly) return;
            var panel = Load("offsets", "hit hit hit"); Select(panel);
            Verify("next visits first same-cell match", Find(panel, "hit") == 0);
            Verify("next visits second same-cell match", Find(panel, "hit") == 4);
            Verify("next visits third same-cell match", Find(panel, "hit") == 8);
            Verify("previous returns second same-cell match", Find(panel, "hit", -1) == 4);
            Verify("previous returns first same-cell match", Find(panel, "hit", -1) == 0);
            Verify("previous wraps to last same-cell match", Find(panel, "hit", -1) == 8);
            Box(panel, "折返し").IsChecked = false;
            Verify("no-wrap preserves last highlight", Find(panel, "hit") == 8 && panel.CellEditor.SelectionEnd == 11);
            Verify("changed query resets decoded offset", Find(panel, "it") == 1 && panel.CellEditor.SelectionEnd == 3);
            screenshot("table-search-offsets.png");

            panel = Load("current", "hit hit hit"); Select(panel); Find(panel, "hit"); panel.ReplacementText.Text = "X";
            Click(panel, "この一致を置換");
            Verify("current replacement advances to next match", pane.LeftEditor.Text == "X hit hit" && panel.CellEditor.SelectionStart == 2
                && panel.CellEditor.SelectionEnd == 5 && pane.RightEditor.Text == "hit hit hit");

            const string raw = "id;value;untouched\r\n1;'hit hit hit';'raw''quote'\r\n2;'first\r\nsecond';end";
            var bigEndian = new UnicodeEncoding(true, true);
            panel = Load("replace-all-lossless", raw, encoding: bigEndian); Select(panel, row: 1, column: 1);
            var originalAttributes = File.GetAttributes(leftPath); var rightBytes = File.ReadAllBytes(rightPath);
            Box(panel, "全ペイン").IsChecked = true; panel.SearchText.Text = "hit"; panel.ReplacementText.Text = "hit!";
            Click(panel, "ペイン内を全置換");
            var replaced = raw.Replace("'hit hit hit'", "'hit! hit! hit!'", StringComparison.Ordinal);
            Verify("all replacement uses original snapshot and one active pane", pane.LeftEditor.Text == replaced && pane.RightEditor.Text == raw
                && panel.Comparison.Documents[0].SourceText == replaced);
            Click(panel, "セル編集を戻す"); Verify("one undo restores all replacement raw intervals", pane.LeftEditor.Text == raw);
            Click(panel, "セル編集を戻す"); Verify("all replacement adds one undo item", pane.LeftEditor.Text == raw);
            Click(panel, "セル編集をやり直す"); Verify("one redo restores all replacement", pane.LeftEditor.Text == replaced);
            pump(pane.SaveAsync(false));
            Verify("save preserves UTF16BE BOM untouched quoting CRLF and attributes", File.ReadAllBytes(leftPath).SequenceEqual(bigEndian.GetPreamble().Concat(bigEndian.GetBytes(replaced)))
                && File.GetAttributes(leftPath) == originalAttributes && File.ReadAllBytes(rightPath).SequenceEqual(rightBytes));
            pump(pane.CompareProjectAsync()); Dispatcher.UIThread.RunJobs(); panel = pane.GetVisualDescendants().OfType<TablePanel>().Single();
            Verify("saved all replacement reloads exact original source", pane.LeftEditor.Text == replaced && panel.Comparison.GetCell(0, 1, 1)?.Value == "hit! hit! hit!");
            screenshot("table-search-replaced.png");

            panel = Load("quoting", "id;value;raw\r\n1;'hit';'untouched''quote'\r\n"); Select(panel, row: 1, column: 1);
            Find(panel, "hit"); panel.ReplacementText.Text = "new;value 'quote'\r\nnext"; Click(panel, "この一致を置換");
            Verify("replacement requotes one decoded cell and preserves outside raw intervals", pane.LeftEditor.Text == "id;value;raw\r\n1;'new;value ''quote''\r\nnext';'untouched''quote'\r\n");

            panel = Load("captures", "a1 b2"); Select(panel); Box(panel, "正規表現").IsChecked = true;
            panel.SearchText.Text = "(?<letter>[ab])(?<digit>[12])"; panel.ReplacementText.Text = "${digit}-$1-$&-$$";
            Click(panel, "ペイン内を全置換"); Verify("regex named numbered whole-match and dollar captures", pane.LeftEditor.Text == "1-a-a1-$ 2-b-b2-$");
            panel = Load("capture-prefix-suffix", "abc"); Select(panel); Box(panel, "正規表現").IsChecked = true;
            panel.SearchText.Text = "b"; panel.ReplacementText.Text = "$`|$'|$$";
            Click(panel, "ペイン内を全置換"); Verify("regex prefix suffix and escaped dollar use original decoded cell", pane.LeftEditor.Text == "aa|c|$c");
            panel = Load("literal-dollar", "hit hit"); Select(panel); panel.SearchText.Text = "hit"; panel.ReplacementText.Text = "$1$$";
            Click(panel, "ペイン内を全置換"); Verify("literal search keeps replacement dollar syntax literal", pane.LeftEditor.Text == "$1$$ $1$$");

            panel = Load("fixed-range", "hit hit hit"); Select(panel); panel.CellEditor.SelectionStart = 4; panel.CellEditor.SelectionEnd = 11;
            Click(panel, "検索範囲を固定"); Verify("pin activates selected-range checkbox", Box(panel, "選択範囲だけ").IsChecked == true);
            panel.SearchText.Text = "hit"; Click(panel, "次の一致"); Click(panel, "次の一致");
            panel.ReplacementText.Text = "X"; Click(panel, "ペイン内を全置換");
            Verify("fixed range survives highlights and excludes outside matches", pane.LeftEditor.Text == "hit X X");
            panel = Load("fully-contained", "hit hit hit"); Select(panel); panel.CellEditor.SelectionStart = 1; panel.CellEditor.SelectionEnd = 10;
            Click(panel, "検索範囲を固定"); panel.SearchText.Text = "hit"; panel.ReplacementText.Text = "X"; Click(panel, "ペイン内を全置換");
            Verify("range requires whole-match containment", pane.LeftEditor.Text == "hit X hit");
            Click(panel, "検索範囲を解除"); Verify("clear scope disables selected-range checkbox", Box(panel, "選択範囲だけ").IsChecked != true);
            panel = Load("unfixed-range", "hit"); Select(panel); Box(panel, "選択範囲だけ").IsChecked = true; panel.SearchText.Text = "hit"; panel.ReplacementText.Text = "X";
            Verify("selected-range without pin refuses source changes", Refused(panel.ReplaceAllAsync) && pane.LeftEditor.Text == "hit");
            screenshot("table-search-scope.png");

            panel = Load("readonly", "hit", leftReadOnly: true); Select(panel); Find(panel, "hit"); panel.ReplacementText.Text = "X";
            Verify("readonly current replacement refused", Refused(panel.ReplaceCurrentAsync) && pane.LeftEditor.Text == "hit");
            Verify("readonly all replacement refused", Refused(panel.ReplaceAllAsync) && pane.LeftEditor.Text == "hit");
            panel = Load("ancestor", "hit", ancestor: "hit"); Select(panel, side: 1); Find(panel, "hit"); panel.ReplacementText.Text = "X";
            Verify("ancestor current and all replacement refused", Refused(panel.ReplaceCurrentAsync) && Refused(panel.ReplaceAllAsync)
                && pane.LeftEditor.Text == "hit" && pane.RightEditor.Text == "hit" && File.ReadAllText(basePath) == "hit");
            panel = Load("ghost", "head\nleft-only\ntail\n", "head\ntail\n");
            var ghost = Enumerable.Range(0, panel.Comparison.Rows.Count).First(row => panel.Comparison.GetSourceRow(1, row) is null);
            Select(panel, side: 1, row: ghost); panel.SearchText.Text = "left-only"; panel.ReplacementText.Text = "X";
            Verify("ghost current replacement refused", Refused(panel.ReplaceCurrentAsync) && pane.RightEditor.Text == "head\ntail\n");
            panel = Load("stale", "hit"); Select(panel); Find(panel, "hit"); panel.ReplacementText.Text = "X"; pane.LeftEditor.Text = "external hit";
            Verify("stale current replacement protects edited source", Refused(panel.ReplaceCurrentAsync) && pane.LeftEditor.Text == "external hit");
            Verify("stale all replacement protects edited source", Refused(panel.ReplaceAllAsync) && pane.LeftEditor.Text == "external hit");
            panel = Load("uncommitted", "hit"); Select(panel); Find(panel, "hit"); panel.CellEditor.Text = "pending edit"; panel.ReplacementText.Text = "X";
            Verify("uncommitted decoded edit is preserved on replacement refusal", Refused(panel.ReplaceCurrentAsync) && Refused(panel.ReplaceAllAsync)
                && panel.CellEditor.Text == "pending edit" && pane.LeftEditor.Text == "hit");

            Verify("uncommitted cell counts as unsaved", pane.HasUnsavedChanges);
            var draftBytes = File.ReadAllBytes(leftPath);
            Verify("save refuses pending decoded cell and retains file", Refused(() => pane.SaveAsync(false)) && File.ReadAllBytes(leftPath).SequenceEqual(draftBytes));
            var draftReport = Path.Combine(directory, "pending-report.html"); File.WriteAllText(draftReport, "existing-report", utf8);
            Verify("report refuses pending decoded cell and retains output", Refused(() => pane.SaveReportAsync(draftReport)) && File.ReadAllText(draftReport) == "existing-report");
            var draftPackage = Path.Combine(directory, "pending-package.zip"); File.WriteAllText(draftPackage, "existing-package", utf8);
            Verify("packaging refuses pending decoded cell and retains output", Refused(() => window.PackageWorkspaceAsync(draftPackage, [window.SessionPanes.ToList().IndexOf(pane)])) && File.ReadAllText(draftPackage) == "existing-package");
            Click(panel, "未反映セルを破棄"); Verify("explicit discard clears pending decoded edit", !pane.HasUnsavedChanges && panel.CellEditor.Text == "hit");

            panel = Load("pane-cursor", "aaaaaaX", "X X"); Select(panel); Box(panel, "折返し").IsChecked = false; Find(panel, "X");
        panel.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Name == "table-side").SelectedIndex = 1;
            Verify("changing search pane does not carry old decoded offset", Find(panel, "X") == 0 && panel.CellEditor.Text == "X X");
            panel = Load("moved-highlight", "hit hit"); Select(panel); Find(panel, "hit"); panel.ReplacementText.Text = "X";
            panel.CellEditor.SelectionStart = 4; panel.CellEditor.SelectionEnd = 7;
            Verify("manual highlight invalidates current replacement target", Refused(panel.ReplaceCurrentAsync) && pane.LeftEditor.Text == "hit hit");
            panel = Load("inflight-target", "hit;other"); Select(panel); Find(panel, "hit"); panel.ReplacementText.Text = new string('x', 1_048_576);
            var inflight = panel.ReplaceCurrentAsync(); Verify("current replacement is pending before target change", !inflight.IsCompleted);
            Select(panel, column: 1);
            Verify("target change during planning rejects old current match", Refused(() => inflight) && pane.LeftEditor.Text == "hit;other" && panel.CellEditor.Text == "other");

            panel = Load("post-write-cancel", "a;a;a;a\n"); Select(panel); panel.SearchText.Text = "a"; panel.ReplacementText.Text = "b";
            var written = false;
            var stopAfterWrite = pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止"));
            void ObserveWrite(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs args)
            {
                if (!written && args.Property == TextBox.TextProperty && pane.LeftEditor.Text == "b;b;b;b\n")
                { written = true; stopAfterWrite.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); }
            }
            pane.LeftEditor.PropertyChanged += ObserveWrite;
            try
            {
                Verify("cancel between raw write and comparison rolls back original", Refused(panel.ReplaceAllAsync) && written && pane.LeftEditor.Text == "a;a;a;a\n"
                    && panel.Comparison.Documents[0].SourceText == "a;a;a;a\n" && File.ReadAllText(leftPath) == "a;a;a;a\n");
            }
            finally { pane.LeftEditor.PropertyChanged -= ObserveWrite; }
            Click(panel, "セル編集を戻す"); Verify("rolled-back replacement adds no undo item", pane.LeftEditor.Text == "a;a;a;a\n");

            panel = Load("readonly-post-write-cancel", "a;a\n"); Select(panel); panel.SearchText.Text = "a"; panel.ReplacementText.Text = "b";
            var readOnlyWritten = false;
            var readOnlyStop = pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止"));
            void ObserveReadOnlyWrite(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs args)
            {
                if (!readOnlyWritten && args.Property == TextBox.TextProperty && pane.LeftEditor.Text == "b;b\n")
                {
                    readOnlyWritten = true; pane.LeftEditor.IsReadOnly = true;
                    readOnlyStop.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                }
            }
            pane.LeftEditor.PropertyChanged += ObserveReadOnlyWrite;
            try
            {
                Verify("readonly change after owned write still permits cancellation rollback", Refused(panel.ReplaceAllAsync) && readOnlyWritten
                    && pane.LeftEditor.Text == "a;a\n" && panel.Comparison.Documents[0].SourceText == "a;a\n" && pane.LeftEditor.IsReadOnly && panel.CellEditor.IsReadOnly
                    && File.ReadAllText(leftPath) == "a;a\n");
            }
            finally { pane.LeftEditor.PropertyChanged -= ObserveReadOnlyWrite; }

            panel = Load("zero-length", "'A😀\r\nB'"); Select(panel); Box(panel, "正規表現").IsChecked = true; Box(panel, "折返し").IsChecked = false;
            var positions = new List<int>();
            for (var step = 0; step < 8; step++) positions.Add(Find(panel, "(?=)"));
            Verify("zero-length search progresses without surrogate or CRLF internal offsets", positions.Take(6).SequenceEqual(new[] { 0, 1, 3, 5, 6, 6 })
                && positions.All(position => position is not (2 or 4)), string.Join(',', positions));
            var backward = new List<int>();
            for (var step = 0; step < 5; step++) backward.Add(Find(panel, "(?=)", -1));
            Verify("zero-length previous search progresses through safe decoded boundaries", backward.SequenceEqual(new[] { 5, 3, 1, 0, 0 }), string.Join(',', backward));
            panel = Load("zero-replace", "ab"); Select(panel); Box(panel, "正規表現").IsChecked = true; panel.SearchText.Text = "(?=)"; panel.ReplacementText.Text = "|";
            Click(panel, "ペイン内を全置換"); Verify("zero-length replacement uses snapshot boundaries once", pane.LeftEditor.Text == "|a|b|");

            panel = Load("invalid-regex", "hit"); Select(panel); Box(panel, "正規表現").IsChecked = true; panel.SearchText.Text = "["; panel.ReplacementText.Text = "X";
            Verify("invalid regex preserves source", Refused(panel.ReplaceAllAsync) && pane.LeftEditor.Text == "hit");
            var timeoutRaw = new string('a', 100_000) + "!";
            panel = Load("regex-timeout", timeoutRaw); Select(panel); Box(panel, "正規表現").IsChecked = true;
            panel.SearchText.Text = "(a+)+$"; panel.ReplacementText.Text = "X";
            var timedOut = false; try { pump(panel.ReplaceAllAsync()); } catch (RegexMatchTimeoutException) { timedOut = true; }
            Verify("regex timeout refuses planned replacement without source changes", timedOut && pane.LeftEditor.Text == timeoutRaw && File.ReadAllText(leftPath) == timeoutRaw);
            panel = Load("newline-forbidden", "hit", allowNewlines: false); Select(panel); panel.SearchText.Text = "hit"; panel.ReplacementText.Text = "a\nb";
            Verify("forbidden replacement newline preserves source", Refused(panel.ReplaceAllAsync) && pane.LeftEditor.Text == "hit");
            panel = Load("output-expansion", new string('a', 16384)); Select(panel); Box(panel, "正規表現").IsChecked = true;
            panel.SearchText.Text = "a+"; panel.ReplacementText.Text = string.Concat(Enumerable.Repeat("$&", 4097));
            var expansionBefore = pane.LeftEditor.Text;
            Verify("capture expansion capacity refusal preserves source", Refused(panel.ReplaceAllAsync) && pane.LeftEditor.Text == expansionBefore);

            var cancellationRaw = string.Concat(Enumerable.Repeat("a;a;a;a\n", 50_000));
            panel = Load("pending-cancellation", cancellationRaw); Select(panel); panel.SearchText.Text = "a"; panel.ReplacementText.Text = "b";
            var cancellationBytes = File.ReadAllBytes(leftPath);
            var stop = pane.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "中止"));
            Button(panel, "ペイン内を全置換").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            var pending = panel.PendingOperation ?? throw new InvalidOperationException("置換操作のTaskがありません。");
            Verify("real replace-all cancellation observes pending operation", !pending.IsCompleted);
            stop.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            var canceled = false; try { pump(pending); } catch (OperationCanceledException) { canceled = true; }
            Dispatcher.UIThread.RunJobs();
            Verify("pending cancellation preserves editor file and shared model", canceled && pane.LeftEditor.Text == cancellationRaw
                && panel.Comparison.Documents[0].SourceText == cancellationRaw && File.ReadAllBytes(leftPath).SequenceEqual(cancellationBytes));
            Click(panel, "セル編集を戻す"); Verify("canceled replacement adds no undo item", pane.LeftEditor.Text == cancellationRaw);
            screenshot("table-search-canceled.png");
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "final-left-editor.csv"), pane.LeftEditor.Text ?? "", utf8);
            File.WriteAllText(Path.Combine(directory, "final-right-editor.csv"), pane.RightEditor.Text ?? "", utf8);
            using var stream = File.Create(Path.Combine(directory, "results.json"));
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject(); writer.WriteString("leftFixture", leftPath); writer.WriteString("rightFixture", rightPath); writer.WriteString("baseFixture", basePath);
            writer.WriteStartArray("assertions");
            foreach (var record in records)
            {
                writer.WriteStartObject(); writer.WriteString("name", record.Name); writer.WriteBoolean("passed", record.Passed); writer.WriteString("detail", record.Detail);
                writer.WriteString("leftSource", record.Left); writer.WriteString("rightSource", record.Right); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject(); pane.DiscardChanges();
        }
    }
}

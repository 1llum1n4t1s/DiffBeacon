using System.Text;
using System.Text.Json;
using DiffBeacon.Core;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static class CommandLine
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args[0] is "--help" or "--version")
            {
                Console.WriteLine("DiffBeacon · .NET 10 / Avalonia\n"
                    + "GUI: DiffBeacon LEFT [BASE] RIGHT\n"
                    + "--compare LEFT RIGHT [--ignore-case] [--ignore-space] [--ignore-blank] [--ignore-regex PATTERN] [--ignore-numbers] [--comments cstyle|csharp|python|xml|none] [--whitespace none|trim|changes|all] [--substitute PATTERN REPLACEMENT] [--max-work N]\n"
                    + "独立三者Text: --compare LEFT RIGHT --middle MIDDLE --independent-text [--pair left-middle|middle-right|left-right] [comparison options]\n"
                    + "--word-diff LEFT RIGHT [--word-level] [--ignore-case] [--ignore-numbers] [--whitespace none|changes|all] [--eol strict|ignore|space] [--no-separators] [--separators TEXT] [--max-work N]\n"
                    + "開発用: --gnu-line-script INPUT_JSON [--max-work N]\n"
                    + "開発用: --image-line-script INPUT_JSON [--max-work N]\n"
                    + "開発用: --image-overlay-script INPUT_JSON [--max-work 0..256000000] [--cancel-after-clock 1..16]（mode0..3、個別epoch clock）\n"
                    + "開発用: --image-align LEFT [MIDDLE] RIGHT [--horizontal] [--threshold X] [--block-size N]\n"
                    + "開発用: --image-regions LEFT [MIDDLE] RIGHT [--block-size N] [--threshold X] [--left-frame N [--middle-frame N] --right-frame N] [--highlight-alpha X] [--selected-region N]\n"
                    + "開発用: --image-copy LEFT [MIDDLE] RIGHT --script SCRIPT_JSON [--hashes-only]\n"
                    + "画像の行・列整列: --image / --report-project に --insertion-deletion-mode none|vertical|horizontal\n"
                    + "画像ワイプ: --image / --image-regions / --report-project に --wipe-mode vertical|horizontal と --wipe-position N（vertical=Y、horizontal=X、非負整数）\n"
                    + "画像overlay: --image / --image-regions / --report-project に --overlay-mode none|xor|alpha|anim、--overlay-alpha X（0～1）、--overlay-blink true|false、--overlay-period N / --blink-period N（200～8000ms）\n"
                    + "画像の差分色: --image / --report-project に --highlight-alpha X（0～1、既定0.7）\n"
                    + "--directory / --folder LEFT RIGHT [--middle MIDDLE] [folder options]\n--binary LEFT RIGHT\n--image LEFT [MIDDLE] RIGHT [--left-frame N [--middle-frame N] --right-frame N] [--threshold X] [--block-size N] [--left-orientation ANGLE,HORIZONTAL,VERTICAL] [--middle-orientation ANGLE,HORIZONTAL,VERTICAL] [--right-orientation ANGLE,HORIZONTAL,VERTICAL] [--left-offset X,Y [--middle-offset X,Y] --right-offset X,Y]\n"
                    + "--provider ID LEFT RIGHT\n--external-provider EXE LEFT RIGHT FORMAT\n"
                    + "--json LEFT RIGHT\n--table LEFT RIGHT [--base BASE] [--word-level] [--eol strict|ignore] [comparison options]\n--report LEFT RIGHT OUTPUT_HTML\n--report-project INPUT_PROJECT OUTPUT_HTML [--left-offset X,Y [--middle-offset X,Y] --right-offset X,Y] [--entry N] [--left-frame N [--middle-frame N] --right-frame N] [--threshold X]\n"
                    + "--project-copy INPUT_PROJECT OUTPUT_PROJECT\n--package-project INPUT_PROJECT OUTPUT_ARCHIVE [--entries 1,3] [--report] [--patch] [--no-documents] [--no-project]\n--folder-copy SOURCE_ROOT DEST_ROOT RELATIVE\n"
                    + "--folder-plan LEFT RIGHT [--middle MIDDLE] --direction DIRECTION --copy all|diff --select RELATIVE [--select RELATIVE ...] [folder options]\n"
                    + "--folder-sync LEFT RIGHT [--middle MIDDLE] --direction DIRECTION --copy all|diff --select RELATIVE [--select RELATIVE ...] [folder options]\n"
                    + "folder options: --middle MIDDLE --mode content|hash|timestamp --exclude PATTERN --filter FILE --no-recursive --show-filtered --max-entries N --max-depth N --max-content-bytes N と text comparison options\n"
                    + "three-way folder directions: left-to-middle, middle-to-left, middle-to-right, right-to-middle（left-to-right/right-to-leftも使用可能）\n"
                    + "--archive-list ARCHIVE [--password-stdin]\n--archive-compare LEFT RIGHT [--password-stdin]\n--archive-entry ARCHIVE ENTRY OUTPUT [--password-stdin]\n--archive-repack INPUT OUTPUT [--password-stdin]\n--archive-extract INPUT NEW_DIRECTORY [--password-stdin]\n--archive-create SOURCE_FILE_OR_DIRECTORY OUTPUT\n"
                    + "archive create/repack gzip output: --output-gzip-name-code-page N\narchive read options: --gzip-name-code-page N --gzip-payload-kind auto|file|tar --compression-payload-kind auto|file|tar\narchive compare right overrides: --right-gzip-name-code-page N --right-gzip-payload-kind auto|file|tar --right-compression-payload-kind auto|file|tar\n"
                    + "--merge BASE LEFT RIGHT OUTPUT\n--merge-select BASE LEFT RIGHT OUTPUT LEFT|BASE|RIGHT\n--patch-create LEFT RIGHT OUTPUT\n--patch-apply SOURCE PATCH OUTPUT\n"
                    + "--archive-source-list DESCRIPTOR_JSON [--password-stdin]\n--archive-source-entry DESCRIPTOR_JSON ENTRY OUTPUT [--password-stdin]\n"
                    + "--self-test OUTPUT_DIRECTORY\nExit: 0 equal/success, 1 differences/conflicts, 2 error");
                return 0;
            }
            var command = args[0];
            if (command == "--word-diff") return await WordDiffCommands.RunAsync(args);
            if (command == "--gnu-line-script") return await GnuLineCommands.RunAsync(args);
            if (command == "--image-wipe-script") return await ImageWipeCommands.RunAsync(args);
            if (command == "--image-overlay-script") return await ImageOverlayCommands.RunAsync(args);
            if (command == "--image-line-script") return await ImageLineCommands.RunAsync(args);
            if (command == "--image-align") return await ImageAlignmentCommands.RunAsync(args);
            if (command == "--image-regions") return await ImageRegionCommands.RunAsync(args);
            if (command == "--image-copy") return await ImageCopyCommands.RunAsync(args);
            if (command == "--image") return await ImageCommands.RunAsync(args);
            if (command == "--package-project") return await PackageCommands.RunAsync(args);
            if (command == "--report-project") return await ReportCommands.RunAsync(args);
            if (command is "--archive-source-list" or "--archive-source-entry") return await ArchiveSourceCommands.RunAsync(args);
            if (command.StartsWith("--archive-", StringComparison.Ordinal)) return await ArchiveCommands.RunAsync(args);
            var required = command switch { "--merge" => 5, "--merge-select" => 6, "--patch-create" or "--patch-apply" => 4, _ => 3 };
            if (args.Length < required) throw new ArgumentException("引数が不足しています。--help を参照してください。");
            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            var token = cancel.Token;
            if (command is "--provider" or "--external-provider")
            {
                var external = command == "--external-provider";
                if (args.Length != (external ? 5 : 4)) throw new ArgumentException("プロバイダーID、左、右（外部プロバイダーでは形式）が必要です。");
                var provider = external ? (IComparisonProvider)new ExecutableComparisonProvider("external", Path.GetFullPath(args[1]), [args[4]]) : BuiltinComparisonProviders.CreateDefault().Get(args[1]);
                var result = await provider.CompareAsync(new ComparisonRequest(args[2], args[3], external ? args[4] : provider.Id), token);
                var diff = TextDiffer.Compare(result.LeftText, result.RightText);
                WriteJson(w => { w.WriteBoolean("different", diff.HasDifferences); w.WriteString("summary", result.Summary); w.WriteNumber("blocks", diff.Blocks.Count); });
                return diff.HasDifferences ? 1 : 0;
            }
            if (command == "--compare")
            {
                var comparisonArguments = new List<string>();
                string? middlePath = null, selectedPair = null;
                var independent = false;
                for (var index = 3; index < args.Length; index++)
                {
                    if (args[index] == "--independent-text")
                    {
                        if (independent) throw new ArgumentException("独立三者Textの指定が重複しています。");
                        independent = true;
                    }
                    else if (args[index] is "--middle" or "--pair")
                    {
                        var option = args[index];
                        if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                            throw new ArgumentException("中央入力または比較ペアの値がありません。");
                        if (option == "--middle")
                        {
                            if (middlePath is not null) throw new ArgumentException("中央入力の指定が重複しています。");
                            middlePath = args[index];
                        }
                        else
                        {
                            if (selectedPair is not null) throw new ArgumentException("比較ペアの指定が重複しています。");
                            selectedPair = args[index] switch
                            {
                                "left-middle" => "LeftMiddle", "middle-right" => "MiddleRight", "left-right" => "LeftRight",
                                _ => throw new ArgumentException("比較ペアはleft-middle、middle-right、left-rightです。")
                            };
                        }
                    }
                    else
                    {
                        // 値を含む既存比較オプションは従来parserへ一組のまま渡す。
                        var option = args[index]; comparisonArguments.Add(option);
                        var count = option == "--substitute" ? 2 : option is "--eol" or "--comments" or "--whitespace"
                            or "--ignore-regex" or "--max-work" ? 1 : 0;
                        while (count-- > 0 && index + 1 < args.Length) comparisonArguments.Add(args[++index]);
                    }
                }
                if (independent && string.IsNullOrWhiteSpace(middlePath) || !independent && (middlePath is not null || selectedPair is not null))
                    throw new ArgumentException("--middle と --pair は --independent-text と三つの物理Text入力を指定してください。");
                var options = ParseOptions(comparisonArguments.ToArray());
                var left = await TextDocument.LoadAsync(independent ? ArchiveActions.ValidatePath(args[1]) : args[1], token);
                var right = await TextDocument.LoadAsync(independent ? ArchiveActions.ValidatePath(args[2]) : args[2], token);
                var middle = independent ? await TextDocument.LoadAsync(ArchiveActions.ValidatePath(middlePath!), token) : null;
                TextDocument[] documents = middle is null ? [left, right] : [left, middle, right];
                var pair = selectedPair ?? "LeftMiddle";
                var (first, second) = independent ? IndependentTextReport.PairSides(pair) : (0, 1);
                var result = TextDiffer.Compare(documents[first].Text, documents[second].Text, options, token);
                Action<Utf8JsonWriter> writeComparison = writer =>
                {
                    if (independent)
                    {
                        writer.WriteString("mode", "Text"); writer.WriteString("textSemantics", "Independent"); writer.WriteString("comparisonPair", pair);
                        writer.WriteString("semantics", "Independent"); writer.WriteString("pair", pair);
                        writer.WriteString("leftPath", Path.GetFullPath(args[1])); writer.WriteString("middlePath", Path.GetFullPath(middlePath!));
                        writer.WriteString("rightPath", Path.GetFullPath(args[2]));
                        token.ThrowIfCancellationRequested();
                        writer.WriteString("firstText", documents[first].Text); writer.Flush();
                        token.ThrowIfCancellationRequested();
                        writer.WriteString("secondText", documents[second].Text); writer.Flush();
                        writer.WriteString("firstRole", IndependentTextReport.Role(first)); writer.WriteString("secondRole", IndependentTextReport.Role(second));
                        writer.Flush();
                        writer.WriteStartArray("inputs");
                        var paths = new[] { args[1], middlePath!, args[2] };
                        for (var side = 0; side < 3; side++)
                        {
                            writer.WriteStartObject(); writer.WriteString("role", IndependentTextReport.Role(side));
                            writer.WriteString("kind", "Physical"); writer.WriteString("path", Path.GetFullPath(paths[side]));
                            writer.WriteString("description", paths[side]); writer.WriteBoolean("exists", true);
                            writer.WriteString("encoding", documents[side].EncodingName); writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteBoolean("different", result.HasDifferences);
                    writer.WriteNumber("blocks", result.Blocks.Count);
                    writer.WriteNumber("inlineWorkUsed", result.InlineWorkUsed);
                    writer.WriteNumber("inlineFallbackCount", result.InlineFallbackCount);
                    writer.WriteNumber("lineWorkUsed", result.LineWorkUsed);
                    writer.WriteBoolean("lineFallback", result.LineFallback);
                    writer.WriteString("lineFallbackReason", result.LineFallbackReason);
                    writer.WriteString("leftEncoding", documents[first].EncodingName);
                    writer.WriteString("rightEncoding", documents[second].EncodingName);
                    writer.WriteStartArray("rows");
                    foreach (var row in result.Rows)
                    {
                        if (independent) token.ThrowIfCancellationRequested();
                        writer.WriteStartObject(); writer.WriteString("kind", row.Kind.ToString());
                        writer.WriteString("left", row.LeftText); writer.WriteString("right", row.RightText);
                        if (row.LeftLineNumber is int ln) writer.WriteNumber("leftLine", ln);
                        if (row.RightLineNumber is int rn) writer.WriteNumber("rightLine", rn);
                        void Spans(string name, IReadOnlyList<InlineSpan> spans)
                        {
                            writer.WriteStartArray(name);
                            foreach (var span in spans)
                            {
                                writer.WriteStartArray(); writer.WriteNumberValue(span.Start); writer.WriteNumberValue(span.Length);
                                writer.WriteEndArray();
                            }
                            writer.WriteEndArray();
                        }
                        Spans("leftSpans", row.LeftSpans); Spans("rightSpans", row.RightSpans);
                        writer.WriteEndObject();
                        if (independent) writer.Flush();
                    }
                    writer.WriteEndArray();
                };
                if (independent) WriteIndependentJson(writeComparison, token);
                else WriteJson(writeComparison);
                return result.HasDifferences ? 1 : 0;
            }
            if (command == "--project-copy")
            {
                var workspace = await WorkspaceStore.LoadWorkspaceAsync(args[1], token);
                if (workspace.Entries.Length == 1 && workspace.FormatVersion is not (8 or 9)) await WorkspaceStore.SaveAsync(args[2], workspace.Entries[0], token, args[1]);
                else await WorkspaceStore.SaveWorkspaceAsync(args[2], workspace, token, args[1]);
                WriteJson(w => { w.WriteString("output", Path.GetFullPath(args[2])); w.WriteNumber("entries", workspace.Entries.Length); w.WriteNumber("activeEntryIndex", workspace.ActiveEntryIndex); }); return 0;
            }
            if (command == "--report")
            {
                if (args.Length != 4) throw new ArgumentException("--report LEFT RIGHT OUTPUT_HTML");
                await ProjectReport.ExportAsync(new ComparisonWorkspace
                { Entries = [new ComparisonProject { LeftPath = args[1], RightPath = args[2], Mode = "Text" }] }, 0, args[3], token: token);
                WriteJson(w => w.WriteString("output", Path.GetFullPath(args[3]))); return 0;
            }
            if (command == "--folder-copy")
            {
                if (args.Length != 4) throw new ArgumentException("--folder-copy SOURCE_ROOT DEST_ROOT RELATIVE");
                await FolderOperations.CopyAsync(args[1], args[2], args[3], token);
                WriteJson(w => w.WriteString("copied", args[3])); return 0;
            }
            if (command == "--folder-plan")
            {
                var selection = await FolderCommands.PrepareSelectionAsync(args, token);
                WriteJson(w => FolderCommands.WriteSelection(w, selection.Selection)); return 0;
            }
            if (command == "--folder-sync")
            {
                var selection = await FolderCommands.PrepareSelectionAsync(args, token);
                selection.RequireInputsUnchanged();
                var plan = await FolderOperations.PrepareAsync(selection.Selection, cancellationToken: token);
                selection.RequireInputsUnchanged();
                selection.ValidateOutputs(plan);
                var result = await FolderOperations.ExecuteAsync(plan, selection.ValidateDestination, cancellationToken: token);
                WriteJson(w => FolderCommands.WriteExecution(w, plan, result));
                return result.Succeeded ? 0 : 2;
            }
            if (command is "--json" or "--table")
            {
                var left = await TextDocument.LoadAsync(args[1], token); var right = await TextDocument.LoadAsync(args[2], token);
                if (command == "--json")
                {
                    var different = StructuredComparer.NormalizeJson(left.Text) != StructuredComparer.NormalizeJson(right.Text);
                    WriteJson(w => w.WriteBoolean("different", different)); return different ? 1 : 0;
                }
                var delimiter = Path.GetExtension(args[1]).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
                string? basePath = null;
                var tableFlags = new List<string>();
                for (var index = 3; index < args.Length; index++)
                {
                    if (args[index] == "--base")
                    {
                        if (basePath is not null || ++index >= args.Length) throw new ArgumentException("--baseには一つの基準ファイルが必要です。");
                        basePath = args[index];
                    }
                    else
                    {
                        var values = args[index] switch
                        {
                            "--substitute" => 2,
                            "--comments" or "--whitespace" or "--ignore-regex" or "--max-work" or "--eol" => 1,
                            _ => 0
                        };
                        tableFlags.Add(args[index]);
                        while (values-- > 0)
                        {
                            if (++index >= args.Length) throw new ArgumentException("比較オプションの値が不足しています。");
                            tableFlags.Add(args[index]);
                        }
                    }
                }
                var tableOptions = ParseOptions(tableFlags.ToArray());
                var ancestor = basePath is null ? null : await TextDocument.LoadAsync(basePath, token);
                var table = StructuredComparer.CompareTables(ancestor is null ? [left.Text, right.Text] : [left.Text, ancestor.Text, right.Text],
                    new(delimiter), tableOptions, token);
                WriteJson(w =>
                {
                    w.WriteBoolean("different", table.HasDifferences); w.WriteNumber("rows", table.Documents.Max(document => document.Rows.Count));
                    w.WriteNumber("cols", table.ColumnCount); w.WriteNumber("alignedRows", table.Rows.Count); w.WriteBoolean("alignmentFallback", table.AlignmentFallback);
                    w.WriteNumber("alignmentWorkUsed", table.AlignmentWorkUsed); w.WriteString("alignmentFallbackReason", table.AlignmentFallbackReason);
                    w.WriteStartArray("mapping");
                    foreach (var row in table.Rows)
                    {
                        w.WriteStartObject();
                        if (row.LeftRow is int l) w.WriteNumber("left", l); else w.WriteNull("left");
                        if (ancestor is not null) { if (row.BaseRow is int b) w.WriteNumber("base", b); else w.WriteNull("base"); }
                        if (row.RightRow is int r) w.WriteNumber("right", r); else w.WriteNull("right");
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                });
                return table.HasDifferences ? 1 : 0;
            }
            if (command is "--merge" or "--merge-select")
            {
                if (args.Length != (command == "--merge-select" ? 6 : 5)) throw new ArgumentException("--merge BASE LEFT RIGHT OUTPUT または --merge-select BASE LEFT RIGHT OUTPUT LEFT|BASE|RIGHT");
                var ancestor = await TextDocument.LoadAsync(args[1], token);
                var left = await TextDocument.LoadAsync(args[2], token);
                var right = await TextDocument.LoadAsync(args[3], token);
                var materialized = FourPaneMaterialization.CreateThreeWay(ancestor.Text, left.Text, right.Text,
                    options: null, autoResolve: true, settings: new FourPaneMaterializationSettings { DefaultEol = ancestor.NewLine }, token: token);
                var session = materialized.InitialSession;
                if (command == "--merge-select")
                {
                    if (!Enum.TryParse<MergeSource>(args[5], true, out var source) || !Enum.IsDefined(source)) throw new ArgumentException("採用元はLEFT、BASE、RIGHTです。");
                    foreach (var descriptor in materialized.Choices.Descriptors)
                    {
                        if (!session.Current.Segments.Single(s => s.Id == descriptor.SegmentId).IsPlaceholder) continue;
                        var candidate = session.PrepareChoice(materialized.Choices,
                            new(session.Current.Version, descriptor.OriginalDiffIndex, [(int)source]), token, out _);
                        try { candidate.End(candidate.Buffer.Text, candidate.Buffer.Cursor, candidate.Buffer.Selection); }
                        finally { candidate.Cancel(); }
                    }
                }
                var text = FourPaneCliSerialization.ExpandedText(materialized, session.Current, ancestor.Text, left.Text, right.Text, token);
                int unresolved = session.Current.Segments.Count(s => s.IsPlaceholder);
                int conflicts = session.Current.Segments.Count(s => s.IsPlaceholder && s.State == SegmentState.Conflict);
                await ancestor.SaveAsync(args[4], text, token);
                WriteJson(w => { w.WriteNumber("conflicts", conflicts); w.WriteNumber("unresolved", unresolved); w.WriteString("output", Path.GetFullPath(args[4])); });
                return unresolved != 0 ? 1 : 0;
            }
            if (command == "--patch-create")
            {
                var left = await TextDocument.LoadAsync(args[1], token);
                var right = await TextDocument.LoadAsync(args[2], token);
                var patch = UnifiedPatch.Create(left.Text, right.Text, args[1], args[2]);
                await File.WriteAllTextAsync(args[3], patch, new UTF8Encoding(false), token);
                WriteJson(w => w.WriteString("output", Path.GetFullPath(args[3]))); return 0;
            }
            if (command == "--patch-apply")
            {
                var source = await TextDocument.LoadAsync(args[1], token);
                var patch = await File.ReadAllTextAsync(args[2], token);
                var result = UnifiedPatch.Apply(source.Text, patch);
                await source.SaveAsync(args[3], result, token);
                WriteJson(w => w.WriteString("output", Path.GetFullPath(args[3]))); return 0;
            }
            if (command is "--directory" or "--folder")
            {
                if (args.Length < 3) throw new ArgumentException("--directory LEFT RIGHT [--filter FILE] [--no-recursive] [--show-filtered] [--exclude GLOB]");
                var result = await FolderCommands.CompareAsync(args, token);
                WriteJson(w => FolderCommands.WriteModel(w, result));
                return result.Entries.Any(x => x.Status == DirectoryDifferenceKind.Error) ? 2 : result.HasDifferences ? 1 : 0;
            }
            if (command == "--binary")
            {
                await using var left = File.OpenRead(args[1]); await using var right = File.OpenRead(args[2]);
                var different = left.Length != right.Length;
                var l = new byte[65536]; var r = new byte[65536]; long offset = 0; long first = -1;
                while (true)
                {
                    var lc = await left.ReadAsync(l, token); var rc = await right.ReadAsync(r, token);
                    if (lc == 0 && rc == 0) break;
                    if (!l.AsSpan(0, lc).SequenceEqual(r.AsSpan(0, rc)))
                    {
                        different = true;
                        if (first < 0) { var i = 0; while (i < Math.Min(lc, rc) && l[i] == r[i]) i++; first = offset + i; }
                    }
                    offset += Math.Max(lc, rc);
                }
                WriteJson(w => { w.WriteBoolean("different", different); w.WriteNumber("firstDifference", first); });
                return different ? 1 : 0;
            }
            throw new ArgumentException($"不明なコマンド: {command}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine(FolderOperations.DescribeFailure(ex));
            return 2;
        }
    }

    internal static ComparisonOptions ParseOptions(string[] args)
    {
        var options = new ComparisonOptions();
        for (var index = 0; index < args.Length; index++)
        {
            options = args[index] switch
            {
                "--ignore-case" => options with { IgnoreCase = true },
                "--ignore-space" => options with { IgnoreWhitespace = true },
                "--ignore-blank" => options with { IgnoreBlankLines = true },
                "--ignore-numbers" => options with { IgnoreNumbers = true },
                "--word-level" => options with { InlineCharacterLevel = false },
                "--eol" when index + 1 < args.Length => options with { CompareLineEndings = args[++index] switch
                {
                    "strict" => true, "ignore" => false, _ => throw new ArgumentException("改行モードはstrict、ignoreです。")
                } },
                "--comments" when index + 1 < args.Length => options with { CommentSyntax = ParseCommentSyntax(args[++index]) },
                "--whitespace" when index + 1 < args.Length => options with { Whitespace = ParseWhitespace(args[++index]), IgnoreWhitespace = false },
                "--substitute" when index + 2 < args.Length => options with { SubstitutionRules = [.. options.SubstitutionRules, new SubstitutionRule(args[++index], args[++index])] },
                "--ignore-regex" when index + 1 < args.Length => options with { IgnoreLinePattern = args[++index] },
                "--max-work" when index + 1 < args.Length => options with { MaxFallbackComparisons = ParseMaxWork(args[++index]) },
                "--independent-text" or "--pair" => throw new ArgumentException("独立三者Textと比較ペアは --compare にだけ指定できます。"),
                _ => throw new ArgumentException($"不明な比較オプション: {args[index]}")
            };
        }
        return options;
    }

    private static CommentSyntax ParseCommentSyntax(string value) => Enum.TryParse<CommentSyntax>(value, true, out var syntax) && Enum.IsDefined(syntax)
        ? syntax : throw new ArgumentException("コメント構文はnone、cstyle、csharp、python、xmlです。");
    private static int ParseMaxWork(string value) => int.TryParse(value, System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out var result) && result is >= 0 and <= 8_000_000
        ? result : throw new ArgumentException("比較予算は0から8000000です。");
    private static WhitespaceMode ParseWhitespace(string value) => value.ToLowerInvariant() switch
    {
        "none" => WhitespaceMode.None, "trim" => WhitespaceMode.Trim, "changes" => WhitespaceMode.IgnoreChanges, "all" => WhitespaceMode.IgnoreAll,
        _ => throw new ArgumentException("空白モードはnone、trim、changes、allです。")
    };

    private static void WriteIndependentJson(Action<Utf8JsonWriter> content, CancellationToken token)
    {
        using var stream = new BoundedIndependentOutput(token);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject(); content(writer); writer.WriteEndObject(); writer.Flush();
        }
        stream.Write(Encoding.UTF8.GetBytes(Environment.NewLine));
        token.ThrowIfCancellationRequested();
        // 全本文の直列化・上限確認が成功するまでstdoutを公開しない。
        var json = Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
        token.ThrowIfCancellationRequested();
        Console.Write(json);
    }

    private sealed class BoundedIndependentOutput(CancellationToken token) : MemoryStream
    {
        private void Check(int count)
        {
            token.ThrowIfCancellationRequested();
            if (count > ProjectReport.MaximumBytes - Length)
                throw new InvalidDataException("独立三者TextのJSONは32 MiBまでです。");
            var required = checked((int)(Length + count));
            // MemoryStreamの自動倍増も32 MiBを越えないよう、書込み前に容量を確定する。
            if (required > Capacity)
                Capacity = (int)Math.Min(ProjectReport.MaximumBytes, Math.Max(required, (long)Capacity * 2));
        }

        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }

    internal static void WriteJson(Action<Utf8JsonWriter> content)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) { writer.WriteStartObject(); content(writer); writer.WriteEndObject(); }
        Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }
}

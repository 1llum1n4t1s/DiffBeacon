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
                    + "--compare LEFT RIGHT [--ignore-case] [--ignore-space] [--ignore-blank] [--ignore-regex PATTERN] [--ignore-numbers] [--comments cstyle|csharp|python|xml|none] [--whitespace none|trim|changes|all] [--substitute PATTERN REPLACEMENT]\n"
                    + "--directory LEFT RIGHT\n--binary LEFT RIGHT\n"
                    + "--provider ID LEFT RIGHT\n--external-provider EXE LEFT RIGHT FORMAT\n"
                    + "--json LEFT RIGHT\n--table LEFT RIGHT\n--report LEFT RIGHT OUTPUT_HTML\n"
                    + "--project-copy INPUT_PROJECT OUTPUT_PROJECT\n--folder-copy SOURCE_ROOT DEST_ROOT RELATIVE\n"
                    + "--archive-list ARCHIVE [--password-stdin]\n--archive-compare LEFT RIGHT [--password-stdin]\n--archive-entry ARCHIVE ENTRY OUTPUT [--password-stdin]\n--archive-repack INPUT OUTPUT [--password-stdin]\n--archive-create SOURCE_DIRECTORY OUTPUT\n"
                    + "--merge BASE LEFT RIGHT OUTPUT\n--merge-select BASE LEFT RIGHT OUTPUT LEFT|BASE|RIGHT\n--patch-create LEFT RIGHT OUTPUT\n--patch-apply SOURCE PATCH OUTPUT\n"
                    + "--self-test OUTPUT_DIRECTORY\nExit: 0 equal/success, 1 differences/conflicts, 2 error");
                return 0;
            }
            var command = args[0];
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
                var options = ParseOptions(args.Skip(3).ToArray());
                var left = await TextDocument.LoadAsync(args[1], token);
                var right = await TextDocument.LoadAsync(args[2], token);
                var result = TextDiffer.Compare(left.Text, right.Text, options);
                WriteJson(writer =>
                {
                    writer.WriteBoolean("different", result.HasDifferences);
                    writer.WriteNumber("blocks", result.Blocks.Count);
                    writer.WriteString("leftEncoding", left.EncodingName);
                    writer.WriteString("rightEncoding", right.EncodingName);
                    writer.WriteStartArray("rows");
                    foreach (var row in result.Rows)
                    {
                        writer.WriteStartObject(); writer.WriteString("kind", row.Kind.ToString());
                        writer.WriteString("left", row.LeftText); writer.WriteString("right", row.RightText);
                        if (row.LeftLineNumber is int ln) writer.WriteNumber("leftLine", ln);
                        if (row.RightLineNumber is int rn) writer.WriteNumber("rightLine", rn);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                });
                return result.HasDifferences ? 1 : 0;
            }
            if (command == "--project-copy")
            {
                var project = await WorkspaceStore.LoadAsync(args[1], token);
                await WorkspaceStore.SaveAsync(args[2], project, token);
                WriteJson(w => w.WriteString("output", Path.GetFullPath(args[2]))); return 0;
            }
            if (command == "--report")
            {
                if (args.Length != 4) throw new ArgumentException("--report LEFT RIGHT OUTPUT_HTML");
                var left = await TextDocument.LoadAsync(args[1], token); var right = await TextDocument.LoadAsync(args[2], token);
                var diff = TextDiffer.Compare(left.Text, right.Text);
                await File.WriteAllTextAsync(args[3], HtmlReport.Create(diff, args[1], args[2]), new UTF8Encoding(false), token);
                WriteJson(w => w.WriteString("output", Path.GetFullPath(args[3]))); return 0;
            }
            if (command == "--folder-copy")
            {
                if (args.Length != 4) throw new ArgumentException("--folder-copy SOURCE_ROOT DEST_ROOT RELATIVE");
                await FolderOperations.CopyAsync(args[1], args[2], args[3], token);
                WriteJson(w => w.WriteString("copied", args[3])); return 0;
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
                var table = StructuredComparer.CompareDelimited(left.Text, right.Text, delimiter);
                WriteJson(w => { w.WriteBoolean("different", table.HasDifferences); w.WriteNumber("rows", Math.Max(table.Left.Rows.Count, table.Right.Rows.Count)); w.WriteNumber("cols", Math.Max(table.Left.ColumnCount, table.Right.ColumnCount)); });
                return table.HasDifferences ? 1 : 0;
            }
            if (command is "--merge" or "--merge-select")
            {
                if (args.Length != (command == "--merge-select" ? 6 : 5)) throw new ArgumentException("--merge BASE LEFT RIGHT OUTPUT または --merge-select BASE LEFT RIGHT OUTPUT LEFT|BASE|RIGHT");
                var ancestor = await TextDocument.LoadAsync(args[1], token);
                var left = await TextDocument.LoadAsync(args[2], token);
                var right = await TextDocument.LoadAsync(args[3], token);
                var session = MergeSession.CreateThreeWay(ancestor.Text, left.Text, right.Text, token: token);
                if (command == "--merge-select")
                {
                    if (!Enum.TryParse<MergeSource>(args[5], true, out var source) || !Enum.IsDefined(source)) throw new ArgumentException("採用元はLEFT、BASE、RIGHTです。");
                    session.ChooseAll(source);
                }
                await ancestor.SaveAsync(args[4], session.Text, token);
                WriteJson(w => { w.WriteNumber("conflicts", session.ConflictCount); w.WriteNumber("unresolved", session.UnresolvedCount); w.WriteString("output", Path.GetFullPath(args[4])); });
                return session.UnresolvedCount != 0 ? 1 : 0;
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
            if (command == "--directory")
            {
                FileFilter? filter = null;
                if (args.Length > 3)
                {
                    if (args.Length != 5 || args[3] != "--filter") throw new ArgumentException("--directory LEFT RIGHT [--filter FILE]");
                    filter = FileFilter.Load(args[4]);
                }
                var result = await DirectoryComparer.CompareAsync(args[1], args[2], new DirectoryComparisonOptions { FileFilter = filter }, token);
                WriteJson(w =>
                {
                    w.WriteStartArray("entries");
                    foreach (var entry in result.Entries) { w.WriteStartObject(); w.WriteString("path", entry.RelativePath); w.WriteString("status", entry.Status.ToString()); w.WriteEndObject(); }
                    w.WriteEndArray();
                });
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
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static ComparisonOptions ParseOptions(string[] args)
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
                "--comments" when index + 1 < args.Length => options with { CommentSyntax = ParseCommentSyntax(args[++index]) },
                "--whitespace" when index + 1 < args.Length => options with { Whitespace = ParseWhitespace(args[++index]), IgnoreWhitespace = false },
                "--substitute" when index + 2 < args.Length => options with { SubstitutionRules = [.. options.SubstitutionRules, new SubstitutionRule(args[++index], args[++index])] },
                "--ignore-regex" when index + 1 < args.Length => options with { IgnoreLinePattern = args[++index] },
                _ => throw new ArgumentException($"不明な比較オプション: {args[index]}")
            };
        }
        return options;
    }

    private static CommentSyntax ParseCommentSyntax(string value) => Enum.TryParse<CommentSyntax>(value, true, out var syntax) && Enum.IsDefined(syntax)
        ? syntax : throw new ArgumentException("コメント構文はnone、cstyle、csharp、python、xmlです。");
    private static WhitespaceMode ParseWhitespace(string value) => value.ToLowerInvariant() switch
    {
        "none" => WhitespaceMode.None, "trim" => WhitespaceMode.Trim, "changes" => WhitespaceMode.IgnoreChanges, "all" => WhitespaceMode.IgnoreAll,
        _ => throw new ArgumentException("空白モードはnone、trim、changes、allです。")
    };

    internal static void WriteJson(Action<Utf8JsonWriter> content)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) { writer.WriteStartObject(); content(writer); writer.WriteEndObject(); }
        Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }
}

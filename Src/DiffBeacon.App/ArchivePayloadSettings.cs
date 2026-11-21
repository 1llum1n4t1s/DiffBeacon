using Avalonia.Controls;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static class ArchivePayloadSettings
{
    internal static GZipPayloadKind Parse(string? value) => value switch
    {
        "Auto" => GZipPayloadKind.Auto, "File" => GZipPayloadKind.File, "Tar" => GZipPayloadKind.Tar,
        _ => throw new InvalidDataException("gzip本文の形式はAuto／File／Tarで指定してください。")
    };
    internal static GZipPayloadKind ParseCommand(string? value) => value switch
    {
        "auto" => GZipPayloadKind.Auto, "file" => GZipPayloadKind.File, "tar" => GZipPayloadKind.Tar,
        _ => throw new ArgumentException("gzip本文の形式はauto／file／tarで指定してください。")
    };
    internal static IEnumerable<GZipPayloadKind> Choices(string[]? choices, int count)
    {
        if (count is < 1 or > 4097 || choices is not null && choices.Length != count)
            throw new InvalidDataException("gzip本文の形式はrootと各コンテナーに一つずつ指定してください。");
        return choices is null ? Enumerable.Repeat(GZipPayloadKind.Auto, count) : choices.Select(Parse);
    }
    internal static string[]? Capture(IReadOnlyList<GZipPayloadKind> choices)
        => choices.All(value => value == GZipPayloadKind.Auto) ? null : choices.Select(value => value.ToString()).ToArray();
    internal static string[]? Copy(string[]? choices, int count) => Capture(Choices(choices, count).ToArray());
    internal static bool HasNonAuto(string[]? choices) => choices?.Any(value => Parse(value) != GZipPayloadKind.Auto) == true;
    internal static CompressionPayloadKind ParseCompression(string? value) => value switch
    {
        "Auto" => CompressionPayloadKind.Auto, "File" => CompressionPayloadKind.File, "Tar" => CompressionPayloadKind.Tar,
        _ => throw new InvalidDataException("BZip2／Z本文の形式はAuto／File／Tarで指定してください。")
    };
    internal static CompressionPayloadKind ParseCompressionCommand(string? value) => value switch
    {
        "auto" => CompressionPayloadKind.Auto, "file" => CompressionPayloadKind.File, "tar" => CompressionPayloadKind.Tar,
        _ => throw new ArgumentException("BZip2／Z本文の形式はauto／file／tarで指定してください。")
    };
    internal static IEnumerable<CompressionPayloadKind> CompressionChoices(string[]? choices, int count)
    {
        if (count is < 1 or > 4097 || choices is not null && choices.Length != count)
            throw new InvalidDataException("BZip2／Z本文の形式はrootと各コンテナーに一つずつ指定してください。");
        return choices is null ? Enumerable.Repeat(CompressionPayloadKind.Auto, count) : choices.Select(ParseCompression);
    }
    internal static string[]? Capture(IReadOnlyList<CompressionPayloadKind> choices)
    {
        if (choices.Any(value => !Enum.IsDefined(value))) throw new InvalidDataException("BZip2／Z本文の形式が不正です。");
        return choices.All(value => value == CompressionPayloadKind.Auto) ? null : choices.Select(value => value.ToString()).ToArray();
    }
    internal static string[]? CopyCompression(string[]? choices, int count) => Capture(CompressionChoices(choices, count).ToArray());
    internal static bool HasNonAutoCompression(string[]? choices) => choices?.Any(value => ParseCompression(value) != CompressionPayloadKind.Auto) == true;
    // 双方の選択を同時に適用し、途中状態の旧指定との衝突を避ける。
    internal static ArchiveSource WithChoices(ArchiveSource source, int layer, GZipPayloadKind gzip, CompressionPayloadKind compression)
    {
        var gzipChoices = source.ContainerGZipPayloadKinds.ToArray(); var compressionChoices = source.ContainerCompressionPayloadKinds.ToArray();
        gzipChoices[layer] = gzip; compressionChoices[layer] = compression;
        return new(source.RootPath, source.EntryChain, source.RootSha256, source.ContainerNameCodePages, gzipChoices, compressionChoices);
    }
    // 新しい列は対象prefix内に明示指定があるときだけ、既存列に存在しないmarkerの後へ追加する。
    internal static IEnumerable<string> NormalizedChoices(int[]? codePages, string[]? payloadKinds, int count, int? prefixCount = null, string[]? compressionPayloadKinds = null)
    {
        var prefix = prefixCount ?? count;
        if (prefix < 1) throw new InvalidDataException("本文形式の共通階層が不正です。");
        var compression = CompressionChoices(compressionPayloadKinds, count).Take(prefix).ToArray();
        var legacy = ArchiveNameSettings.Choices(codePages, count).Take(prefix).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Concat(Choices(payloadKinds, count).Take(prefix).Select(value => value.ToString()));
        return compression.All(value => value == CompressionPayloadKind.Auto) ? legacy
            : legacy.Append("\u0000BZip2/Z:payload:v9").Concat(compression.Select(value => value.ToString()));
    }
    internal static ComboBox Picker(GZipPayloadKind selected = GZipPayloadKind.Auto) => new()
    {
        ItemsSource = new[] { GZipPayloadKind.Auto, GZipPayloadKind.File, GZipPayloadKind.Tar },
        SelectedItem = selected, Width = 130, Margin = new Avalonia.Thickness(4),
        ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<GZipPayloadKind>((value, _) => new TextBlock
        {
            Text = value switch { GZipPayloadKind.Auto => "自動判定", GZipPayloadKind.File => "単一ファイル", GZipPayloadKind.Tar => "TARアーカイブ", _ => "" }
        })
    };
    internal static ComboBox CompressionPicker(CompressionPayloadKind selected = CompressionPayloadKind.Auto) => new()
    {
        ItemsSource = new[] { CompressionPayloadKind.Auto, CompressionPayloadKind.File, CompressionPayloadKind.Tar },
        SelectedItem = selected, Width = 130, Margin = new Avalonia.Thickness(4),
        ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<CompressionPayloadKind>((value, _) => new TextBlock
        {
            Text = value switch { CompressionPayloadKind.Auto => "自動判定", CompressionPayloadKind.File => "単一ファイル", CompressionPayloadKind.Tar => "TARアーカイブ", _ => "" }
        })
    };
    internal static CompressionPayloadKind SelectedCompression(ComboBox picker) => picker.SelectedItem is CompressionPayloadKind value ? value : CompressionPayloadKind.Auto;
    internal static GZipPayloadKind Selected(ComboBox picker) => picker.SelectedItem is GZipPayloadKind value ? value : GZipPayloadKind.Auto;
}

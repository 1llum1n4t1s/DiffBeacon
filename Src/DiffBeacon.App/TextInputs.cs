using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffBeacon.App;

/// <summary>Text入力の役割と存在だけを保存し、本文や保存点は各側の既存payloadから分離する。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TextInputDescriptor
{
    public string Semantics { get; set; } = "";
    public TextInputSide? Left { get; set; }
    public TextInputSide? Middle { get; set; }
    public TextInputSide? Right { get; set; }

    internal TextInputDescriptor Copy() => this with
    {
        Left = Left is null ? null : Left with { },
        Middle = Middle is null ? null : Middle with { },
        Right = Right is null ? null : Right with { }
    };

    internal TextInputSide Side(int side) => (side switch
    {
        0 => Left, 1 => Middle, 2 => Right,
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    }) ?? throw new InvalidDataException("Text入力の側がありません。");

    internal void Validate(ComparisonProject project)
    {
        if (!string.Equals(project.Mode, "Text", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("typed Text入力にはTextの明示形式が必要です。");
        if (Semantics is not ("FixedAncestor" or "Independent"))
            throw new InvalidDataException("Text入力の比較役割が不正です。");
        for (var side = 0; side < 3; side++)
        {
            var kind = Side(side).Kind;
            var path = side switch { 0 => project.LeftPath, 1 => project.BasePath, _ => project.RightPath };
            var archive = ProjectInputs.Archive(project, side);
            if (kind is not ("Physical" or "Untitled" or "Archive" or "Absent"))
                throw new InvalidDataException("Text入力の種類が不正です。");
            if (Semantics == "Independent" && kind is not ("Physical" or "Untitled"))
                throw new InvalidDataException("独立三者Textの内包入力と不在入力にはまだ対応していません。");
            if (Semantics == "FixedAncestor" && kind == "Untitled")
                throw new InvalidDataException("固定祖先Textの無題入力にはまだ対応していません。");
            if (kind == "Absent" && side != 1)
                throw new InvalidDataException("固定祖先Textでは中央以外の不在入力を指定できません。");
            var valid = kind switch
            {
                "Physical" => !string.IsNullOrWhiteSpace(path) && archive is null,
                "Archive" => path == "" && archive is not null,
                _ => path == "" && archive is null
            };
            if (!valid) throw new InvalidDataException("Text入力の種類と既存path／内包payloadが一致しません。");
        }
    }

    internal static void ValidateJson(JsonElement value)
    {
        RequireFields(value, ["semantics", "left", "middle", "right"]);
        if (value.GetProperty("semantics").ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Text入力の比較役割には文字列が必要です。");
        foreach (var name in new[] { "left", "middle", "right" })
        {
            var side = value.GetProperty(name);
            RequireFields(side, ["kind"]);
            if (side.GetProperty("kind").ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Text入力の種類には文字列が必要です。");
        }
    }

    private static void RequireFields(JsonElement value, string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Text入力のdescriptorと各側はobjectで指定してください。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!required.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name))
                throw new InvalidDataException("Text入力に未対応または重複した項目があります。");
        if (seen.Count != required.Length)
            throw new InvalidDataException("Text入力の必須項目がありません。");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TextInputSide
{
    public string Kind { get; set; } = "";
}

namespace DiffBeacon.Core;

/// <summary>一回の比較で使う原文の実パスと、既読入力の文字コード。nullのpathは未保存・仮想入力を表し、ファイルは読み込まない。</summary>
public sealed record TextComparisonSourcePaths
{
    public string? Left { get; }
    public string? Base { get; }
    public string? Right { get; }
    public string? LeftEncodingName { get; }
    public string? BaseEncodingName { get; }
    public string? RightEncodingName { get; }

    public TextComparisonSourcePaths(string? left = null, string? @base = null, string? right = null,
        string? leftEncodingName = null, string? baseEncodingName = null, string? rightEncodingName = null)
    {
        Left = ValidatePath(left, nameof(left));
        Base = ValidatePath(@base, nameof(@base));
        Right = ValidatePath(right, nameof(right));
        LeftEncodingName = ValidateEncodingName(leftEncodingName, nameof(leftEncodingName));
        BaseEncodingName = ValidateEncodingName(baseEncodingName, nameof(baseEncodingName));
        RightEncodingName = ValidateEncodingName(rightEncodingName, nameof(rightEncodingName));
    }

    internal static string? ValidatePath(string? path, string parameterName)
    {
        if (path is not null && (!Path.IsPathFullyQualified(path) || path.Contains('\0')))
            throw new ArgumentException("原文の実パスは完全修飾パスまたはnullを指定してください。", parameterName);
        return path;
    }

    private static string? ValidateEncodingName(string? encodingName, string parameterName)
    {
        if (encodingName is not null && (string.IsNullOrWhiteSpace(encodingName) || encodingName.Length > 128 || encodingName.Contains('\0')))
            throw new ArgumentException("原文の文字コード名が不正です。", parameterName);
        return encodingName;
    }

    internal string? ForPane(TablePaneMask pane) => pane switch
    {
        TablePaneMask.Left => Left,
        TablePaneMask.Base => Base,
        TablePaneMask.Right => Right,
        _ => throw new ArgumentException("原文のパスを取得するペインを一つ指定してください。", nameof(pane))
    };

    internal bool IncludeFinalEofCandidateForPane(TablePaneMask pane)
    {
        var encodingName = pane switch
        {
            TablePaneMask.Left => LeftEncodingName,
            TablePaneMask.Base => BaseEncodingName,
            TablePaneMask.Right => RightEncodingName,
            _ => throw new ArgumentException("文字コードを取得するペインを一つ指定してください。", nameof(pane))
        };
        // FileContentRefのUnicode readerはEOF後に空bufferを一度評価する。
        // NONE/code-page readerは非EOL末尾行のReadStringでfalseを返す。
        // 文字コード不明の仮想入力は従来のUnicode相当の既定動作を維持する。
        return encodingName is null
            || encodingName.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            || encodingName.Equals("utf-16", StringComparison.OrdinalIgnoreCase)
            || encodingName.Equals("utf-16BE", StringComparison.OrdinalIgnoreCase)
            || encodingName.Equals("utf-32", StringComparison.OrdinalIgnoreCase)
            || encodingName.Equals("utf-32BE", StringComparison.OrdinalIgnoreCase);
    }
}

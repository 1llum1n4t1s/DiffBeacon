namespace DiffBeacon.Core;

/// <summary>行変換式が出力する診断の旧ロガー互換severity。</summary>
public enum LineTransformLogLevel
{
    Error = 0,
    Warning = 1,
    Information = 2
}

/// <summary>比較単位で注入する行変換ログの受け口。</summary>
public interface ILineTransformLogEnvironment
{
    void Write(LineTransformLogLevel level, string message);
}

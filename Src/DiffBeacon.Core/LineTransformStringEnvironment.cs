using System.Text;

namespace DiffBeacon.Core;

/// <summary>行変換で文字列の大小文字・Unicode正規化・文字種を変換する実行環境。</summary>
public interface ILineTransformStringEnvironment
{
    string ToLower(string value);
    string ToUpper(string value);
    string NormalizeUnicode(string value, NormalizationForm form);
    string MapCharacters(string value, LineTransformCharacterMapping mapping)
        => throw new ArgumentException("この環境は文字種変換に対応していません。");
}

/// <summary>互換正規化から独立した、原典の個別文字種変換。</summary>
public enum LineTransformCharacterMapping
{
    HalfWidth, FullWidth, Hiragana, Katakana, SimplifiedChinese, TraditionalChinese
}

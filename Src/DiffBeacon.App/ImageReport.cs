using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace DiffBeacon.App;

// 確定済みの画像から、元画素と既存の差分マスクを自己完結 HTML にする。
internal static class ImageReport
{
    internal static string Create(ImageComparisonEngine.Snapshot left, ImageComparisonEngine.Snapshot right,
        string leftTitle, string rightTitle, int threshold = 0, int? leftFrame = null, int? rightFrame = null,
        CancellationToken token = default, int maximumBytes = ProjectReport.MaximumBytes)
    {
        token.ThrowIfCancellationRequested();
        ImageComparisonEngine.ValidateComparison(left, right, leftFrame, rightFrame, threshold);
        var html = new BoundedHtml(Math.Min(maximumBytes, ProjectReport.MaximumBytes), token);
        var selected = leftFrame.HasValue;
        html.Append("<!doctype html><html lang=\"ja\"><head><meta charset=\"utf-8\"><title>画像比較</title>"
            + "<style>body{font-family:system-ui,sans-serif}table{border-collapse:collapse;width:100%}"
            + "th,td{border:1px solid #888;padding:.5rem;vertical-align:top}img{max-width:100%;height:auto;"
            + "background:repeating-conic-gradient(#ddd 0% 25%,#fff 0% 50%) 0 0/16px 16px}"
            + "td{width:33%}caption{text-align:left;padding:.5rem}</style></head>"
            + "<body data-mode=\"Image\" data-different=\"");
        var differentPosition = html.Length;
        html.Append("true\" data-frame-mode=\"");
        html.Append(selected ? "selected" : "all");
        html.Append("\" data-left-frames=\""); html.Number(left.FrameCount);
        html.Append("\" data-right-frames=\""); html.Number(right.FrameCount);
        html.Append("\" data-threshold=\""); html.Number(threshold);
        html.Append("\"><h1>画像比較</h1><table><caption>閾値: "); html.Number(threshold);
        html.Append(" / フレーム: "); html.Append(selected ? "選択した組" : "全同番号フレーム");
        html.Append("</caption><thead><tr><th>"); html.Escape(leftTitle);
        html.Append("</th><th>"); html.Escape(rightTitle);
        html.Append("</th><th>ピクセル差分</th></tr></thead><tbody>");
        var different = !selected && left.FrameCount != right.FrameCount;
        var count = selected ? 1 : Math.Max(left.FrameCount, right.FrameCount);
        for (var index = 1; index <= count; index++)
        {
            token.ThrowIfCancellationRequested();
            var aNumber = selected ? leftFrame!.Value : index;
            var bNumber = selected ? rightFrame!.Value : index;
            var a = aNumber <= left.FrameCount ? left.Decode(aNumber, token) : null;
            var b = bNumber <= right.FrameCount ? right.Decode(bNumber, token) : null;
            token.ThrowIfCancellationRequested();
            var comparison = ImageComparisonEngine.ComparePixels(a, b, threshold, true, token);
            different |= a is null || b is null || comparison.DifferentPixels > 0;
            html.Append("<tr data-left-frame=\""); html.Frame(a?.Number);
            html.Append("\" data-right-frame=\""); html.Frame(b?.Number);
            html.Append("\" data-different-pixels=\""); html.Number(comparison.DifferentPixels);
            html.Append("\" data-total-pixels=\""); html.Number(comparison.TotalPixels);
            html.Append("\"><td>");
            AppendSource(html, a, "left", leftTitle, token);
            html.Append("</td><td>");
            AppendSource(html, b, "right", rightTitle, token);
            html.Append("</td><td><p>差分画素: "); html.Number(comparison.DifferentPixels);
            html.Append(" / "); html.Number(comparison.TotalPixels); html.Append("</p>");
            AppendImage(html, comparison.DifferencePixels!, comparison.Width, comparison.Height,
                "difference", null, "ピクセル差分", token);
            html.Append("</td></tr>");
        }
        html.Append("</tbody></table></body></html>");
        if (!different) html.MarkSame(differentPosition);
        return html.Finish();
    }

    private static void AppendSource(BoundedHtml html, ImageComparisonEngine.DecodedFrame? frame,
        string side, string title, CancellationToken token)
    {
        if (frame is null) { html.Append("<p>対応するフレームなし</p>"); return; }
        html.Append("<p>フレーム "); html.Number(frame.Number); html.Append("</p>");
        AppendImage(html, frame.Pixels, frame.Width, frame.Height, side, frame.Number, title, token);
    }

    private static void AppendImage(BoundedHtml html, byte[] pixels, int width, int height,
        string side, int? frame, string title, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var hash = ImageComparisonEngine.PixelHash(pixels, token);
        html.Append("<img data-side=\""); html.Append(side); html.Append("\"");
        if (frame.HasValue) { html.Append(" data-frame=\""); html.Number(frame.Value); html.Append("\""); }
        html.Append(" data-width=\""); html.Number(width);
        html.Append("\" data-height=\""); html.Number(height);
        html.Append("\" data-pixel-sha256=\""); html.Append(hash);
        html.Append("\" alt=\""); html.Escape(title); html.Append("\" src=\"data:image/png;base64,");
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            // Unpremul の pixmap を直接エンコードし、透明画素の色成分も保持する。
            using var pixmap = new SKPixmap(new SKImageInfo(width, height, SKColorType.Bgra8888,
                SKAlphaType.Unpremul), pinned.AddrOfPinnedObject(), checked(width * 4));
            token.ThrowIfCancellationRequested();
            using var png = pixmap.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidDataException("画像レポートのPNGを作成できません。");
            token.ThrowIfCancellationRequested();
            html.Base64(png.AsSpan());
        }
        finally { pinned.Free(); }
        token.ThrowIfCancellationRequested();
        html.Append("\">");
    }

    private sealed class BoundedHtml(int maximumBytes, CancellationToken token)
    {
        private readonly StringBuilder _text = new();
        private long _bytes;
        internal int Length => _text.Length;

        private void Ensure(long bytes)
        {
            token.ThrowIfCancellationRequested();
            if (bytes > maximumBytes - _bytes)
                throw new InvalidDataException("画像HTMLレポートが出力サイズ上限（最大32 MiB）を超えます。");
        }

        internal void Append(string value)
        {
            token.ThrowIfCancellationRequested();
            var bytes = Encoding.UTF8.GetByteCount(value);
            Ensure(bytes);
            _text.Append(value);
            _bytes += bytes;
        }

        internal void Number(long value) => Append(value.ToString(CultureInfo.InvariantCulture));
        internal void Frame(int? value) { if (value.HasValue) Number(value.Value); else Append("none"); }

        internal void Escape(string value)
        {
            // 全文を先にエスケープしない。UTF-8 とサロゲートペアを rune 単位で計上する。
            token.ThrowIfCancellationRequested();
            foreach (var rune in value.EnumerateRunes())
                Append(rune.Value switch
                {
                    '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;",
                    _ => rune.ToString()
                });
            token.ThrowIfCancellationRequested();
        }

        internal void Base64(ReadOnlySpan<byte> png)
        {
            // 増幅後の全長を変換前に確認し、フレームごとの巨大な文字列や byte[] を作らない。
            Ensure(checked(((long)png.Length + 2) / 3 * 4));
            const int chunkBytes = 12_288; // 3の倍数で、途中の padding を避ける。
            for (var offset = 0; offset < png.Length; offset += chunkBytes)
            {
                token.ThrowIfCancellationRequested();
                Append(Convert.ToBase64String(png.Slice(offset, Math.Min(chunkBytes, png.Length - offset))));
            }
            token.ThrowIfCancellationRequested();
        }

        internal void MarkSame(int position)
        {
            Ensure(1);
            _text.Remove(position, 4).Insert(position, "false");
            _bytes++;
        }

        internal string Finish()
        {
            token.ThrowIfCancellationRequested();
            Ensure(0);
            var result = _text.ToString();
            token.ThrowIfCancellationRequested();
            return result;
        }
    }
}

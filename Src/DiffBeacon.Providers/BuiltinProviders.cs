using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DiffBeacon.Providers;

public static class BuiltinComparisonProviders
{
    public static ComparisonProviderRegistry CreateDefault()
    {
        var registry = new ComparisonProviderRegistry();
        foreach (var id in new[] { "xml", "html-source", "html-text", "web-source", "web-text", "office", "tar", "tar-metadata" })
            registry.Register(new BuiltinProvider(id));
        registry.Register(new ArchiveComparisonProvider());
        return registry;
    }

    private sealed class BuiltinProvider(string id) : IComparisonProvider
    {
        public string Id => id;
        public IReadOnlyList<string> Formats { get; } = Array.AsReadOnly(new[] { id });
        public async Task<ProviderResult> CompareAsync(ComparisonRequest request, CancellationToken cancellationToken)
        {
            if (!string.Equals(request.Format, Id, StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("プロバイダーと比較形式が一致しません。");
            var left = await ReadAsync(request.LeftPath, cancellationToken);
            var right = await ReadAsync(request.RightPath, cancellationToken);
            var description = Id switch
            {
                "xml" => "XML: 名前空間を展開し属性順を正規化。DTDは禁止。要素内の本文・空白・改行を保持（インデント変更も差分）。",
                "html-source" => "HTMLソース: 改行をLFに統一。マークアップ・属性順・空白は比較対象。",
                "html-text" => "HTMLテキスト: 静的な本文を抽出。head/script/style/templateを除外。CSS・JavaScript・レイアウトは未評価。",
                "web-source" => "HTTPページのHTMLソース: GETで取得した応答本文を比較。ログイン・JavaScript実行・ブラウザー描画なし。",
                "web-text" => "HTTPページの静的テキスト: GETで取得して本文抽出。ログイン・CSS・JavaScript・ブラウザー描画なし。",
                "office" => "Office Open XML: DOCX本文・ヘッダー等、PPTXスライド本文、XLSXセル値・数式・キャッシュ値。書式・画像・描画・数式再計算なし。",
                "tar" => "tar/tar.gz: 展開せず名前・型・リンク先・サイズ・SHA-256を比較。時刻・所有者・権限・格納順は無視。リンク先は参照しません。",
                _ => "tar/tar.gzメタデータ: 内容に加え所有者番号・権限・更新日時も比較。リンク先は参照しません。"
            };
            return new ProviderResult($"{(left == right ? "一致" : "差分あり")} · {description}", left, right);
        }
        private async Task<string> ReadAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Id.StartsWith("web-", StringComparison.Ordinal))
            { var html = await FetchAsync(path, token); return Id == "web-text" ? HtmlText(html, token) : NormalizeLines(html); }
            if (Id == "office") return await OfficeAsync(path, token);
            if (Id is "tar" or "tar-metadata") return await TarAsync(path, Id == "tar-metadata", token);
            var bytes = await ReadFileAsync(path, Id == "xml" ? XmlLimit : TextLimit, token);
            if (Id == "xml") return CanonicalXml(ParseXml(bytes), token);
            var text = Decode(bytes, null);
            return Id == "html-text" ? HtmlText(text, token) : NormalizeLines(text);
        }
    }

    private const int TextLimit = 8 * 1024 * 1024;
    private const int XmlLimit = 16 * 1024 * 1024;
    private const long EntryLimit = 256L * 1024 * 1024;
    private const long ArchiveLimit = 1024L * 1024 * 1024;
    private static string NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static async Task<byte[]> ReadFileAsync(string path, int limit, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        if (file.Length > limit) throw new InvalidDataException($"入力は {limit / (1024 * 1024)} MiBまでです。");
        return await ReadBytesAsync(file, limit, token);
    }
    private static async Task<byte[]> ReadBytesAsync(Stream input, int limit, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token); if (count == 0) return output.ToArray();
            if (output.Length > limit - count) throw new InvalidDataException("入力の読込量が上限を超えました。");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
    private static string Decode(byte[] bytes, string? charset)
    {
        Encoding encoding;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(bytes.AsSpan(3));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return Encoding.UTF32.GetString(bytes.AsSpan(4));
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return new UTF32Encoding(true, false).GetString(bytes.AsSpan(4));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes.AsSpan(2));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(bytes.AsSpan(2));
        if (string.IsNullOrWhiteSpace(charset)) encoding = new UTF8Encoding(false, true);
        else
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try { encoding = Encoding.GetEncoding(charset.Trim().Trim('"', '\''), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
            catch (ArgumentException ex) { throw new InvalidDataException("応答の文字コードに対応していません。", ex); }
        }
        return encoding.GetString(bytes);
    }

    private static readonly HttpClient Http = new(new HttpClientHandler
    { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli }) { Timeout = Timeout.InfiniteTimeSpan };
    private static Uri WebUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new ArgumentException("Web比較には認証情報を含まないHTTP/HTTPSのURLが必要です。");
        return uri;
    }
    private static async Task<string> FetchAsync(string value, CancellationToken cancellationToken)
    {
        var uri = WebUri(value);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("DiffBeacon/1.0"); request.Headers.Accept.ParseAdd("text/html, application/xhtml+xml, text/plain;q=0.8");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is null || redirects == 5) throw new HttpRequestException("HTTPリダイレクト先がないか、5回の上限を超えました。");
                uri = WebUri(new Uri(uri, response.Headers.Location).AbsoluteUri); continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > TextLimit) throw new InvalidDataException("HTTP応答は8 MiBまでです。");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) && !string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"HTML/テキストではないHTTP応答です: {mediaType}");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return Decode(await ReadBytesAsync(stream, TextLimit, deadline.Token), response.Content.Headers.ContentType?.CharSet);
        }
        throw new HttpRequestException("HTTPリダイレクト上限を超えました。");
    }

    private static XDocument ParseXml(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = XmlLimit, MaxCharactersFromEntities = 0, IgnoreWhitespace = false });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private static string CanonicalXml(XDocument document, CancellationToken token)
    {
        var output = new StringBuilder();
        void Node(XNode node, int depth, bool preserve)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 128) throw new InvalidDataException("XMLは深さ128までです。");
            if (node is XElement element)
            {
                var mode = element.Attribute(XNamespace.Xml + "space")?.Value;
                if (mode is not null) preserve = mode == "preserve";
                output.Append('<').Append(element.Name);
                foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))
                    output.Append(' ').Append(attribute.Name).Append("=\"").Append(CanonicalXmlValue(element, attribute.Value, attribute)).Append('"');
                output.AppendLine(">");
                foreach (var child in element.Nodes()) Node(child, depth + 1, preserve);
                output.Append("</").Append(element.Name).AppendLine(">");
            }
            else if (node is XText text)
            {
                var value = NormalizeLines(text.Value);
                // DTDなしでは要素内の空白が無意味とは断定できないため、本文を保持する。
                if (text.Parent is null && string.IsNullOrWhiteSpace(value)) return;
                output.Append("TEXT \"").Append(text.Parent is XElement parent ? CanonicalXmlValue(parent, value, null) : Escape(value)).AppendLine("\"");
            }
            else if (node is XComment comment) output.Append("COMMENT ").AppendLine(Escape(comment.Value));
            else if (node is XProcessingInstruction instruction) output.Append("PI ").Append(instruction.Target).Append(' ').AppendLine(instruction.Data);
            if (output.Length > XmlLimit * 4L) throw new InvalidDataException("XML正規化結果が上限を超えました。");
        }
        foreach (var node in document.Nodes()) Node(node, 0, false);
        return output.ToString();
    }
    private static readonly XNamespace SchemaInstance = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Schema = "http://www.w3.org/2001/XMLSchema";
    private static readonly Regex QNamePrefixes = new(@"([\p{L}_][\p{L}\p{N}_.-]*):", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(2));
    private static string CanonicalXmlValue(XElement context, string value, XAttribute? attribute)
    {
        var qname = attribute?.Name == SchemaInstance + "type";
        var list = false;
        if (attribute is not null && context.Name.Namespace == Schema && attribute.Name.Namespace == XNamespace.None)
        {
            qname = attribute.Name.LocalName is "type" or "ref" or "base" or "itemType" or "substitutionGroup" or "refer";
            list = attribute.Name.LocalName == "memberTypes";
        }
        if (attribute is null && context.Attribute(SchemaInstance + "type") is { } type)
        { var typeName = ResolveQName(context, type.Value); qname = typeName == Schema + "QName" || typeName == Schema + "NOTATION"; }
        if (qname) return "QNAME " + Escape(ResolveQName(context, value).ToString());
        if (list) return "QNAMES " + string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(v => Escape(ResolveQName(context, v).ToString())));
        // スキーマがない値はQNameと断定しない。字面と参照し得るprefixの束縛を併記する。
        var bindings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in QNamePrefixes.Matches(value))
        {
            var prefix = match.Groups[1].Value;
            if (context.GetNamespaceOfPrefix(prefix) is { } ns) bindings[prefix] = ns.NamespaceName;
        }
        return Escape(value) + (bindings.Count == 0 ? "" : " [NAMESPACE-BINDINGS " + string.Join(' ', bindings.Select(pair => Escape(pair.Key) + "=" + Escape(pair.Value))) + "]");
    }
    private static XName ResolveQName(XElement context, string value)
    {
        var lexical = value.Trim(); var separator = lexical.IndexOf(':');
        var prefix = separator < 0 ? "" : lexical[..separator]; var local = separator < 0 ? lexical : lexical[(separator + 1)..];
        try { XmlConvert.VerifyNCName(local); if (separator >= 0) XmlConvert.VerifyNCName(prefix); }
        catch (XmlException ex) { throw new InvalidDataException("XMLのQName値が不正です。", ex); }
        var ns = separator < 0 ? context.GetDefaultNamespace() : context.GetNamespaceOfPrefix(prefix) ?? throw new InvalidDataException($"XMLのQName prefixが未定義です: {prefix}");
        return ns + local;
    }
    private static string Escape(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    // HTML5のブラウザーDOMではなく、引用符付き属性を考慮した限定トークナイザー。
    private static string HtmlText(string html, CancellationToken token)
    {
        var output = new StringBuilder(); string? suppressed = null;
        var blocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "p", "div", "br", "hr", "li", "tr", "table", "section", "article", "header", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre" };
        for (var i = 0; i < html.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (suppressed is not null)
            {
                var closing = FindClosingTag(html, suppressed, i);
                if (closing < 0) break;
                i = closing; suppressed = null;
            }
            if (html[i] != '<')
            {
                var end = html.IndexOf('<', i); if (end < 0) end = html.Length;
                output.Append(WebUtility.HtmlDecode(html[i..end])); i = end; continue;
            }
            if (html.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
            { var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal); i = end < 0 ? html.Length : end + 3; continue; }
            var candidate = i + 1 < html.Length && (char.IsAsciiLetter(html[i + 1]) || html[i + 1] is '!' or '?' || (html[i + 1] == '/' && i + 2 < html.Length && char.IsAsciiLetter(html[i + 2])));
            if (!candidate) { output.Append('<'); i++; continue; }
            var tagEnd = i + 1; char quote = '\0';
            for (; tagEnd < html.Length; tagEnd++)
            {
                var c = html[tagEnd];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c is '"' or '\'') quote = c;
                else if (c == '>') break;
            }
            if (tagEnd == html.Length) { output.Append(WebUtility.HtmlDecode(html[i..])); break; }
            var content = html.AsSpan(i + 1, tagEnd - i - 1).Trim();
            var closingTag = content.StartsWith("/", StringComparison.Ordinal); if (closingTag) content = content[1..].TrimStart();
            var length = 0; while (length < content.Length && !HtmlSpace(content[length]) && content[length] != '/') length++;
            var name = content[..length].ToString().ToLowerInvariant();
            if (!closingTag && name is "head" or "script" or "style" or "template") suppressed = name;
            if (blocks.Contains(name)) output.Append('\n'); else if (name is "td" or "th") output.Append('\t');
            i = tagEnd + 1;
        }
        var normalized = new StringBuilder(); var pendingSpace = false; var pendingBreak = false;
        foreach (var c in NormalizeLines(output.ToString()))
        {
            if (c == '\n') { pendingBreak = true; pendingSpace = false; continue; }
            if (char.IsWhiteSpace(c)) { pendingSpace = true; continue; }
            if (normalized.Length > 0) { if (pendingBreak) normalized.Append('\n'); else if (pendingSpace) normalized.Append(' '); }
            normalized.Append(c); pendingSpace = false; pendingBreak = false;
        }
        return normalized.ToString();
    }
    private static bool HtmlSpace(char character) => character is ' ' or '\t' or '\r' or '\n' or '\f';
    private static int FindClosingTag(string html, string name, int start)
    {
        var needle = "</" + name;
        while (start < html.Length)
        {
            var index = html.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return -1;
            var boundary = index + needle.Length;
            if (boundary < html.Length && (HtmlSpace(html[boundary]) || html[boundary] is '/' or '>'))
            {
                // 閉じ名前が合っても、終端のないタグは本文の抑制を解除しない。
                var end = boundary; char quote = '\0';
                for (; end < html.Length; end++)
                {
                    var character = html[end];
                    if (quote != '\0') { if (character == quote) quote = '\0'; }
                    else if (character is '"' or '\'') quote = character;
                    else if (character == '>') return index;
                }
                return -1;
            }
            start = boundary;
        }
        return -1;
    }

    private static async Task<string> OfficeAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, true);
        if (zip.Entries.Count > 100_000) throw new InvalidDataException("Officeコンテナーは10万エントリまでです。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries) if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("Officeコンテナーに同名エントリがあります。");
        long xmlTotal = 0;
        async Task<XDocument> Xml(string name)
        {
            token.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(name, out var entry)) throw new InvalidDataException($"必要なOffice XMLがありません: {name}");
            if (entry.Length > XmlLimit || xmlTotal > 64L * 1024 * 1024 - entry.Length) throw new InvalidDataException("Office XMLは各16 MiB、合計64 MiBまでです。");
            await using var input = entry.Open(); var bytes = await ReadBytesAsync(input, XmlLimit, token);
            if (bytes.LongLength != entry.Length) throw new InvalidDataException("Office XMLの実サイズが宣言値と異なります。");
            xmlTotal += bytes.LongLength; return ParseXml(bytes);
        }
        var output = new StringBuilder();
        if (entries.ContainsKey("word/document.xml"))
        {
            foreach (var name in entries.Keys.Where(n => n == "word/document.xml" || n == "word/footnotes.xml" || n == "word/endnotes.xml" || IsNumberedPart(n, "word/header", ".xml") || IsNumberedPart(n, "word/footer", ".xml")).OrderBy(n => n == "word/document.xml" ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal))
            { output.AppendLine($"PART {name}"); AppendParagraphs(output, await Xml(name), "http://schemas.openxmlformats.org/wordprocessingml/2006/main", token); }
        }
        else if (entries.ContainsKey("ppt/presentation.xml"))
        {
            var presentation = await Xml("ppt/presentation.xml");
            var p = presentation.Root?.Name.Namespace ?? XNamespace.None;
            if (p.NamespaceName is not ("http://schemas.openxmlformats.org/presentationml/2006/main" or "http://purl.oclc.org/ooxml/presentationml/main"))
                throw new NotSupportedException("PPTXプレゼンテーションの名前空間に対応していません。");
            var relationshipNamespace = XNamespace.Get(p.NamespaceName.StartsWith("http://purl.oclc.org/", StringComparison.Ordinal)
                ? "http://purl.oclc.org/ooxml/officeDocument/relationships" : "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            var relationships = await Xml("ppt/_rels/presentation.xml.rels");
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rel in relationships.Root?.Elements() ?? [])
            {
                var id = rel.Attribute("Id")?.Value; var target = rel.Attribute("Target")?.Value;
                if (id is null || target is null || rel.Attribute("TargetMode")?.Value == "External") continue;
                var type = rel.Attribute("Type")?.Value;
                if (type is not ("http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" or "http://purl.oclc.org/ooxml/officeDocument/relationships/slide")) continue;
                if (!targets.TryAdd(id, ResolvePart("ppt", target))) throw new InvalidDataException("PPTXのスライド参照IDが重複しています。");
            }
            var ordinal = 0;
            foreach (var slide in presentation.Root?.Element(p + "sldIdLst")?.Elements(p + "sldId") ?? [])
            {
                token.ThrowIfCancellationRequested(); var id = slide.Attribute(relationshipNamespace + "id")?.Value;
                if (id is null || !targets.TryGetValue(id, out var name)) throw new InvalidDataException("PPTXのスライド参照を解決できません。");
                output.AppendLine($"SLIDE {++ordinal}");
                AppendParagraphs(output, await Xml(name), "http://schemas.openxmlformats.org/drawingml/2006/main", token);
            }
        }
        else if (entries.ContainsKey("xl/workbook.xml"))
        {
            XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace relation = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var shared = entries.ContainsKey("xl/sharedStrings.xml") ? (await Xml("xl/sharedStrings.xml")).Descendants(s + "si").Select(e => string.Concat(e.Descendants(s + "t").Select(t => t.Value))).ToArray() : [];
            var workbook = await Xml("xl/workbook.xml"); var relationships = await Xml("xl/_rels/workbook.xml.rels");
            if (workbook.Root?.Name.Namespace != s) throw new NotSupportedException("Strict形式など、このXLSX名前空間は未対応です。");
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rel in relationships.Root?.Elements() ?? [])
            {
                var id = rel.Attribute("Id")?.Value; var target = rel.Attribute("Target")?.Value;
                if (id is null || target is null || rel.Attribute("TargetMode")?.Value == "External") continue;
                targets[id] = ResolvePart("xl", target);
            }
            foreach (var sheet in workbook.Descendants(s + "sheet"))
            {
                token.ThrowIfCancellationRequested(); output.AppendLine($"SHEET {Escape(sheet.Attribute("name")?.Value ?? "")}");
                var id = sheet.Attribute(relation + "id")?.Value;
                if (id is null || !targets.TryGetValue(id, out var target)) throw new InvalidDataException("ワークシートの参照先が不明です。");
                var worksheet = await Xml(target);
                if (worksheet.Root?.Name.Namespace != s) throw new NotSupportedException("ワークシートのXML名前空間に対応していません。");
                foreach (var cell in worksheet.Descendants(s + "c").OrderBy(CellPosition))
                {
                    token.ThrowIfCancellationRequested(); var type = cell.Attribute("t")?.Value ?? "n";
                    var value = cell.Element(s + "v")?.Value ?? "";
                    if (type == "s")
                    { if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= shared.Length) throw new InvalidDataException("共有文字列の参照番号が不正です。"); value = shared[index]; }
                    else if (type == "inlineStr") value = string.Concat(cell.Descendants(s + "t").Select(t => t.Value));
                    var formula = cell.Element(s + "f");
                    output.Append(cell.Attribute("r")?.Value ?? "?").Append(" [").Append(type is "s" or "inlineStr" ? "text" : type).Append("] ").Append(Escape(value).Replace("\n", "\\n", StringComparison.Ordinal));
                    if (formula is not null) output.Append(" FORMULA=").Append(Escape(formula.Value)).Append(" ATTRS=").Append(string.Join(' ', formula.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).Select(a => a.Name + "=" + Escape(a.Value))));
                    output.AppendLine();
                    if (output.Length > 64L * 1024 * 1024) throw new InvalidDataException("Office抽出結果が64 Mi文字を超えました。");
                }
            }
        }
        else throw new NotSupportedException("DOCX/PPTX/XLSXのOffice Open XMLコンテナーではありません。旧バイナリOffice・暗号化Officeは未対応です。");
        if (output.Length > 64L * 1024 * 1024) throw new InvalidDataException("Office抽出結果が64 Mi文字を超えました。");
        return output.ToString();
    }
    private static bool IsNumberedPart(string name, string prefix, string suffix) => PartNumber(name, prefix, suffix) >= 0;
    private static (int Row, int Column) CellPosition(XElement cell)
    {
        var address = cell.Attribute("r")?.Value;
        if (address is null) throw new InvalidDataException("セル位置rを省略したワークシートは未対応です。");
        var index = 0; var column = 0;
        while (index < address.Length && address[index] is >= 'A' and <= 'Z')
        { if (column > 16_384) throw new InvalidDataException("セル列番号が不正です。"); column = column * 26 + address[index] - 'A' + 1; index++; }
        if (column is < 1 or > 16_384 || !int.TryParse(address.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row is < 1 or > 1_048_576)
            throw new InvalidDataException("セル位置が不正です。");
        return (row, column);
    }
    private static int PartNumber(string name, string prefix, string suffix)
    { return name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal) && int.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1; }
    private static string ResolvePart(string folder, string target)
    {
        if (target.Contains('\\') || target.Contains(':')) throw new InvalidDataException("Officeパーツ参照が不正です。");
        var parts = new List<string>();
        foreach (var part in (target.StartsWith('/') ? target[1..] : folder + "/" + target).Split('/'))
        { if (part is "" or ".") continue; if (part == "..") { if (parts.Count == 0) throw new InvalidDataException("Officeパーツ参照がコンテナー外です。"); parts.RemoveAt(parts.Count - 1); } else parts.Add(part); }
        return string.Join('/', parts);
    }
    private static void AppendParagraphs(StringBuilder output, XDocument document, string namespaceName, CancellationToken token)
    {
        var expected = XNamespace.Get(namespaceName);
        var actual = document.Root?.Name.Namespace ?? XNamespace.None;
        if (actual.NamespaceName is not ("http://schemas.openxmlformats.org/wordprocessingml/2006/main" or "http://schemas.openxmlformats.org/presentationml/2006/main" or "http://schemas.openxmlformats.org/drawingml/2006/main" or "http://purl.oclc.org/ooxml/wordprocessingml/main" or "http://purl.oclc.org/ooxml/presentationml/main" or "http://purl.oclc.org/ooxml/drawingml/main"))
            throw new NotSupportedException("Office XMLの名前空間に対応していません。");
        XNamespace ns = actual.NamespaceName.StartsWith("http://purl.oclc.org/ooxml/", StringComparison.Ordinal)
            ? XNamespace.Get(namespaceName.Contains("wordprocessingml", StringComparison.Ordinal) ? "http://purl.oclc.org/ooxml/wordprocessingml/main" : "http://purl.oclc.org/ooxml/drawingml/main") : expected;
        foreach (var paragraph in document.Descendants(ns + "p"))
        {
            token.ThrowIfCancellationRequested();
            foreach (var node in paragraph.Descendants())
            { if (node.Name == ns + "t") output.Append(node.Value); else if (node.Name == ns + "tab") output.Append('\t'); else if (node.Name == ns + "br" || node.Name == ns + "cr") output.Append('\n'); }
            output.AppendLine();
            if (output.Length > 64L * 1024 * 1024) throw new InvalidDataException("Office抽出結果が64 Mi文字を超えました。");
        }
    }

    private static async Task<string> TarAsync(string path, bool metadata, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var compressed = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);
        using var gzip = compressed ? new GZipStream(file, CompressionMode.Decompress, true) : null;
        using var bounded = new BoundedReadStream(gzip ?? (Stream)file, ArchiveLimit, token);
        using var reader = new TarReader(bounded, true);
        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal); long contentTotal = 0;
        while (await reader.GetNextEntryAsync(false, token) is { } entry)
        {
            token.ThrowIfCancellationRequested();
            if (rows.Count >= 100_000 || entry.Length > EntryLimit || contentTotal > ArchiveLimit - entry.Length) throw new InvalidDataException("tarは10万エントリ、各256 MiB、非圧縮合計1 GiBまでです。");
            var hashText = "—"; long read = 0;
            if (entry.DataStream is { } data)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[64 * 1024];
                while (true)
                { var count = await data.ReadAsync(buffer, token); if (count == 0) break; read += count; if (read > EntryLimit || read > entry.Length) throw new InvalidDataException("tarエントリの内容がサイズ上限または宣言値を超えました。"); hash.AppendData(buffer.AsSpan(0, count)); }
                if (read != entry.Length) throw new InvalidDataException("tarエントリの実サイズが宣言値と異なります。");
                hashText = Convert.ToHexString(hash.GetHashAndReset());
            }
            contentTotal += read;
            var row = $"{Escape(entry.Name)}\t{entry.EntryType}\t{entry.Length}\tlink={Escape(entry.LinkName)}\tsha256={hashText}";
            if (metadata) row += $"\tmode={Convert.ToString((int)entry.Mode, 8)}\tuid={entry.Uid}\tgid={entry.Gid}\tmtime={entry.ModificationTime.UtcDateTime:O}";
            if (!rows.TryAdd(entry.Name, row)) throw new InvalidDataException("tarに同名エントリがあります。曖昧な比較を避けるため拒否します。");
        }
        return string.Join('\n', rows.Values);
    }
    private sealed class BoundedReadStream(Stream inner, long limit, CancellationToken token) : Stream
    {
        private long _read;
        private int Check(int count) { _read += count; if (_read > limit) throw new InvalidDataException("非圧縮tarストリームが1 GiBを超えました。"); return count; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); return Check(inner.Read(buffer, offset, count)); }
        public override int Read(Span<byte> buffer) { token.ThrowIfCancellationRequested(); return Check(inner.Read(buffer)); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { token.ThrowIfCancellationRequested(); return Check(await inner.ReadAsync(buffer, cancellationToken)); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() { }
    }
}

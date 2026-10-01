using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ImageReportScenarios
{
    public static async Task RunAsync(string output, string fixtures,
        Func<string, int, bool, string[], Task<CommandResult>> run, Action<string, bool, string> check, Action<string, string> skip)
    {
        var source = FindFixtures();
        var folder = Path.Combine(fixtures, "image-reports"); Directory.CreateDirectory(folder);
        var evidence = Path.Combine(output, "image-report-pixels"); Directory.CreateDirectory(evidence);
        using var expectations = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(source, "expectations.json")));
        var assets = expectations.RootElement.GetProperty("assets");
        var baseline = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateObject())
        {
            var destination = Path.Combine(folder, asset.Name); File.Copy(Path.Combine(source, asset.Name), destination, true);
            baseline[destination] = Hash(destination);
            check("image-report-fixture-" + asset.Name, baseline[destination] == asset.Value.GetProperty("fileSha256").GetString(), destination);
        }
        File.Copy(Path.Combine(source, "expectations.json"), Path.Combine(evidence, "expectations.json"), true);
        string PathOf(string name) => Path.Combine(folder, name);
        Dictionary<string, object?> Entry(string left, string right, string mode = "Image", string ancestor = "") => new()
        { ["leftPath"] = left, ["rightPath"] = right, ["basePath"] = ancestor, ["mode"] = mode,
          ["leftDescription"] = "左 & <label>", ["rightDescription"] = "右 <script>unsafe</script>" };
        string Project(string name, params Dictionary<string, object?>[] entries)
        {
            var path = PathOf(name + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { formatVersion = 1, entries, activeEntryIndex = 0 })); return path;
        }
        async Task<string> Render(string name, string project, params string[] options)
        {
            var path = PathOf(name + ".html");
            await run("image-report-" + name, 0, true, ["--report-project", project, path, .. options]);
            check(name + " HTML exists", File.Exists(path), path);
            return File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
        }
        async Task Case(string name, string left, string right, int? lf = null, int? rf = null, int threshold = 0, string mode = "Image")
        {
            var project = Project(name, Entry(left, right, mode));
            var options = new List<string>();
            if (lf.HasValue) options.AddRange(["--left-frame", lf.Value.ToString(CultureInfo.InvariantCulture), "--right-frame", rf!.Value.ToString(CultureInfo.InvariantCulture)]);
            if (threshold != 0) options.AddRange(["--threshold", threshold.ToString(CultureInfo.InvariantCulture)]);
            Verify(name, await Render(name, project, options.ToArray()), left, right, lf, rf, threshold);
        }
        // 全固定画素を通す。expectationsにframeのない拒否fixtureは後段で確認する。
        foreach (var asset in assets.EnumerateObject())
            if (asset.Value.GetProperty("frames").GetArrayLength() > 0)
                await Case("all-pixels-" + asset.Name, asset.Name, asset.Name);
        await Case("static-difference", "red.png", "green.png");
        await Case("later-frame", "same-first-left.gif", "same-first-right.gif");
        await Case("missing-right", "same-first-left.gif", "short.gif");
        await Case("missing-left", "short.gif", "same-first-left.gif", threshold: 255);
        await Case("webp-later", "lossless-left.webp", "lossless-right.webp");
        await Case("selected-first", "same-first-left.gif", "same-first-right.gif", 1, 1);
        await Case("selected-cross", "same-first-left.gif", "green.png", 2, 1);
        await Case("selected-different", "same-first-left.gif", "same-first-right.gif", 2, 1);
        await Case("transparent-update", "transparent-update.gif", "disposal-2.png", 2, 1);
        for (var f = 1; f <= 4; f++) await Case("disposal-" + f, "disposal.gif", "disposal-" + f + ".png", f, 1);
        await Case("dimensions", "red.png", "red-wide.png");
        await Case("alpha", "alpha-opaque.png", "alpha-half.png");
        await Case("alpha-threshold", "alpha-opaque.png", "alpha-half.png", threshold: 127);
        foreach (var threshold in new[] { 4, 5, 6, 255 }) await Case("threshold-" + threshold, "threshold-100.png", "threshold-105.png", threshold: threshold);
        await Case("auto", "same-first-left.gif", "same-first-right.gif", mode: "Auto");
        var portable = Project("portable", Entry("same-first-left.gif", "same-first-right.gif"));
        var zip = PathOf("portable.zip");
        await run("image-report-package", 0, true, ["--package-project", portable, zip, "--report"]);
        check("image report ZIP exists", File.Exists(zip), zip);
        if (File.Exists(zip))
        {
            var extracted = PathOf("portable-extracted"); Directory.CreateDirectory(extracted);
            ZipFile.ExtractToDirectory(zip, extracted, true);
            var htmlPath = Path.Combine(extracted, "report.files", "1.html");
            check("image report package HTML", File.Exists(htmlPath), htmlPath);
            if (File.Exists(htmlPath)) Verify("package", await File.ReadAllTextAsync(htmlPath), "same-first-left.gif", "same-first-right.gif", null, null, 0);
            var embedded = Path.Combine(extracted, "project.json");
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(embedded));
            var entry = saved.RootElement.TryGetProperty("entries", out var entries) ? entries[0] : saved.RootElement;
            foreach (var side in new[] { "leftPath", "rightPath" })
            {
                var reference = entry.GetProperty(side).GetString()!;
                check("package " + side + " relative", !Path.IsPathRooted(reference), reference);
                var original = side == "leftPath" ? "same-first-left.gif" : "same-first-right.gif";
                check("package " + side + " snapshot", Hash(Path.Combine(extracted, reference)) == baseline[PathOf(original)], reference);
            }
            Verify("package-reloaded", await Render("package-reloaded", embedded), "same-first-left.gif", "same-first-right.gif", null, null, 0);
        }
        foreach (var pair in new[] { ("disposal.gif", "disposal.gif"), ("lossless-transparent.webp", "lossless-transparent.webp"), ("short.gif", "same-first-left.gif"), ("alpha-half.png", "alpha-opaque.png"), ("red.png", "red-wide.png") })
        {
            var name = "package-" + pair.Item1;
            var project = Project(name, Entry(pair.Item1, pair.Item2)); var destination = PathOf(name + ".zip");
            await run("image-report-" + name, 0, true, ["--package-project", project, destination, "--report"]);
            check(name + " ZIP exists", File.Exists(destination), destination);
            if (!File.Exists(destination)) continue;
            using var archive = ZipFile.OpenRead(destination); var htmlEntry = archive.GetEntry("report.files/1.html");
            check(name + " HTML exists", htmlEntry is not null, destination);
            if (htmlEntry is null) continue;
            using var stream = htmlEntry.Open(); using var reader = new StreamReader(stream, Encoding.UTF8);
            var html = await reader.ReadToEndAsync(); File.WriteAllText(PathOf(name + "-extracted.html"), html);
            Verify(name, html, pair.Item1, pair.Item2, null, null, 0);
        }
        var valid = Project("valid", Entry("red.png", "green.png"));
        async Task Reject(string name, string project, string[]? options = null, string? diagnostic = null, bool package = false, string? destination = null)
        {
            destination ??= PathOf("rejected-" + name + (package ? ".zip" : ".html"));
            if (!File.Exists(destination)) File.WriteAllText(destination, "protected existing output\n");
            var before = File.ReadAllBytes(destination);
            var result = await run("image-report-reject-" + name, 2, false,
                [package ? "--package-project" : "--report-project", project, destination, .. (package ? new[] { "--report" } : Array.Empty<string>()), .. (options ?? [])]);
            check(name + " existing output retained", File.ReadAllBytes(destination).SequenceEqual(before), destination);
            check(name + " diagnostic", !string.IsNullOrWhiteSpace(result.Stderr) && (diagnostic is null || result.Stderr.Contains(diagnostic, StringComparison.Ordinal)), result.Stderr);
        }
        foreach (var option in new[] { "--left-frame", "--right-frame" })
        {
            await Reject(option + "-only", valid, [option, "1"]);
            foreach (var value in new[] { "0", "-1", "2", "abc", "1.5", "2147483648" })
                await Reject(option + "-" + value, valid, ["--left-frame", option == "--left-frame" ? value : "1", "--right-frame", option == "--right-frame" ? value : "1"]);
        }
        foreach (var value in new[] { "-1", "256", "abc", "1.5", "2147483648" }) await Reject("threshold-" + value, valid, ["--threshold", value]);
        await Reject("duplicate-threshold", valid, ["--threshold", "0", "--threshold", "1"]);
        await Reject("missing-threshold", valid, ["--threshold"]);
        await Reject("duplicate-frame", valid, ["--left-frame", "1", "--left-frame", "1", "--right-frame", "1"]);
        await Reject("missing-frame", valid, ["--left-frame"]);
        await Reject("unknown-option", valid, ["--unknown-image-option"]);
        await Reject("missing-file", Project("missing-file", Entry("missing.png", "red.png")));
        await Reject("non-image-options", Project("text", Entry("red.png", "red.png", "Text")), ["--threshold", "0"]);
        await Reject("url", Project("url", Entry("https://example.invalid/image.png", "red.png")));
        foreach (var asset in new[] { "broken.gif", "truncated.gif", "over-pixel-limit.gif", "over-frame-limit.gif", "over-work-limit.gif" })
            await Reject(asset, Project("reject-input-" + asset, Entry(asset, "red.png")), diagnostic: asset == "over-work-limit.gif" ? "復号作業量" : null);
        await Reject("selected-work", Project("selected-work", Entry("over-work-limit.gif", "red.png")), ["--left-frame", "17", "--right-frame", "1"], "復号作業量");
        await Reject("canvas", Project("canvas", Entry("canvas-wide.gif", "canvas-tall.gif")), diagnostic: "比較キャンバス");
        var oversized = PathOf("oversized.gif");
        using (var stream = File.Create(oversized)) { stream.Write(File.ReadAllBytes(PathOf("short.gif"))); stream.SetLength(64L * 1024 * 1024 + 1); }
        await Reject("input-capacity", Project("input-capacity", Entry("oversized.gif", "red.png")));
        var triple = Project("three", Entry("red.png", "green.png", ancestor: "red.png"));
        await Reject("three", triple); await Reject("three-package", triple, package: true);
        var filter = PathOf("protected-filter.html"); File.WriteAllText(filter, "name: protected\ndef: include\n");
        var protectedEntry = Entry("red.png", "green.png"); protectedEntry["fileFilterPath"] = filter;
        var protectedProject = Project("protected", protectedEntry, Entry("alpha-half.png", "red.png", ancestor: "disposal-1.png"));
        foreach (var path in new[] { PathOf("red.png"), PathOf("green.png"), PathOf("alpha-half.png"), PathOf("disposal-1.png"), filter, protectedProject })
            await Reject("protected-" + Path.GetFileName(path), protectedProject, destination: path);
        await Reject("package-protected-unselected", protectedProject, package: true, destination: PathOf("alpha-half.png"));
        var readOnly = PathOf("readonly.html"); File.WriteAllText(readOnly, "readonly output"); var attributes = File.GetAttributes(readOnly);
        try { File.SetAttributes(readOnly, attributes | FileAttributes.ReadOnly); await Reject("readonly", valid, destination: readOnly); }
        finally { File.SetAttributes(readOnly, attributes); }
        var link = PathOf("linked-output.html"); var linkParent = PathOf("linked-parent");
        var targetFolder = PathOf("link-target"); Directory.CreateDirectory(targetFolder);
        var target = Path.Combine(targetFolder, "output.html"); File.WriteAllText(target, "protected linked output");
        var linksCreated = false;
        try
        {
            File.CreateSymbolicLink(link, target);
            Directory.CreateSymbolicLink(linkParent, targetFolder);
            linksCreated = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            File.WriteAllText(Path.Combine(evidence, "link-unverified.txt"), ex.ToString());
            skip("image report link output/input/parent protection", ex.Message);
        }
        try
        {
            if (linksCreated)
            {
                await Reject("linked-output", valid, destination: link);
                await Reject("linked-input", Project("linked-input", Entry(link, "red.png")));
                await Reject("linked-parent", valid, destination: Path.Combine(linkParent, "output.html"));
            }
        }
        finally
        {
            // 今回作成したリンクだけを除去する。リンク先の成果物は保持する。
            if (new FileInfo(link).LinkTarget is not null) File.Delete(link);
            if (new DirectoryInfo(linkParent).LinkTarget is not null) Directory.Delete(linkParent);
        }
        // ノイズのbase64だけで32MiBを超える。PNG入力自体は64MiB/16M画素未満。
        var noise = PathOf("capacity-noise.png"); WriteNoisePng(noise, 2600, 2600);
        await Reject("body-capacity", Project("capacity", Entry("capacity-noise.png", "capacity-noise.png")), diagnostic: "32");
        await Reject("body-capacity-package", PathOf("capacity.json"), diagnostic: "32", package: true);
        foreach (var (path, hash) in baseline) check("image-report-source-preserved-" + Path.GetFileName(path), Hash(path) == hash, path);

        void Verify(string name, string html, string left, string right, int? lf, int? rf, int threshold)
        {
            var lframes = assets.GetProperty(left).GetProperty("frames"); var rframes = assets.GetProperty(right).GetProperty("frames");
            var rows = Regex.Matches(html, @"<tr\b(?<attrs>[^>]*)>(?<content>[\s\S]*?)</tr>", RegexOptions.CultureInvariant)
                .Cast<Match>().Where(m => Attribute(m.Groups["attrs"].Value, "data-left-frame") != "").ToArray();
            var count = lf.HasValue ? 1 : Math.Max(lframes.GetArrayLength(), rframes.GetArrayLength());
            check(name + " frame rows", rows.Length == count, $"expected={count}; actual={rows.Length}");
            var anyDifferent = false;
            for (var i = 0; i < Math.Min(rows.Length, count); i++)
            {
                var ln = lf ?? i + 1; var rn = rf ?? i + 1;
                JsonElement? l = ln <= lframes.GetArrayLength() ? lframes[ln - 1] : null;
                JsonElement? r = rn <= rframes.GetArrayLength() ? rframes[rn - 1] : null;
                var (mask, width, height, changed) = Mask(l, r, threshold); anyDifferent |= changed != 0;
                var attrs = rows[i].Groups["attrs"].Value; var prefix = name + "-row-" + i;
                check(prefix + " metadata", Attribute(attrs, "data-left-frame") == (l.HasValue ? ln.ToString(CultureInfo.InvariantCulture) : "none")
                    && Attribute(attrs, "data-right-frame") == (r.HasValue ? rn.ToString(CultureInfo.InvariantCulture) : "none")
                    && Attribute(attrs, "data-different-pixels") == changed.ToString(CultureInfo.InvariantCulture)
                    && Attribute(attrs, "data-total-pixels") == ((long)width * height).ToString(CultureInfo.InvariantCulture), attrs);
                var images = Regex.Matches(rows[i].Groups["content"].Value, @"<img\b(?<attrs>[^>]*)>", RegexOptions.CultureInvariant).Cast<Match>().ToArray();
                check(prefix + " image count", images.Length == 1 + (l.HasValue ? 1 : 0) + (r.HasValue ? 1 : 0), images.Length.ToString(CultureInfo.InvariantCulture));
                foreach (var side in new[] { "left", "right", "difference" })
                {
                    var frame = side == "left" ? l : r;
                    var expected = side == "difference" ? mask : frame.HasValue ? Convert.FromHexString(frame.Value.GetProperty("bgraHex").GetString()!) : null;
                    var matches = images.Where(m => Attribute(m.Groups["attrs"].Value, "data-side") == side).ToArray();
                    check(prefix + " " + side + " presence", matches.Length == (expected is null ? 0 : 1), "");
                    if (expected is null || matches.Length != 1) continue;
                    var a = matches[0].Groups["attrs"].Value;
                    try
                    {
                        var uri = Attribute(a, "src");
                        if (!uri.StartsWith("data:image/png;base64,", StringComparison.Ordinal)) throw new InvalidDataException("PNG data URI required");
                        var png = Convert.FromBase64String(uri[22..]); var decoded = DecodePng(png);
                        var ew = side == "difference" ? width : frame!.Value.GetProperty("width").GetInt32();
                        var eh = side == "difference" ? height : frame!.Value.GetProperty("height").GetInt32();
                        check(prefix + " " + side + " independent pixels", decoded.Width == ew && decoded.Height == eh && decoded.Bgra.SequenceEqual(expected), Convert.ToHexString(SHA256.HashData(decoded.Bgra)));
                        check(prefix + " " + side + " image metadata", Attribute(a, "data-width") == ew.ToString(CultureInfo.InvariantCulture)
                            && Attribute(a, "data-height") == eh.ToString(CultureInfo.InvariantCulture)
                            && Attribute(a, "data-pixel-sha256").Equals(Convert.ToHexString(SHA256.HashData(expected)), StringComparison.OrdinalIgnoreCase)
                            && (side == "difference" || Attribute(a, "data-frame") == (side == "left" ? ln : rn).ToString(CultureInfo.InvariantCulture)), a);
                        File.WriteAllBytes(Path.Combine(evidence, prefix + "-" + side + ".png"), png);
                        File.WriteAllBytes(Path.Combine(evidence, prefix + "-" + side + ".bgra"), decoded.Bgra);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or IOException)
                    { check(prefix + " " + side + " decode", false, ex.Message); }
                }
            }
            var body = Regex.Match(html, @"<body\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups["attrs"].Value;
            check(name + " body metadata", Attribute(body, "data-mode") == "Image" && Attribute(body, "data-different") == (anyDifferent ? "true" : "false")
                && Attribute(body, "data-frame-mode") == (lf.HasValue ? "selected" : "all")
                && Attribute(body, "data-left-frames") == lframes.GetArrayLength().ToString(CultureInfo.InvariantCulture)
                && Attribute(body, "data-right-frames") == rframes.GetArrayLength().ToString(CultureInfo.InvariantCulture)
                && Attribute(body, "data-threshold") == threshold.ToString(CultureInfo.InvariantCulture), body);
            check(name + " standalone", html.Contains("<!doctype html>", StringComparison.OrdinalIgnoreCase)
                && !Regex.IsMatch(html, @"\b(?:src|href)\s*=\s*[""'](?!data:image/png;base64,|#)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                && !html.Contains("url(", StringComparison.OrdinalIgnoreCase) && !html.Contains("@import", StringComparison.OrdinalIgnoreCase), "");
            check(name + " escaped descriptions", html.Contains("&lt;label&gt;", StringComparison.Ordinal)
                && html.Contains("&lt;script&gt;unsafe&lt;/script&gt;", StringComparison.Ordinal) && !html.Contains("<script>unsafe", StringComparison.Ordinal), "");
        }
    }

    private static string Attribute(string tag, string name) => WebUtility.HtmlDecode(Regex.Match(tag,
        @"\b" + Regex.Escape(name) + "=[\"'](?<v>[^\"']*)[\"']", RegexOptions.CultureInvariant).Groups["v"].Value);

    private static (byte[] Bgra, int Width, int Height, long Changed) Mask(JsonElement? left, JsonElement? right, int threshold)
    {
        var lw = left?.GetProperty("width").GetInt32() ?? 0; var lh = left?.GetProperty("height").GetInt32() ?? 0;
        var rw = right?.GetProperty("width").GetInt32() ?? 0; var rh = right?.GetProperty("height").GetInt32() ?? 0;
        var width = Math.Max(lw, rw); var height = Math.Max(lh, rh); var result = new byte[width * height * 4]; long changed = 0;
        // 手指定RGBAの成分差から算出し、アプリの比較JSON/decoderは参照しない。
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var different = x >= lw || y >= lh || x >= rw || y >= rh;
            if (!different)
            {
                var l = left!.Value.GetProperty("rgba")[y * lw + x]; var r = right!.Value.GetProperty("rgba")[y * rw + x];
                for (var c = 0; c < 4; c++) different |= Math.Abs(l[c].GetInt32() - r[c].GetInt32()) > threshold;
            }
            if (different) changed++;
            var p = (y * width + x) * 4; result[p] = result[p + 1] = different ? (byte)80 : (byte)28;
            result[p + 2] = different ? (byte)255 : (byte)28; result[p + 3] = 255;
        }
        return (result, width, height, changed);
    }

    // 独立BCL decoder。PNG CRC/各行filterを確認し、非interlace 8bitの標準色形式を復号する。
    private static (int Width, int Height, byte[] Bgra) DecodePng(byte[] png)
    {
        if (!png.AsSpan(0, Math.Min(8, png.Length)).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("PNG signature");
        int width = 0, height = 0, color = -1; byte[] palette = [], transparency = []; using var idat = new MemoryStream();
        var ended = false;
        for (var p = 8; p + 12 <= png.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p, 4)));
            if (length > png.Length - p - 12) throw new InvalidDataException("PNG chunk length");
            var type = Encoding.ASCII.GetString(png, p + 4, 4); var data = png.AsSpan(p + 8, length);
            if (Crc(png.AsSpan(p + 4, length + 4)) != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p + 8 + length, 4))) throw new InvalidDataException("PNG CRC");
            if (type == "IHDR")
            {
                if (length != 13 || data[8] != 8 || data[10] != 0 || data[11] != 0 || data[12] != 0) throw new InvalidDataException("PNG format");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data)); height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..])); color = data[9];
            }
            else if (type == "PLTE") palette = data.ToArray(); else if (type == "tRNS") transparency = data.ToArray();
            else if (type == "IDAT") idat.Write(data); else if (type == "IEND") { ended = true; break; }
            p += length + 12;
        }
        if (!ended || width <= 0 || height <= 0 || (long)width * height > 16 * 1024 * 1024) throw new InvalidDataException("PNG dimensions/end");
        var channels = color switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new InvalidDataException("PNG color") };
        var stride = checked(width * channels); var raw = new byte[checked((stride + 1) * height)]; idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress, true)) { z.ReadExactly(raw); if (z.ReadByte() != -1) throw new InvalidDataException("PNG extra scanline"); }
        var pixels = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++) for (var x = 0; x < stride; x++)
        {
            var a = x >= channels ? pixels[y * stride + x - channels] : 0; var b = y > 0 ? pixels[(y - 1) * stride + x] : 0;
            var c = y > 0 && x >= channels ? pixels[(y - 1) * stride + x - channels] : 0;
            var predictor = raw[y * (stride + 1)] switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => throw new InvalidDataException("PNG filter") };
            pixels[y * stride + x] = unchecked((byte)(raw[y * (stride + 1) + x + 1] + predictor));
        }
        var bgra = new byte[checked(width * height * 4)];
        for (var i = 0; i < width * height; i++)
        {
            var p = i * channels; byte r, g, b, alpha = 255;
            if (color == 3) { var index = pixels[p]; if (index * 3 + 2 >= palette.Length) throw new InvalidDataException("PNG palette"); r = palette[index * 3]; g = palette[index * 3 + 1]; b = palette[index * 3 + 2]; if (index < transparency.Length) alpha = transparency[index]; }
            else if (color is 0 or 4) { r = g = b = pixels[p]; if (color == 4) alpha = pixels[p + 1]; else if (transparency.Length == 2 && pixels[p] == BinaryPrimitives.ReadUInt16BigEndian(transparency)) alpha = 0; }
            else { r = pixels[p]; g = pixels[p + 1]; b = pixels[p + 2]; if (color == 6) alpha = pixels[p + 3]; else if (transparency.Length == 6 && r == BinaryPrimitives.ReadUInt16BigEndian(transparency) && g == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) && b == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4))) alpha = 0; }
            bgra[i * 4] = b; bgra[i * 4 + 1] = g; bgra[i * 4 + 2] = r; bgra[i * 4 + 3] = alpha;
        }
        return (width, height, bgra);
    }
    private static int Paeth(int a, int b, int c) { var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
    private static uint Crc(ReadOnlySpan<byte> bytes) { uint crc = uint.MaxValue; foreach (var b in bytes) { crc ^= b; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); } return ~crc; }
    private static void WriteNoisePng(string path, int width, int height)
    {
        using var file = File.Create(path); file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height); header[8] = 8; header[9] = 6; Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var row = new byte[width * 4 + 1]; uint state = 0x12345678;
            for (var y = 0; y < height; y++) { for (var i = 1; i < row.Length; i++) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; row[i] = (byte)state; } z.Write(row); }
        }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        void Chunk(string type, byte[] data)
        {
            var bytes = new byte[data.Length + 4]; Encoding.ASCII.GetBytes(type).CopyTo(bytes, 0); data.CopyTo(bytes, 4);
            var number = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); file.Write(number); file.Write(bytes); BinaryPrimitives.WriteUInt32BigEndian(number, Crc(bytes)); file.Write(number);
        }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string FindFixtures()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        { var path = Path.Combine(d.FullName, "tests", "Fixtures", "Images"); if (File.Exists(Path.Combine(path, "expectations.json"))) return path; }
        throw new FileNotFoundException("Image fixtures not found");
    }
}

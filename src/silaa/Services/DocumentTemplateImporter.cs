using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using silaa.Core;

namespace silaa.Services;

public static partial class DocumentTemplateImporter
{
    public static DocumentDesignTemplate Import(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("템플릿으로 가져올 문서를 찾지 못했습니다.", path);
        if (new FileInfo(path).Length > 100 * 1024 * 1024)
            throw new InvalidOperationException("100MB보다 큰 문서는 템플릿으로 가져올 수 없습니다.");

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var format = extension switch
        {
            ".docx" => "docx",
            ".pptx" => "pptx",
            ".html" or ".htm" => "html",
            _ => throw new NotSupportedException("DOCX, PPTX, HTML 파일만 디자인 템플릿으로 가져올 수 있습니다.")
        };
        var source = format == "html" ? File.ReadAllText(path) : ReadPackageXml(path);
        var fallback = DocumentTemplateStore.BuiltInOrDefault(format, null);
        var colors = HexColorRegex().Matches(source)
            .Select(match => match.Groups[1].Value.ToUpperInvariant())
            .Where(IsColor)
            .Distinct(StringComparer.Ordinal)
            .Take(24)
            .ToList();

        string Css(string name, string fallbackValue)
        {
            var match = Regex.Match(source, $@"--{Regex.Escape(name)}\s*:\s*#([0-9a-fA-F]{{6}})", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : fallbackValue;
        }

        var background = format == "html" ? Css("bg", fallback.Background) : format == "pptx" ? At(colors, 0, fallback.Background) : fallback.Background;
        var accent = format == "html" ? Css("accent", fallback.AccentColor) : MostSaturated(colors, fallback.AccentColor, background);
        var title = format == "html" ? Css("title", fallback.TitleColor) : format == "pptx" ? At(colors, 2, Darkest(colors, fallback.TitleColor)) : At(colors, 0, fallback.TitleColor);
        var body = format == "html" ? Css("body", fallback.BodyColor) : format == "pptx" ? At(colors, 3, title) : At(colors, 1, fallback.BodyColor);
        var muted = format == "html" ? Css("muted", fallback.MutedColor) : At(colors, 4, fallback.MutedColor);
        var font = ExtractFont(source, fallback.FontFamily);
        var layout = source.Contains("grid", StringComparison.OrdinalIgnoreCase) || source.Contains("<w:tbl", StringComparison.OrdinalIgnoreCase)
            ? "grid"
            : font.Contains("Georgia", StringComparison.OrdinalIgnoreCase) || font.Contains("serif", StringComparison.OrdinalIgnoreCase)
                ? "editorial"
                : "balanced";
        var name = Path.GetFileNameWithoutExtension(path).Trim();
        if (name.Length == 0) name = "가져온 문서 디자인";

        return new DocumentDesignTemplate(
            name, name, $"{Path.GetFileName(path)}에서 가져온 {FormatLabel(format)} 디자인",
            background, title, body, accent, muted, font, layout, false, format);
    }

    private static string ReadPackageXml(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var output = new StringBuilder();
        foreach (var entry in archive.Entries
                     .Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) ? 0
                         : entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ? 1 : 2))
        {
            if (entry.Length > 8 * 1024 * 1024 || output.Length > 16 * 1024 * 1024) continue;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
            output.AppendLine(reader.ReadToEnd());
        }
        return output.ToString();
    }

    private static string ExtractFont(string source, string fallback)
    {
        var typeface = Regex.Match(source, "typeface=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        if (typeface.Success && !string.IsNullOrWhiteSpace(typeface.Groups[1].Value)) return typeface.Groups[1].Value;
        var css = Regex.Match(source, "font-family\\s*:\\s*[\"']?([^,;\"']+)", RegexOptions.IgnoreCase);
        return css.Success && !string.IsNullOrWhiteSpace(css.Groups[1].Value) ? css.Groups[1].Value.Trim() : fallback;
    }

    private static string At(IReadOnlyList<string> colors, int index, string fallback) =>
        index >= 0 && index < colors.Count ? colors[index] : fallback;

    private static string Darkest(IEnumerable<string> colors, string fallback) =>
        colors.OrderBy(Luminance).FirstOrDefault() ?? fallback;

    private static string MostSaturated(IEnumerable<string> colors, string fallback, string background) =>
        colors.Where(color => color != background).OrderByDescending(Saturation).FirstOrDefault() ?? fallback;

    private static double Luminance(string color)
    {
        var (red, green, blue) = Rgb(color);
        return red * .2126 + green * .7152 + blue * .0722;
    }

    private static double Saturation(string color)
    {
        var (red, green, blue) = Rgb(color);
        return Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
    }

    private static (int Red, int Green, int Blue) Rgb(string color) =>
        (Convert.ToInt32(color[..2], 16), Convert.ToInt32(color[2..4], 16), Convert.ToInt32(color[4..6], 16));

    private static bool IsColor(string value) => value.Length == 6 && value.All(Uri.IsHexDigit);

    private static string FormatLabel(string format) => format switch { "docx" => "Docs", "pptx" => "PPT", _ => "HTML" };

    [GeneratedRegex("(?:#|val=\"|lastClr=\")([0-9a-fA-F]{6})(?:\"|\\b)", RegexOptions.IgnoreCase)]
    private static partial Regex HexColorRegex();
}

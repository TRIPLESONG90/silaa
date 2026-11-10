using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Writer;
using silaa.Core;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace silaa.Services;

internal static class DocumentArtifactService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".html", ".docx", ".xlsx", ".pptx", ".pdf"
    };

    public static bool Supports(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    public static string ReadText(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("문서 파일을 찾을 수 없습니다.", path);
        if (new FileInfo(path).Length > 100 * 1024 * 1024)
            throw new InvalidOperationException("100MB보다 큰 문서는 읽을 수 없습니다.");

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" => ReadWord(path),
            ".xlsx" => ReadWorkbook(path),
            ".pptx" => ReadPresentation(path),
            ".pdf" => ReadPdf(path),
            ".txt" or ".md" or ".csv" or ".json" or ".html" => File.ReadAllText(path),
            _ => throw new NotSupportedException($"지원하지 않는 문서 형식입니다: {Path.GetExtension(path)}")
        };
    }

    public static string Create(
        string path, string format, string title, string content,
        string template = "default", DocumentDesignTemplate? design = null)
    {
        var extension = NormalizeExtension(format, path);
        path = EnsureExtension(Path.GetFullPath(path), extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) throw new IOException("같은 이름의 문서가 이미 있습니다. 다른 이름을 사용하세요.");

        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            switch (extension)
            {
                case ".docx": CreateWord(temp, title, content, design ?? DocumentTemplateStore.BuiltInOrDefault("docx", template)); break;
                case ".xlsx": CreateWorkbook(temp, content); break;
                case ".pptx": CreatePresentation(
                    temp, title, content, design ?? DocumentTemplateStore.BuiltInOrDefault("pptx", template)); break;
                case ".html": CreateHtml(temp, title, content, design ?? DocumentTemplateStore.BuiltInOrDefault("html", template)); break;
                case ".pdf": CreatePdf(temp, title, content); break;
                case ".txt" or ".md" or ".csv" or ".json":
                    File.WriteAllText(temp, content, new UTF8Encoding(false));
                    break;
                default: throw new NotSupportedException($"지원하지 않는 문서 형식입니다: {extension}");
            }
            File.Move(temp, path);
            return path;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string ReadWord(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart?.Document?.Body;
        return body is null ? "" : string.Join('\n', body.Descendants<W.Paragraph>()
            .Select(paragraph => paragraph.InnerText).Where(text => text.Length > 0));
    }

    private static string ReadWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbook = document.WorkbookPart;
        if (workbook is null) return "";
        var shared = workbook.SharedStringTablePart?.SharedStringTable?
            .Elements<S.SharedStringItem>().Select(item => item.InnerText).ToArray() ?? [];
        var output = new StringBuilder();
        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<S.Sheet>() ?? [])
        {
            output.Append("# Sheet: ").AppendLine(sheet.Name?.Value ?? "Sheet");
            if (sheet.Id?.Value is null || workbook.GetPartById(sheet.Id.Value) is not WorksheetPart worksheet) continue;
            foreach (var row in worksheet.Worksheet?.Descendants<S.Row>() ?? [])
            {
                var values = row.Elements<S.Cell>().Select(cell => CellValue(cell, shared));
                output.AppendLine(string.Join('\t', values));
            }
            output.AppendLine();
        }
        return output.ToString().TrimEnd();
    }

    private static string CellValue(S.Cell cell, IReadOnlyList<string> shared)
    {
        if (cell.DataType?.Value == S.CellValues.InlineString) return cell.InlineString?.InnerText ?? "";
        var value = cell.CellValue?.Text ?? "";
        return cell.DataType?.Value == S.CellValues.SharedString && int.TryParse(value, out var index) && index >= 0 && index < shared.Count
            ? shared[index] : value;
    }

    private static string ReadPresentation(string path)
    {
        using var document = PresentationDocument.Open(path, false);
        var part = document.PresentationPart;
        if (part?.Presentation?.SlideIdList is null) return "";
        var output = new StringBuilder();
        var index = 0;
        foreach (var slideId in part.Presentation.SlideIdList.Elements<P.SlideId>())
        {
            if (slideId.RelationshipId?.Value is null || part.GetPartById(slideId.RelationshipId.Value) is not SlidePart slide) continue;
            output.Append("# Slide ").AppendLine((++index).ToString(CultureInfo.InvariantCulture));
            foreach (var paragraph in slide.Slide?.Descendants<A.Paragraph>() ?? [])
            {
                var text = string.Concat(paragraph.Descendants<A.Text>().Select(item => item.Text));
                if (text.Length > 0) output.AppendLine(text);
            }
            output.AppendLine();
        }
        return output.ToString().TrimEnd();
    }

    private static string ReadPdf(string path)
    {
        using var document = PdfDocument.Open(path);
        var output = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            output.Append("# Page ").AppendLine(page.Number.ToString(CultureInfo.InvariantCulture));
            output.AppendLine(ContentOrderTextExtractor.GetText(page)).AppendLine();
        }
        var result = output.ToString().TrimEnd();
        return result.Length == 0 ? "텍스트를 추출하지 못했습니다. 스캔 이미지 PDF일 수 있습니다." : result;
    }

    private static void CreateWord(string path, string title, string content, DocumentDesignTemplate design)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new W.Body();
        if (!string.IsNullOrWhiteSpace(title)) body.Append(WordParagraph(title.Trim(), "Title", design));
        foreach (var line in NormalizeLines(content))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal)) body.Append(WordParagraph(line[2..], "Heading1", design));
            else if (line.StartsWith("## ", StringComparison.Ordinal)) body.Append(WordParagraph(line[3..], "Heading2", design));
            else body.Append(WordParagraph(line, null, design));
        }
        main.Document = new W.Document(body);
        main.Document.Save();
    }

    private static W.Paragraph WordParagraph(string text, string? style, DocumentDesignTemplate design)
    {
        var title = style == "Title";
        var heading = title || style is "Heading1" or "Heading2";
        var paragraph = new W.Paragraph(new W.ParagraphProperties(
            new W.SpacingBetweenLines
            {
                Before = title ? "200" : heading ? "260" : "80",
                After = title ? "320" : heading ? "140" : "100",
                Line = "300",
                LineRule = W.LineSpacingRuleValues.Auto
            }));
        var properties = new W.RunProperties(
            new W.RunFonts { Ascii = design.FontFamily, HighAnsi = design.FontFamily, EastAsia = "Malgun Gothic" },
            new W.Color { Val = heading ? design.TitleColor : design.BodyColor },
            new W.FontSize { Val = title ? "44" : style == "Heading1" ? "32" : style == "Heading2" ? "26" : "21" });
        if (heading) properties.Append(new W.Bold());
        paragraph.Append(new W.Run(properties, new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return paragraph;
    }

    private static void CreateWorkbook(string path, string content)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        var sheetData = new S.SheetData();
        foreach (var sourceRow in ParseDelimited(content))
        {
            var row = new S.Row();
            foreach (var value in sourceRow)
                row.Append(new S.Cell { DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(value)) });
            sheetData.Append(row);
        }
        worksheet.Worksheet = new S.Worksheet(sheetData);
        workbook.Workbook = new S.Workbook(new S.Sheets(
            new S.Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1U, Name = "Sheet1" }));
        workbook.Workbook.Save();
    }

    private static IEnumerable<string[]> ParseDelimited(string content)
    {
        foreach (var line in NormalizeLines(content))
        {
            var values = new List<string>();
            var current = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (ch == '"') quoted = !quoted;
                else if (ch == ',' && !quoted) { values.Add(current.ToString()); current.Clear(); }
                else current.Append(ch);
            }
            values.Add(current.ToString());
            yield return values.ToArray();
        }
    }

    private static void CreatePresentation(
        string path, string title, string content, DocumentDesignTemplate design)
    {
        var slides = ParseSlides(title, content);
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentationPart = document.AddPresentationPart();
        presentationPart.Presentation = new P.Presentation(
            new P.SlideMasterIdList(),
            new P.SlideIdList(),
            new P.SlideSize { Cx = 12192000, Cy = 6858000, Type = P.SlideSizeValues.Screen16x9 },
            new P.NotesSize { Cx = 6858000, Cy = 9144000 },
            new P.DefaultTextStyle());

        var slideLayoutPart = AddPresentationInfrastructure(presentationPart);

        uint id = 256;
        for (var index = 0; index < slides.Count; index++)
        {
            var slide = slides[index];
            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.Slide = BuildSlide(slide.Title, slide.Body, design, index == 0);
            slidePart.AddPart(slideLayoutPart);
            presentationPart.Presentation.SlideIdList!.Append(new P.SlideId
            {
                Id = id++, RelationshipId = presentationPart.GetIdOfPart(slidePart)
            });
            slidePart.Slide.Save();
        }
        presentationPart.Presentation.Save();
    }

    private static SlideLayoutPart AddPresentationInfrastructure(PresentationPart presentationPart)
    {
        var masterPart = presentationPart.AddNewPart<SlideMasterPart>();
        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
        var themePart = masterPart.AddNewPart<ThemePart>();

        layoutPart.SlideLayout = new P.SlideLayout(
            new P.CommonSlideData(EmptyShapeTree()) { Name = "Blank" },
            new P.ColorMapOverride(new A.MasterColorMapping()))
        {
            Type = P.SlideLayoutValues.Blank,
            Preserve = true
        };

        masterPart.SlideMaster = new P.SlideMaster(
            new P.CommonSlideData(EmptyShapeTree()),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
            },
            new P.SlideLayoutIdList(new P.SlideLayoutId
            {
                Id = 2147483649U,
                RelationshipId = masterPart.GetIdOfPart(layoutPart)
            }),
            new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));

        layoutPart.AddPart(masterPart);
        themePart.Theme = BuildPresentationTheme();
        presentationPart.Presentation!.SlideMasterIdList!.Append(new P.SlideMasterId
        {
            Id = 2147483648U,
            RelationshipId = presentationPart.GetIdOfPart(masterPart)
        });
        layoutPart.SlideLayout.Save();
        masterPart.SlideMaster.Save();
        themePart.Theme.Save();
        return layoutPart;
    }

    private static P.ShapeTree EmptyShapeTree() => new(
        new P.NonVisualGroupShapeProperties(
            new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
            new P.NonVisualGroupShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.GroupShapeProperties(new A.TransformGroup()));

    private static A.Theme BuildPresentationTheme()
    {
        static A.SolidFill SchemeFill() => new(new A.SchemeColor { Val = A.SchemeColorValues.PhColor });
        static A.Outline Outline() => new(SchemeFill(), new A.PresetDash { Val = A.PresetLineDashValues.Solid })
        {
            Width = 9525,
            CapType = A.LineCapValues.Flat,
            CompoundLineType = A.CompoundLineValues.Single,
            Alignment = A.PenAlignmentValues.Center
        };

        var colors = new A.ColorScheme(
            new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
            new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
            new A.Dark2Color(new A.RgbColorModelHex { Val = "1C1C1C" }),
            new A.Light2Color(new A.RgbColorModelHex { Val = "F3F3F1" }),
            new A.Accent1Color(new A.RgbColorModelHex { Val = "111111" }),
            new A.Accent2Color(new A.RgbColorModelHex { Val = "525252" }),
            new A.Accent3Color(new A.RgbColorModelHex { Val = "767676" }),
            new A.Accent4Color(new A.RgbColorModelHex { Val = "969696" }),
            new A.Accent5Color(new A.RgbColorModelHex { Val = "B8B8B8" }),
            new A.Accent6Color(new A.RgbColorModelHex { Val = "D9D9D9" }),
            new A.Hyperlink(new A.RgbColorModelHex { Val = "1F5AA6" }),
            new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "6B438B" }))
        { Name = "silaa monochrome" };
        var fonts = new A.FontScheme(
            new A.MajorFont(
                new A.LatinFont { Typeface = "Aptos Display" },
                new A.EastAsianFont { Typeface = "Malgun Gothic" },
                new A.ComplexScriptFont { Typeface = "Arial" }),
            new A.MinorFont(
                new A.LatinFont { Typeface = "Aptos" },
                new A.EastAsianFont { Typeface = "Malgun Gothic" },
                new A.ComplexScriptFont { Typeface = "Arial" }))
        { Name = "silaa" };
        var format = new A.FormatScheme(
            new A.FillStyleList(SchemeFill(), SchemeFill(), SchemeFill()),
            new A.LineStyleList(Outline(), Outline(), Outline()),
            new A.EffectStyleList(
                new A.EffectStyle(new A.EffectList()),
                new A.EffectStyle(new A.EffectList()),
                new A.EffectStyle(new A.EffectList())),
            new A.BackgroundFillStyleList(SchemeFill(), SchemeFill(), SchemeFill()))
        { Name = "silaa" };
        return new A.Theme(
            new A.ThemeElements(colors, fonts, format),
            new A.ObjectDefaults(),
            new A.ExtraColorSchemeList())
        { Name = "silaa" };
    }

    private static P.Slide BuildSlide(
        string title, IReadOnlyList<string> body, DocumentDesignTemplate design, bool isTitleSlide)
    {
        var tree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup(
                new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = 0L, Cy = 0L },
                new A.ChildOffset { X = 0L, Y = 0L }, new A.ChildExtents { Cx = 0L, Cy = 0L })));
        tree.Append(SolidShape(2U, "Background", 0L, 0L, 12192000L, 6858000L, design.Background));

        var titleX = design.Layout == "editorial" ? 980000L : 760000L;
        var titleY = isTitleSlide ? 2050000L : 680000L;
        var titleWidth = design.Layout == "grid" ? 7600000L : 10500000L;
        var titleSize = isTitleSlide ? 3600 : 2850;
        if (design.Layout == "grid")
            tree.Append(SolidShape(3U, "Grid accent", 0L, 0L, 250000L, 6858000L, design.AccentColor));
        else
            tree.Append(SolidShape(3U, "Accent", titleX, titleY - 260000L, isTitleSlide ? 900000L : 520000L, 65000L, design.AccentColor));

        tree.Append(TextShape(4U, "Title", title, titleX, titleY, titleWidth, isTitleSlide ? 1500000L : 900000L,
            titleSize, design.TitleColor, true, design.FontFamily));
        tree.Append(TextShape(5U, "Body", string.Join('\n', body.Select(line => line.TrimStart('-', '•', ' '))),
            titleX + (design.Layout == "editorial" ? 850000L : 90000L), isTitleSlide ? 3900000L : 1800000L,
            design.Layout == "grid" ? 7200000L : 9600000L, isTitleSlide ? 1600000L : 3900000L,
            isTitleSlide ? 1650 : 1750, design.BodyColor, false, design.FontFamily));
        tree.Append(TextShape(6U, "Brand", "silaa  /  " + design.Name.ToUpperInvariant(),
            9400000L, 6250000L, 1900000L, 260000L, 780, design.MutedColor, false, design.FontFamily));
        return new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping()));
    }

    private static P.Shape SolidShape(
        uint id, string name, long x, long y, long width, long height, string color) =>
        new(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = width, Cy = height }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                new A.SolidFill(new A.RgbColorModelHex { Val = color })));

    private static P.Shape TextShape(
        uint id, string name, string text, long x, long y, long width, long height,
        int fontSize, string color, bool bold, string fontFamily)
    {
        var paragraphs = new List<A.Paragraph>();
        foreach (var line in NormalizeLines(text).DefaultIfEmpty(""))
        {
            var runProperties = new A.RunProperties { Language = "ko-KR", FontSize = fontSize, Bold = bold };
            runProperties.Append(new A.SolidFill(new A.RgbColorModelHex { Val = color }));
            runProperties.Append(new A.LatinFont { Typeface = fontFamily });
            runProperties.Append(new A.EastAsianFont { Typeface = "Malgun Gothic" });
            paragraphs.Add(new A.Paragraph(
                new A.Run(runProperties, new A.Text(line)),
                new A.EndParagraphRunProperties { Language = "ko-KR" }));
        }
        var textBody = new P.TextBody(new A.BodyProperties { Wrap = A.TextWrappingValues.Square }, new A.ListStyle());
        textBody.Append(paragraphs);
        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = width, Cy = height }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                new A.NoFill()),
            textBody);
    }

    private static IReadOnlyList<(string Title, IReadOnlyList<string> Body)> ParseSlides(string title, string content)
    {
        var sections = content.Replace("\r\n", "\n").Split("\n---\n", StringSplitOptions.None);
        var slides = new List<(string, IReadOnlyList<string>)>();
        foreach (var section in sections)
        {
            var lines = NormalizeLines(section).Where(line => line.Length > 0).ToList();
            if (lines.Count == 0) continue;
            slides.Add((lines[0].TrimStart('#', ' '), lines.Skip(1).ToArray()));
        }
        if (slides.Count == 0) slides.Add((string.IsNullOrWhiteSpace(title) ? "새 프레젠테이션" : title.Trim(), Array.Empty<string>()));
        else if (!string.IsNullOrWhiteSpace(title) && !string.Equals(slides[0].Item1, title.Trim(), StringComparison.Ordinal))
            slides.Insert(0, (title.Trim(), ["silaa로 만든 프레젠테이션"]));
        return slides;
    }

    private static void CreateHtml(string path, string title, string content, DocumentDesignTemplate design)
    {
        var body = new StringBuilder();
        var inList = false;
        foreach (var raw in NormalizeLines(content))
        {
            var line = raw.Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("• ", StringComparison.Ordinal))
            {
                if (!inList) { body.AppendLine("<ul>"); inList = true; }
                body.Append("<li>").Append(System.Net.WebUtility.HtmlEncode(line[2..].Trim())).AppendLine("</li>");
                continue;
            }
            if (inList) { body.AppendLine("</ul>"); inList = false; }
            if (line.Length == 0) continue;
            if (line.StartsWith("# ", StringComparison.Ordinal))
                body.Append("<h1>").Append(System.Net.WebUtility.HtmlEncode(line[2..])).AppendLine("</h1>");
            else if (line.StartsWith("## ", StringComparison.Ordinal))
                body.Append("<h2>").Append(System.Net.WebUtility.HtmlEncode(line[3..])).AppendLine("</h2>");
            else
                body.Append("<p>").Append(System.Net.WebUtility.HtmlEncode(line)).AppendLine("</p>");
        }
        if (inList) body.AppendLine("</ul>");

        var encodedTitle = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(title) ? "문서" : title.Trim());
        var html = $$"""
            <!doctype html>
            <html lang="ko">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{encodedTitle}}</title>
              <style>
                :root { color-scheme: light dark; --bg:#{{design.Background}}; --title:#{{design.TitleColor}}; --body:#{{design.BodyColor}}; --accent:#{{design.AccentColor}}; --muted:#{{design.MutedColor}}; }
                * { box-sizing:border-box; }
                body { margin:0; background:var(--bg); color:var(--body); font-family:"{{design.FontFamily}}", "Malgun Gothic", sans-serif; line-height:1.72; }
                main { width:min(880px, calc(100% - 40px)); margin:0 auto; padding:clamp(56px, 9vw, 120px) 0; }
                header { border-top:5px solid var(--accent); padding-top:32px; margin-bottom:56px; }
                .eyebrow { color:var(--muted); font-size:12px; letter-spacing:.16em; text-transform:uppercase; }
                h1 { color:var(--title); font-size:clamp(34px, 6vw, 68px); line-height:1.06; letter-spacing:-.035em; margin:12px 0 24px; }
                h2 { color:var(--title); font-size:26px; line-height:1.25; margin:48px 0 14px; }
                p, li { font-size:17px; }
                ul { padding-left:1.2em; }
                li::marker { color:var(--accent); }
                footer { margin-top:72px; padding-top:18px; border-top:1px solid color-mix(in srgb, var(--muted) 38%, transparent); color:var(--muted); font-size:12px; }
                @media print { main { width:auto; padding:24mm; } }
              </style>
            </head>
            <body>
              <main>
                <header><div class="eyebrow">SILAA · {{System.Net.WebUtility.HtmlEncode(design.Name)}}</div><h1>{{encodedTitle}}</h1></header>
                <article>{{body}}</article>
                <footer>Generated with silaa</footer>
              </main>
            </body>
            </html>
            """;
        File.WriteAllText(path, html, new UTF8Encoding(false));
    }

    private static void CreatePdf(string path, string title, string content)
    {
        var fontPath = FindFont();
        if (fontPath is null) throw new InvalidOperationException("PDF 작성에 사용할 TrueType 글꼴을 찾지 못했습니다.");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddTrueTypeFont(File.ReadAllBytes(fontPath));
        var lines = new List<(string Text, double Size)>();
        if (!string.IsNullOrWhiteSpace(title)) lines.Add((title.Trim(), 20));
        lines.AddRange(NormalizeLines(content).Select(line => (line, line.StartsWith("# ", StringComparison.Ordinal) ? 16d : 11d)));
        PdfPageBuilder? page = null;
        var y = 0d;
        foreach (var source in lines)
        {
            if (page is null || y < 55)
            {
                page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
                y = page.PageSize.Height - 55;
            }
            foreach (var line in Wrap(source.Text.TrimStart('#', ' '), source.Size >= 16 ? 48 : 78))
            {
                page.AddText(line, source.Size, new PdfPoint(52, y), font);
                y -= source.Size + 7;
            }
            y -= 5;
        }
        File.WriteAllBytes(path, builder.Build());
    }

    private static string? FindFont()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[] { "malgun.ttf", "malgunbd.ttf", "arial.ttf" };
        return candidates.Select(name => Path.Combine(windows, "Fonts", name)).FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        if (text.Length == 0) { yield return " "; yield break; }
        for (var offset = 0; offset < text.Length; offset += width)
            yield return text.Substring(offset, Math.Min(width, text.Length - offset));
    }

    private static string[] NormalizeLines(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string NormalizeExtension(string format, string path)
    {
        var value = string.IsNullOrWhiteSpace(format) ? Path.GetExtension(path) : format.Trim();
        if (!value.StartsWith('.')) value = "." + value;
        value = value.ToLowerInvariant();
        if (!SupportedExtensions.Contains(value)) throw new NotSupportedException($"지원하지 않는 문서 형식입니다: {value}");
        return value;
    }

    private static string EnsureExtension(string path, string extension) =>
        string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;
}

using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace silaa.UI;

/// <summary>
/// Streams Markdown as independently cached blocks, then consolidates the completed
/// answer into large selectable document regions. Completed tables stay inside the
/// same selection surface; charts remain separate interactive surfaces.
/// </summary>
internal sealed class MarkdownView
{
    private const int MaximumLiveBlockCharacters = 3_000;
    private readonly StringBuilder _source = new();
    private readonly StackPanel _stableBlocks = new() { Spacing = 8, IsVisible = false };
    private readonly StackPanel _liveBlocks = new() { Spacing = 8, IsVisible = false };
    private IBrush _foreground = Palette.White;
    private bool _completed;
    private int _stableSourceLength;
    private int _renderedSourceLength;

    public MarkdownView(string? text = null)
    {
        Root = new StackPanel { Spacing = 8 };
        ShowStreamingContainers();
        if (!string.IsNullOrEmpty(text))
        {
            _source.Append(text);
            _completed = true;
            ShowCompletedDocument();
        }
    }

    public StackPanel Root { get; }
    public bool HasContent => _source.Length > 0;
    public string Text => _source.ToString();

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _source.Append(text);
        if (_completed) ShowCompletedDocument();
    }

    public void SetText(string text)
    {
        _source.Clear();
        _source.Append(text);
        _completed = true;
        ShowCompletedDocument();
    }

    public void SetForeground(IBrush foreground)
    {
        _foreground = foreground;
        if (_source.Length == 0) return;
        if (_completed) ShowCompletedDocument();
        else RebuildStreamingFromScratch();
    }

    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        ShowCompletedDocument();
    }

    internal void RefreshStreamingForTest() => RefreshStreamingMarkdown();
    internal void FlushStreaming()
    {
        if (!_completed && _source.Length != _renderedSourceLength) RefreshStreamingMarkdown();
    }

    private void RefreshStreamingMarkdown()
    {
        var pending = _source.ToString(_stableSourceLength, _source.Length - _stableSourceLength);
        var stableLength = StablePrefixLength(pending);
        if (stableLength > 0)
        {
            RenderBlocksInto(_stableBlocks, pending[..stableLength]);
            _stableBlocks.IsVisible = _stableBlocks.Children.Count > 0;
            _stableSourceLength += stableLength;
            pending = pending[stableLength..];
        }

        _liveBlocks.Children.Clear();
        if (!string.IsNullOrWhiteSpace(pending))
        {
            RenderBlocksInto(_liveBlocks, LivePreview(pending));
            _liveBlocks.IsVisible = _liveBlocks.Children.Count > 0;
        }
        else
        {
            _liveBlocks.IsVisible = false;
        }
        _renderedSourceLength = _source.Length;
    }

    private void RebuildStreamingFromScratch()
    {
        _stableBlocks.Children.Clear();
        _liveBlocks.Children.Clear();
        _stableBlocks.IsVisible = false;
        _liveBlocks.IsVisible = false;
        _stableSourceLength = 0;
        _renderedSourceLength = 0;
        ShowStreamingContainers();
        RefreshStreamingMarkdown();
    }

    private void ShowStreamingContainers()
    {
        Root.Children.Clear();
        Root.Children.Add(_stableBlocks);
        Root.Children.Add(_liveBlocks);
    }

    private void ShowCompletedDocument()
    {
        Root.Children.Clear();
        RenderCompletedDocumentInto(Root, _source.ToString());
        _stableSourceLength = _source.Length;
        _renderedSourceLength = _source.Length;
    }

    private SelectableTextBlock CreateSelectableDocument() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
        LineHeight = 22,
        Foreground = _foreground,
        SelectionBrush = Palette.Accent,
        SelectionForegroundBrush = Palette.OnAccent,
        Focusable = true
    };

    /// <summary>
    /// Completed prose and tables are consolidated into large selectable regions so a
    /// drag selection can cross them while the transcript scrolls. Chart WebViews remain
    /// separate interactive surfaces.
    /// </summary>
    private void RenderCompletedDocumentInto(StackPanel target, string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n');
        var document = CreateSelectableDocument();
        var hasContent = false;
        var pendingBreaks = 0;

        void StartContentLine()
        {
            if (hasContent)
            {
                for (var count = 0; count < Math.Max(1, pendingBreaks); count++)
                    document.Inlines!.Add(new LineBreak());
            }
            pendingBreaks = 0;
        }

        void AppendInlineLine(string text, double fontSize, FontWeight weight, IBrush? foreground = null)
        {
            StartContentLine();
            AddInlineText(document, text, fontSize, weight, foreground ?? _foreground);
            hasContent = true;
            pendingBreaks = 1;
        }

        void AppendCodeLine(string text, double fontSize = 12)
        {
            StartContentLine();
            document.Inlines!.Add(new Run(text.Length == 0 ? " " : text)
            {
                FontFamily = Palette.Mono,
                FontSize = fontSize,
                FontWeight = FontWeight.Normal,
                Foreground = Palette.InlineCodeText,
                Background = Palette.CodeBackground
            });
            hasContent = true;
            pendingBreaks = 1;
        }

        void AppendTableRow(IReadOnlyList<string> cells, bool header)
        {
            StartContentLine();
            document.Inlines!.Add(new Run("| ")
            {
                FontFamily = Palette.Mono,
                FontSize = 12,
                Foreground = Palette.Muted
            });
            for (var column = 0; column < cells.Count; column++)
            {
                document.Inlines.Add(new Run(cells[column])
                {
                    FontSize = header ? 11.5 : 12,
                    FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = _foreground,
                    Background = header ? Palette.Raised : Brushes.Transparent
                });
                document.Inlines.Add(new Run(column == cells.Count - 1 ? " |" : " | ")
                {
                    FontFamily = Palette.Mono,
                    FontSize = 12,
                    Foreground = Palette.Muted
                });
            }
            hasContent = true;
            pendingBreaks = 1;
        }

        void AppendTableSeparator(int columns)
        {
            StartContentLine();
            document.Inlines!.Add(new Run("| " + string.Join(" | ", Enumerable.Repeat("---", columns)) + " |")
            {
                FontFamily = Palette.Mono,
                FontSize = 11,
                Foreground = Palette.Divider
            });
            hasContent = true;
            pendingBreaks = 1;
        }

        void FlushDocument()
        {
            if (!hasContent) return;
            target.Children.Add(document);
            document = CreateSelectableDocument();
            hasContent = false;
            pendingBreaks = 0;
        }

        for (var index = 0; index < lines.Length;)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                if (hasContent) pendingBreaks = Math.Max(pendingBreaks, 2);
                index++;
                continue;
            }

            if (lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var marker = lines[index].Trim();
                var language = marker.Length > 3 ? marker[3..].Trim() : "";
                index++;
                var code = new StringBuilder();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.AppendLine();
                    code.Append(lines[index++]);
                }
                if (index < lines.Length) index++;
                var source = code.ToString();
                if (language.Equals("chart", StringComparison.OrdinalIgnoreCase) &&
                    ChartHtmlRenderer.TryBuild(source, out var html, out var summary, out var kind, out _))
                {
                    FlushDocument();
                    target.Children.Add(new HtmlChartView(html, source, summary, kind));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(language))
                    AppendInlineLine(language.ToUpperInvariant(), 10, FontWeight.SemiBold, Palette.Muted);
                foreach (var codeLine in source.Split('\n')) AppendCodeLine(codeLine);
                pendingBreaks = Math.Max(pendingBreaks, 2);
                continue;
            }

            if (IsHorizontalRule(lines[index]))
            {
                AppendInlineLine("────────────────────────", 11, FontWeight.Normal, Palette.Divider);
                index++;
                continue;
            }

            if (IsTableStart(lines, index))
            {
                var headers = TableCells(lines[index]);
                index += 2;
                var rows = new List<string[]>();
                while (index < lines.Length && LooksLikeTableRow(lines[index]))
                {
                    rows.Add(TableCells(lines[index]));
                    index++;
                }
                var columnCount = Math.Max(1,
                    Math.Max(headers.Length, rows.Count == 0 ? 0 : rows.Max(row => row.Length)));
                static string[] NormalizeRow(string[] row, int count) =>
                    Enumerable.Range(0, count).Select(column => column < row.Length ? row[column] : "").ToArray();
                if (hasContent) pendingBreaks = Math.Max(pendingBreaks, 2);
                AppendTableRow(NormalizeRow(headers, columnCount), header: true);
                AppendTableSeparator(columnCount);
                foreach (var row in rows) AppendTableRow(NormalizeRow(row, columnCount), header: false);
                pendingBreaks = Math.Max(pendingBreaks, 2);
                continue;
            }

            var heading = HeadingLevel(lines[index]);
            if (heading > 0)
            {
                AppendInlineLine(lines[index].TrimStart()[heading..].TrimStart(),
                    heading == 1 ? 20 : heading == 2 ? 17 : 15, FontWeight.SemiBold);
                pendingBreaks = Math.Max(pendingBreaks, 2);
                index++;
                continue;
            }

            if (IsBullet(lines[index]))
            {
                var text = lines[index].TrimStart()[2..].Trim();
                var task = Regex.Match(text, @"^\[([ xX])\]\s+(.+)$");
                AppendInlineLine(task.Success
                    ? $"{(task.Groups[1].Value.Equals("x", StringComparison.OrdinalIgnoreCase) ? "✓" : "□")}  {task.Groups[2].Value}"
                    : $"•  {text}", 14, FontWeight.Normal);
                index++;
                continue;
            }

            if (Numbered(lines[index]) is { } numbered)
            {
                AppendInlineLine($"{numbered.Number}.  {numbered.Text}", 14, FontWeight.Normal);
                index++;
                continue;
            }

            if (lines[index].TrimStart().StartsWith("> ", StringComparison.Ordinal))
            {
                AppendInlineLine("│  " + lines[index].TrimStart()[2..], 13, FontWeight.Normal, Palette.Muted);
                index++;
                continue;
            }

            AppendInlineLine(lines[index].Trim(), 14, FontWeight.Normal);
            index++;
        }

        FlushDocument();
    }

    private void RenderBlocksInto(StackPanel target, string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n');
        for (var index = 0; index < lines.Length;)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) { index++; continue; }

            if (lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var marker = lines[index].Trim();
                var language = marker.Length > 3 ? marker[3..].Trim() : "";
                index++;
                var code = new StringBuilder();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.AppendLine();
                    code.Append(lines[index++]);
                }
                if (index < lines.Length) index++;
                var source = code.ToString();
                if (language.Equals("chart", StringComparison.OrdinalIgnoreCase) &&
                    ChartHtmlRenderer.TryBuild(source, out var html, out var summary, out var kind, out _))
                    target.Children.Add(new HtmlChartView(html, source, summary, kind));
                else
                    target.Children.Add(CodeBlock(language, source));
                continue;
            }

            if (IsHorizontalRule(lines[index]))
            {
                target.Children.Add(new Border
                {
                    Height = 1,
                    Background = Palette.Divider,
                    Margin = new Thickness(0, 7)
                });
                index++;
                continue;
            }

            if (IsTableStart(lines, index))
            {
                target.Children.Add(TableBlock(lines, ref index));
                continue;
            }

            var heading = HeadingLevel(lines[index]);
            if (heading > 0)
            {
                var text = lines[index].TrimStart()[heading..].TrimStart();
                var block = InlineText(text, heading == 1 ? 20 : heading == 2 ? 17 : 15, FontWeight.SemiBold);
                block.Margin = new Thickness(0, target.Children.Count == 0 ? 0 : 5, 0, 1);
                target.Children.Add(block);
                index++;
                continue;
            }

            if (IsBullet(lines[index]))
            {
                var list = new StackPanel { Spacing = 4 };
                while (index < lines.Length && IsBullet(lines[index]))
                {
                    var text = lines[index].TrimStart()[2..].Trim();
                    var task = Regex.Match(text, @"^\[([ xX])\]\s+(.+)$");
                    list.Children.Add(task.Success
                        ? ListRow(task.Groups[1].Value.Equals("x", StringComparison.OrdinalIgnoreCase) ? "✓" : "□", task.Groups[2].Value)
                        : ListRow("•", text));
                    index++;
                }
                target.Children.Add(list);
                continue;
            }

            if (Numbered(lines[index]) is not null)
            {
                var list = new StackPanel { Spacing = 4 };
                while (index < lines.Length && Numbered(lines[index]) is { } item)
                {
                    list.Children.Add(ListRow(item.Number + ".", item.Text));
                    index++;
                }
                target.Children.Add(list);
                continue;
            }

            if (lines[index].TrimStart().StartsWith("> ", StringComparison.Ordinal))
            {
                var quote = new StringBuilder();
                while (index < lines.Length && lines[index].TrimStart().StartsWith("> ", StringComparison.Ordinal))
                {
                    if (quote.Length > 0) quote.AppendLine();
                    quote.Append(lines[index].TrimStart()[2..]);
                    index++;
                }
                target.Children.Add(new Border
                {
                    BorderBrush = Palette.Divider,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Background = Palette.Raised,
                    CornerRadius = new CornerRadius(0, 8, 8, 0),
                    Padding = new Thickness(12, 9),
                    Child = InlineText(quote.ToString(), 13, FontWeight.Normal, Palette.Muted)
                });
                continue;
            }

            var paragraph = new StringBuilder(lines[index++].Trim());
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) &&
                   !IsBlockStart(lines[index]))
            {
                paragraph.Append(' ').Append(lines[index++].Trim());
            }
            target.Children.Add(InlineText(paragraph.ToString(), 14, FontWeight.Normal));
        }
    }

    private Control CodeBlock(string language, string code)
    {
        var header = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "CODE" : language.ToUpperInvariant(),
            FontFamily = Palette.Mono,
            FontSize = 9.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        var copy = Palette.SmallButton("복사");
        copy.Click += async (_, _) => await Palette.CopyAsync(copy, code);
        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(11, 6, 7, 5) };
        headerRow.Children.Add(header);
        Grid.SetColumn(copy, 1);
        headerRow.Children.Add(copy);
        var codeText = CreateSelectableDocument();
        codeText.Text = code;
        codeText.FontFamily = Palette.Mono;
        codeText.FontSize = 12;
        codeText.LineHeight = 19;
        codeText.Foreground = Palette.White;
        codeText.TextWrapping = TextWrapping.NoWrap;
        codeText.Margin = new Thickness(12, 9, 14, 12);
        var codeScroll = new ScrollViewer
        {
            Content = codeText,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(headerRow);
        stack.Children.Add(new Border { Height = 1, Background = Palette.Divider });
        stack.Children.Add(codeScroll);
        return new Border
        {
            Child = stack,
            Background = Palette.CodeBackground,
            BorderBrush = Palette.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
    }

    private Control TableBlock(string[] lines, ref int index)
    {
        var headers = TableCells(lines[index]);
        index += 2;
        var rows = new List<string[]> { headers };
        while (index < lines.Length && LooksLikeTableRow(lines[index]))
        {
            rows.Add(TableCells(lines[index]));
            index++;
        }

        var columnCount = Math.Max(1, rows.Max(row => row.Length));
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("Auto", columnCount)))
        };
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var value = columnIndex < rows[rowIndex].Length ? rows[rowIndex][columnIndex] : "";
                var cellText = InlineText(value, rowIndex == 0 ? 11.5 : 12, rowIndex == 0 ? FontWeight.SemiBold : FontWeight.Normal);
                cellText.MinWidth = 110;
                cellText.MaxWidth = 320;
                var cell = new Border
                {
                    Child = cellText,
                    Background = rowIndex == 0 ? Palette.Raised : Brushes.Transparent,
                    BorderBrush = Palette.Divider,
                    BorderThickness = new Thickness(columnIndex == 0 ? 0 : 1, rowIndex == 0 ? 0 : 1, 0, 0),
                    Padding = new Thickness(10, 8)
                };
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, columnIndex);
                grid.Children.Add(cell);
            }
        }
        return new Border
        {
            Background = Palette.Panel,
            BorderBrush = Palette.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            }
        };
    }

    private SelectableTextBlock InlineText(string text, double fontSize, FontWeight weight, IBrush? foreground = null)
    {
        var block = CreateSelectableDocument();
        block.FontSize = fontSize;
        block.LineHeight = fontSize + 8;
        block.Foreground = foreground ?? _foreground;
        block.FontWeight = weight;
        AddInlineText(block, text, fontSize, weight, foreground ?? _foreground);
        return block;
    }

    private Control ListRow(string marker, string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*") };
        grid.Children.Add(new TextBlock
        {
            Text = marker,
            Foreground = Palette.Muted,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 7, 0)
        });
        var content = InlineText(text, 14, FontWeight.Normal);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private static void AddInlineText(SelectableTextBlock document, string text, double fontSize,
        FontWeight weight, IBrush foreground)
    {
        var position = 0;
        foreach (Match match in Regex.Matches(text, @"\*\*(.+?)\*\*|~~(.+?)~~|(?<!\*)\*([^*\n]+?)\*(?!\*)|`([^`]+)`|\[([^\]]+)\]\(([^)]+)\)"))
        {
            if (match.Index > position)
                AddRun(document, text[position..match.Index], fontSize, weight, foreground);
            if (match.Groups[1].Success)
                AddRun(document, match.Groups[1].Value, fontSize, FontWeight.SemiBold, foreground);
            else if (match.Groups[2].Success)
                document.Inlines!.Add(new Run(match.Groups[2].Value)
                {
                    FontSize = fontSize,
                    FontWeight = weight,
                    Foreground = Palette.Muted,
                    TextDecorations = TextDecorations.Strikethrough
                });
            else if (match.Groups[3].Success)
                document.Inlines!.Add(new Run(match.Groups[3].Value)
                {
                    FontSize = fontSize,
                    FontStyle = FontStyle.Italic,
                    FontWeight = weight,
                    Foreground = foreground
                });
            else if (match.Groups[4].Success)
                document.Inlines!.Add(new Run(match.Groups[4].Value)
                {
                    FontFamily = Palette.Mono,
                    FontSize = Math.Max(10, fontSize - 1),
                    Foreground = Palette.InlineCodeText,
                    Background = Palette.InlineCode
                });
            else
                document.Inlines!.Add(new Run(match.Groups[5].Value)
                {
                    FontSize = fontSize,
                    FontWeight = weight,
                    Foreground = Palette.Link,
                    TextDecorations = TextDecorations.Underline
                });
            position = match.Index + match.Length;
        }
        if (position < text.Length) AddRun(document, text[position..], fontSize, weight, foreground);
    }

    private static void AddRun(SelectableTextBlock document, string text, double fontSize,
        FontWeight weight, IBrush foreground) =>
        document.Inlines!.Add(new Run(text)
        {
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = foreground
        });

    private static int StablePrefixLength(string text)
    {
        var inFence = false;
        var lineStart = 0;
        var lastSafe = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n') continue;
            var line = text[lineStart..index].Trim();
            if (line.StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            else if (!inFence && line.Length == 0) lastSafe = index + 1;
            lineStart = index + 1;
        }
        return lastSafe;
    }

    private static string LivePreview(string text)
    {
        if (text.Length <= MaximumLiveBlockCharacters) return text;
        var tail = text[^MaximumLiveBlockCharacters..];
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return "… 작성 중인 블록 앞부분 생략 …\n" + tail;

        var fenceEnd = trimmed.IndexOf('\n');
        var fence = fenceEnd < 0 ? trimmed : trimmed[..(fenceEnd + 1)];
        return fence + "… 앞부분 생략 …\n" + tail;
    }

    private static bool IsBlockStart(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal) ||
        HeadingLevel(line) > 0 || IsBullet(line) || Numbered(line) is not null ||
        line.TrimStart().StartsWith("> ", StringComparison.Ordinal) ||
        IsHorizontalRule(line) || LooksLikeTableRow(line);

    private static bool IsHorizontalRule(string line)
    {
        var text = line.Trim();
        return text.Length >= 3 && (text.All(character => character == '-') ||
                                    text.All(character => character == '*') ||
                                    text.All(character => character == '_'));
    }

    private static bool IsTableStart(string[] lines, int index)
    {
        if (index + 1 >= lines.Length || !LooksLikeTableRow(lines[index])) return false;
        var separators = TableCells(lines[index + 1]);
        return separators.Length > 0 && separators.All(cell => Regex.IsMatch(cell.Trim(), @"^:?-{3,}:?$"));
    }

    private static bool LooksLikeTableRow(string line)
    {
        var text = line.Trim();
        return text.Contains('|') && text.Trim('|').Contains('|');
    }

    private static string[] TableCells(string line) =>
        line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

    private static int HeadingLevel(string line)
    {
        var text = line.TrimStart();
        var count = 0;
        while (count < text.Length && count < 3 && text[count] == '#') count++;
        return count > 0 && count < text.Length && text[count] == ' ' ? count : 0;
    }

    private static bool IsBullet(string line)
    {
        var text = line.TrimStart();
        return text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal);
    }

    private static (string Number, string Text)? Numbered(string line)
    {
        var match = Regex.Match(line.TrimStart(), @"^(\d+)\.\s+(.+)$");
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : null;
    }
}

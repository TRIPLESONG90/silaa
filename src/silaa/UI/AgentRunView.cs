using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Text;

namespace silaa.UI;

/// <summary>
/// Codex/Claude Code-inspired streaming transcript: collapsible thinking, paired tool activity,
/// and an uncluttered final answer.
/// </summary>
internal sealed class AgentRunView
{
    private readonly StackPanel _content = new() { Spacing = 8 };
    private readonly Dictionary<string, ToolActivityView> _tools = new(StringComparer.Ordinal);
    private readonly List<string> _timelineKinds = [];
    private readonly List<MarkdownView> _answerViews = [];
    private readonly StringBuilder _answerText = new();
    private readonly WaitingIndicator _waiting = new();
    private readonly Action<Uri>? _openLink;
    private ReasoningDisclosure? _activeReasoning;
    private MarkdownView? _activeAnswer;
    private bool _answerCopyAdded;

    public AgentRunView(Action<Uri>? openLink = null)
    {
        _openLink = openLink;
        var identity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
                    Background = Palette.Accent
                },
                new TextBlock
                {
                    Text = "silaa", Foreground = Palette.Muted, FontSize = 11,
                    FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(identity);
        _content.Children.Add(_waiting.Root);
        stack.Children.Add(_content);
        Root = new Border
        {
            Child = stack,
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 840
        };
    }

    public Control Root { get; }
    public bool HasAnswer => _answerText.Length > 0;
    public string AnswerText => _answerText.ToString();
    internal bool IsWaiting => _waiting.IsActive;
    internal IReadOnlyList<string> TimelineKinds => _timelineKinds;

    public void AppendReasoning(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        StopWaiting();
        FinishAnswerPhase();
        if (_activeReasoning is null)
        {
            _activeReasoning = new ReasoningDisclosure();
            _content.Children.Add(_activeReasoning.Root);
            _timelineKinds.Add("reasoning");
        }
        _activeReasoning.Append(text);
    }

    public void BeginTool(string? id, string text)
        => BeginActivity(id, text, "tool");

    public void BeginAgent(string? id, string text)
        => BeginActivity(id, text, "agent");

    private void BeginActivity(string? id, string text, string kind)
    {
        StopWaiting();
        FinishReasoningPhase();
        FinishAnswerPhase();
        var (name, detail) = Split(text);
        var key = string.IsNullOrWhiteSpace(id) ? $"tool-{_tools.Count}" : id;
        if (!_tools.TryGetValue(key, out var tool))
        {
            tool = new ToolActivityView(name, detail);
            _tools[key] = tool;
            _content.Children.Add(tool.Root);
            _timelineKinds.Add(kind);
        }
    }

    public void CompleteTool(string? id, string text)
    {
        var (name, detail) = Split(text);
        var key = string.IsNullOrWhiteSpace(id) ? "" : id;
        if (!_tools.TryGetValue(key, out var tool))
        {
            tool = _tools.Values.LastOrDefault(item => !item.IsComplete && item.Name == name);
            if (tool is null)
            {
                tool = new ToolActivityView(name, "{}");
                _content.Children.Add(tool.Root);
            }
        }
        tool.Complete(detail);
    }

    public void CompleteAgent(string? id, string text)
    {
        FinishReasoningPhase();
        FinishAnswerPhase();
        CompleteTool(id, text);
    }

    public void AppendAnswer(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        StopWaiting();
        if (_activeAnswer is null)
        {
            if (_answerText.Length > 0 && _answerText[^1] != '\n') _answerText.AppendLine().AppendLine();
            PrepareAnswerSegment();
        }
        _answerText.Append(text);
        _activeAnswer!.Append(text);
    }

    public void SetFinalAnswer(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.Equals(_answerText.ToString(), text, StringComparison.Ordinal)) return;
        StopWaiting();
        FinishAnswerPhase();
        foreach (var answerView in _answerViews) _content.Children.Remove(answerView.Root);
        _answerViews.Clear();
        _timelineKinds.RemoveAll(kind => kind == "answer");
        _answerText.Clear();
        _answerText.Append(text);
        var replacement = new MarkdownView(openLink: _openLink);
        replacement.SetText(text);
        replacement.Complete();
        _answerViews.Add(replacement);
        _content.Children.Add(replacement.Root);
        _timelineKinds.Add("answer");
        _activeAnswer = null;
    }

    public void ShowError(string text)
    {
        StopWaiting();
        FinishAnswerPhase();
        if (_answerText.Length > 0 && _answerText[^1] != '\n') _answerText.AppendLine().AppendLine();
        PrepareAnswerSegment();
        _answerText.Append(text);
        _activeAnswer!.SetForeground(Palette.Danger);
        _activeAnswer.SetText(text);
        FinishAnswerPhase();
    }

    public void AddInteractive(Control control, string kind)
    {
        StopWaiting();
        FinishReasoningPhase();
        FinishAnswerPhase();
        _content.Children.Add(control);
        _timelineKinds.Add(kind);
    }

    public void MarkInterrupted(string text)
    {
        StopWaiting();
        FinishReasoningPhase();
        FinishAnswerPhase();
        foreach (var tool in _tools.Values) tool.StopRunning();
        _content.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Palette.Muted,
            FontSize = 10.5,
            FontStyle = FontStyle.Italic,
            Margin = new Thickness(0, 2, 0, 0)
        });
        _timelineKinds.Add("status");
    }

    public void Complete()
    {
        StopWaiting();
        FinishReasoningPhase();
        FinishAnswerPhase();
        foreach (var tool in _tools.Values) tool.StopRunning();
        EnsureAnswerCopyButton();
    }

    public void FlushStreamingPreviews()
    {
        _activeReasoning?.FlushStreamingPreview();
        _activeAnswer?.FlushStreaming();
    }

    private void PrepareAnswerSegment()
    {
        if (_activeAnswer is not null) return;
        FinishReasoningPhase();
        if (_answerText.Length == 0)
        {
            _content.Children.Add(new Border
            {
                Height = 1, Background = Palette.Divider,
                Margin = new Thickness(0, 3, 0, 4)
            });
        }
        _activeAnswer = new MarkdownView(openLink: _openLink);
        _answerViews.Add(_activeAnswer);
        _content.Children.Add(_activeAnswer.Root);
        _timelineKinds.Add("answer");
    }

    private void FinishAnswerPhase()
    {
        if (_activeAnswer is null) return;
        _activeAnswer.Complete();
        _activeAnswer = null;
    }

    private void EnsureAnswerCopyButton()
    {
        if (_answerCopyAdded || _answerText.Length == 0) return;
        var copy = Palette.SmallButton("답변 복사");
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        copy.Margin = new Thickness(-7, 0, 0, 0);
        copy.Click += async (_, _) => await Palette.CopyAsync(copy, _answerText.ToString());
        _content.Children.Add(copy);
        _answerCopyAdded = true;
    }

    private void FinishReasoningPhase()
    {
        if (_activeReasoning is null) return;
        _activeReasoning.Complete(collapse: true);
        _activeReasoning = null;
    }

    private static (string Name, string Detail) Split(string text)
    {
        var lines = text.Replace("\r", "").Split('\n', 2);
        return (string.IsNullOrWhiteSpace(lines[0]) ? "tool" : lines[0].Trim(), lines.Length > 1 ? lines[1].Trim() : "");
    }

    private void StopWaiting() => _waiting.Stop();

    private sealed class WaitingIndicator
    {
        private readonly Avalonia.Controls.Shapes.Path _spinner = new()
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.None,
            Stroke = Palette.Accent,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = RelativePoint.Center
        };
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(55) };
        private double _angle;

        public WaitingIndicator()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    _spinner,
                    new TextBlock
                    {
                        Text = "응답 기다리는 중…",
                        FontSize = 11,
                        Foreground = Palette.Muted,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
            Root = new Border
            {
                Child = row,
                Padding = new Thickness(0, 3, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _timer.Tick += (_, _) =>
            {
                _angle = (_angle + 24) % 360;
                _spinner.RenderTransform = new RotateTransform(_angle);
            };
            _spinner.AttachedToVisualTree += (_, _) =>
            {
                _spinner.Data ??= Geometry.Parse("M 7 1 A 6 6 0 1 1 1 7");
                if (IsActive) _timer.Start();
            };
            _spinner.DetachedFromVisualTree += (_, _) => _timer.Stop();
        }

        public Control Root { get; }
        public bool IsActive { get; private set; } = true;

        public void Stop()
        {
            if (!IsActive) return;
            IsActive = false;
            _timer.Stop();
            Root.IsVisible = false;
        }
    }

    /// <summary>
    /// One vector disclosure icon is rotated for both states. This avoids the
    /// different baselines and advance widths of the former ›/⌄ font glyphs.
    /// </summary>
    private sealed class DisclosureChevron : Border
    {
        private readonly Avalonia.Controls.Shapes.Path _path = new()
        {
            Width = 10,
            Height = 6,
            Stretch = Stretch.None,
            Stroke = Palette.Muted,
            StrokeThickness = 1.35,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = RelativePoint.Center
        };

        public DisclosureChevron()
        {
            Width = 18;
            Height = 18;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            Child = _path;
            // Geometry creation needs Avalonia's render platform, so load the
            // SVG-style path only when the control enters the visual tree.
            _path.AttachedToVisualTree += (_, _) =>
                _path.Data ??= Geometry.Parse("M 1 1 L 5 5 L 9 1");
            SetOpen(true);
        }

        public void SetOpen(bool open) =>
            _path.RenderTransform = new RotateTransform(open ? 0 : -90);
    }

    private sealed class ReasoningDisclosure
    {
        private const int MaximumStreamingPreviewCharacters = 12_000;
        private readonly DisclosureChevron _chevron = new();
        private readonly TextBlock _title = new() { Text = "생각 중…", FontSize = 12, Foreground = Palette.Reasoning, FontWeight = FontWeight.Medium };
        private readonly TextBlock _meta = new() { FontSize = 10.5, Foreground = Palette.Muted };
        private readonly TextBlock _streamingBody = new()
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 19,
            Foreground = Palette.Reasoning
        };
        private readonly Border _bodyHost;
        private readonly StringBuilder _buffer = new();
        private bool _open = true;
        private int _characterCount;
        private int _previewedCharacterCount;

        public ReasoningDisclosure()
        {
            var headerContent = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto"), ColumnSpacing = 5 };
            headerContent.Children.Add(_chevron);
            Grid.SetColumn(_title, 1);
            headerContent.Children.Add(_title);
            Grid.SetColumn(_meta, 2);
            headerContent.Children.Add(_meta);
            var header = new Button
            {
                Content = headerContent,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            header.Classes.Add("ghost");
            header.Click += (_, _) => SetOpen(!_open);
            _bodyHost = new Border
            {
                Child = _streamingBody,
                BorderBrush = Palette.Divider,
                BorderThickness = new Thickness(1, 0, 0, 0),
                Padding = new Thickness(12, 3, 0, 2),
                Margin = new Thickness(23, 1, 0, 3)
            };
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(header);
            stack.Children.Add(_bodyHost);
            Root = stack;
        }

        public Control Root { get; }

        public void Append(string text)
        {
            _buffer.Append(text);
            _characterCount += text.Length;
            _meta.Text = CharacterCount(_characterCount);
        }

        public void FlushStreamingPreview()
        {
            if (_previewedCharacterCount != _characterCount) RefreshStreamingPreview();
        }

        public void Complete(bool collapse)
        {
            _title.Text = "생각함";
            _meta.Text = CharacterCount(_characterCount);
            _bodyHost.Child = new SelectableTextBlock
            {
                Text = _buffer.ToString(),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                LineHeight = 19,
                Foreground = Palette.Reasoning
            };
            if (collapse) SetOpen(false);
        }

        private void RefreshStreamingPreview()
        {
            _streamingBody.Text = _buffer.Length <= MaximumStreamingPreviewCharacters
                ? _buffer.ToString()
                : "… 이전 추론 생략 …\n" + _buffer.ToString(_buffer.Length - MaximumStreamingPreviewCharacters, MaximumStreamingPreviewCharacters);
            _previewedCharacterCount = _characterCount;
        }

        private void SetOpen(bool open)
        {
            _open = open;
            _chevron.SetOpen(open);
            _bodyHost.IsVisible = open;
        }

        private static string CharacterCount(int count) => count >= 1000 ? $"{count / 1000d:0.#}k자" : $"{count}자";
    }

    private sealed class ToolActivityView
    {
        private readonly DisclosureChevron _chevron = new();
        private readonly SiStatusBadge _status = new("실행 중", SiStatusTone.Running);
        private readonly SelectableTextBlock _result = new()
        {
            FontFamily = Palette.Mono,
            FontSize = 11.5,
            LineHeight = 17,
            Foreground = Palette.Muted,
            TextWrapping = TextWrapping.Wrap
        };
        private readonly Border _details;
        private readonly Border _resultSection;
        private bool _open = true;

        public ToolActivityView(string name, string input)
        {
            Name = name;
            var summary = Compact(input);
            var title = new TextBlock
            {
                Text = name,
                FontFamily = Palette.Mono,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = Palette.White,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var secondary = new TextBlock
            {
                Text = summary,
                FontFamily = Palette.Mono,
                FontSize = 11,
                Foreground = Palette.Muted,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(7, 0, 0, 0)
            };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
            titleRow.Children.Add(title);
            if (summary.Length > 0) titleRow.Children.Add(secondary);
            var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto"), ColumnSpacing = 7 };
            headerGrid.Children.Add(_chevron);
            Grid.SetColumn(titleRow, 1);
            headerGrid.Children.Add(titleRow);
            Grid.SetColumn(_status, 2);
            headerGrid.Children.Add(_status);
            var header = new Button
            {
                Content = headerGrid,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            header.Classes.Add("ghost");
            header.Click += (_, _) => SetOpen(!_open);

            var detailsStack = new StackPanel { Spacing = 0 };
            detailsStack.Children.Add(DetailSection("입력", input.Length == 0 ? "{}" : input));
            _resultSection = DetailSection("결과", "");
            _resultSection.IsVisible = false;
            detailsStack.Children.Add(_resultSection);
            _details = new Border
            {
                Child = detailsStack,
                Background = Palette.CodeBackground,
                BorderBrush = Palette.Divider,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(25, 4, 0, 6)
            };
            var stack = new StackPanel { Spacing = 1 };
            stack.Children.Add(header);
            stack.Children.Add(_details);
            Root = stack;
        }

        public string Name { get; }
        public bool IsComplete { get; private set; }
        public Control Root { get; }

        public void Complete(string result)
        {
            IsComplete = true;
            var failed = result.StartsWith("오류:", StringComparison.OrdinalIgnoreCase);
            _status.Set(failed ? "실패" : "완료", failed ? SiStatusTone.Danger : SiStatusTone.Success);
            SetOpen(false);
            _result.Text = result.Length == 0 ? "(결과 없음)" : result;
            _resultSection.IsVisible = true;
        }

        public void StopRunning()
        {
            if (IsComplete) return;
            _status.Set("중지됨", SiStatusTone.Neutral);
        }

        private Border DetailSection(string label, string value)
        {
            var valueBlock = label == "결과" ? _result : new SelectableTextBlock
            {
                Text = value,
                FontFamily = Palette.Mono,
                FontSize = 11.5,
                LineHeight = 17,
                Foreground = Palette.Muted,
                TextWrapping = TextWrapping.Wrap
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("58,*") };
            grid.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = Palette.Mono,
                FontSize = 10.5,
                Foreground = Palette.Muted,
                Opacity = 0.7,
                Margin = new Thickness(9, 8)
            });
            var valueHost = new Border
            {
                Child = valueBlock,
                Padding = new Thickness(8)
            };
            Grid.SetColumn(valueHost, 1);
            grid.Children.Add(valueHost);
            return new Border
            {
                Child = grid,
                BorderBrush = Palette.Divider,
                BorderThickness = new Thickness(0, label == "결과" ? 1 : 0, 0, 0)
            };
        }

        private void SetOpen(bool open)
        {
            _open = open;
            _chevron.SetOpen(open);
            _details.IsVisible = open;
        }

        private static string Compact(string value)
        {
            var text = string.Join(' ', value.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)).Trim();
            return text.Length > 96 ? text[..96] + "…" : text;
        }
    }
}

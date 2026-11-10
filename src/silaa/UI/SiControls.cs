using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using silaa.Services;

namespace silaa.UI;

internal enum SiButtonVariant
{
    Primary,
    Secondary,
    Ghost,
    Danger
}

internal enum SiButtonSize
{
    Small,
    Medium,
    Large
}

/// <summary>A single button implementation used throughout the application.</summary>
internal sealed class SiButton : Button
{
    public SiButton(string title, SiButtonVariant variant = SiButtonVariant.Secondary, SiButtonSize size = SiButtonSize.Medium)
    {
        Content = title;
        Variant = variant;
        Size = size;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        FontWeight = FontWeight.Medium;
        CornerRadius = new CornerRadius(size == SiButtonSize.Small ? 6 : 8);
        AutomationProperties.SetName(this, title);
        ApplySize(size);
        ApplyVariant(variant);
    }

    public SiButtonVariant Variant { get; }
    public SiButtonSize Size { get; }

    private void ApplySize(SiButtonSize size)
    {
        (Padding, FontSize) = size switch
        {
            SiButtonSize.Small => (new Thickness(8, 4), 11.5),
            SiButtonSize.Large => (new Thickness(18, 11), 14),
            _ => (new Thickness(15, 8), 12.5)
        };
    }

    private void ApplyVariant(SiButtonVariant variant)
    {
        switch (variant)
        {
            case SiButtonVariant.Primary:
                Background = Palette.Accent;
                Foreground = Palette.OnAccent;
                BorderBrush = Palette.Accent;
                BorderThickness = new Thickness(1);
                Classes.Add("primary");
                break;
            case SiButtonVariant.Ghost:
                Background = Brushes.Transparent;
                Foreground = Palette.Muted;
                BorderThickness = new Thickness(0);
                Classes.Add("ghost");
                break;
            case SiButtonVariant.Danger:
                Background = Brushes.Transparent;
                Foreground = Palette.Danger;
                BorderBrush = Palette.Divider;
                BorderThickness = new Thickness(1);
                Classes.Add("danger");
                break;
            default:
                Background = Palette.Panel;
                Foreground = Palette.White;
                BorderBrush = Palette.Divider;
                BorderThickness = new Thickness(1);
                Classes.Add("secondary");
                break;
        }
    }
}

/// <summary>Standard content surface; glass is deliberately reserved for floating chrome.</summary>
internal class SiPanel : Border
{
    public SiPanel(Control child, double padding = 16, double radius = 12, bool glass = false, bool elevated = false)
    {
        Child = child;
        Background = glass ? Palette.Glass : Palette.Panel;
        BorderBrush = glass ? Palette.GlassBorder : Palette.Divider;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(radius);
        Padding = new Thickness(padding);
        if (elevated) BoxShadow = Palette.CardShadow;
    }
}

/// <summary>Label, supporting copy, input and error presentation with consistent spacing.</summary>
internal sealed class SiField : StackPanel
{
    private readonly TextBlock _error = new()
    {
        Foreground = Palette.Danger,
        FontSize = 10.5,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false
    };

    public SiField(string label, string description, Control control)
    {
        Spacing = 6;
        Margin = new Thickness(0, 0, 0, 10);
        var labelBlock = new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12.5
        };
        Children.Add(labelBlock);
        AutomationProperties.SetName(control, label);
        AutomationProperties.SetLabeledBy(control, labelBlock);
        if (!string.IsNullOrWhiteSpace(description)) AutomationProperties.SetHelpText(control, description);
        if (!string.IsNullOrWhiteSpace(description))
        {
            Children.Add(new TextBlock
            {
                Text = description,
                Foreground = Palette.Muted,
                FontSize = 10.75,
                LineHeight = 16,
                TextWrapping = TextWrapping.Wrap
            });
        }
        Children.Add(control);
        Children.Add(_error);
    }

    public void SetError(string? message)
    {
        _error.Text = message ?? "";
        _error.IsVisible = !string.IsNullOrWhiteSpace(message);
    }
}

/// <summary>Consistent page title block with an optional trailing action.</summary>
internal sealed class SiPageHeader : Grid
{
    public SiPageHeader(string eyebrow, string title, string description, Control? action = null)
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        ColumnSpacing = 24;
        var copy = new StackPanel { Spacing = 5 };
        if (!string.IsNullOrWhiteSpace(eyebrow))
        {
            copy.Children.Add(new TextBlock
            {
                Text = eyebrow.ToUpperInvariant(),
                Foreground = Palette.Muted,
                FontSize = 9.5,
                FontWeight = FontWeight.SemiBold,
                LetterSpacing = 1.2
            });
        }
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = -0.4,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetHeadingLevel(titleBlock, 1);
        copy.Children.Add(titleBlock);
        if (!string.IsNullOrWhiteSpace(description))
        {
            copy.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = Palette.Muted,
                FontSize = 11.5,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap
            });
        }
        Children.Add(copy);
        if (action is null) return;
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        Children.Add(action);
    }
}

internal sealed class SiSectionHeader : Grid
{
    public SiSectionHeader(string title, string? description = null, Control? action = null)
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        var copy = new StackPanel { Spacing = 2 };
        var titleBlock = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold };
        AutomationProperties.SetHeadingLevel(titleBlock, 2);
        copy.Children.Add(titleBlock);
        if (!string.IsNullOrWhiteSpace(description))
            copy.Children.Add(new TextBlock { Text = description, Foreground = Palette.Muted, FontSize = 10.5, TextWrapping = TextWrapping.Wrap });
        Children.Add(copy);
        if (action is null) return;
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        Children.Add(action);
    }
}

internal enum SiStatusTone
{
    Neutral,
    Running,
    Success,
    Danger
}

internal sealed class SiStatusBadge : Border
{
    private readonly Border _dot = new() { Width = 5, Height = 5, CornerRadius = new CornerRadius(3) };
    private readonly TextBlock _label = new() { FontSize = 9.75, FontWeight = FontWeight.Medium };

    public SiStatusBadge(string label, SiStatusTone tone = SiStatusTone.Neutral)
    {
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(7, 3);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_dot);
        row.Children.Add(_label);
        Child = row;
        Set(label, tone);
    }

    public void Set(string label, SiStatusTone tone)
    {
        _label.Text = label;
        var foreground = tone switch
        {
            SiStatusTone.Running => Palette.Running,
            SiStatusTone.Success => Palette.Success,
            SiStatusTone.Danger => Palette.Danger,
            _ => Palette.Muted
        };
        _label.Foreground = foreground;
        _dot.Background = foreground;
        Background = tone switch
        {
            SiStatusTone.Danger => Palette.DangerSoft,
            _ => Palette.Raised
        };
    }
}

internal sealed class SiEmptyState : SiPanel
{
    public SiEmptyState(string mark, string title, string description, Control? action = null)
        : base(Build(mark, title, description, action), 18, 10)
    {
    }

    private static Control Build(string mark, string title, string description, Control? action)
    {
        var icon = new Border
        {
            Width = 38,
            Height = 38,
            Background = Palette.Raised,
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = mark,
                Foreground = Palette.Muted,
                FontSize = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var copy = new StackPanel { Spacing = 3 };
        copy.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeight.SemiBold });
        copy.Children.Add(new TextBlock { Text = description, Foreground = Palette.Muted, FontSize = 10.75, TextWrapping = TextWrapping.Wrap });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(action is null ? "Auto,*" : "Auto,*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(icon);
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, 2);
            row.Children.Add(action);
        }
        return row;
    }
}

internal sealed class SiToastHost : StackPanel
{
    public SiToastHost()
    {
        Spacing = 8;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(18);
        Width = 340;
    }

    public void Show(string title, string message, SiStatusTone tone = SiStatusTone.Neutral, int durationMilliseconds = 3600)
    {
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Palette.Muted,
            FontSize = 10.75,
            LineHeight = 16,
            TextWrapping = TextWrapping.Wrap
        });
        var close = new SiButton("닫기", SiButtonVariant.Ghost, SiButtonSize.Small);
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        var status = new SiStatusBadge(tone == SiStatusTone.Danger ? "오류" : tone == SiStatusTone.Success ? "완료" : "알림", tone);
        status.VerticalAlignment = VerticalAlignment.Top;
        layout.Children.Add(status);
        Grid.SetColumn(content, 1);
        layout.Children.Add(content);
        Grid.SetColumn(close, 2);
        layout.Children.Add(close);
        var toast = new SiPanel(layout, 12, 10, glass: true, elevated: true);
        close.Click += (_, _) => Children.Remove(toast);
        Children.Insert(0, toast);
        while (Children.Count > 3) Children.RemoveAt(Children.Count - 1);
        _ = DismissLaterAsync(toast, durationMilliseconds);
    }

    private async Task DismissLaterAsync(Control toast, int durationMilliseconds)
    {
        await Task.Delay(durationMilliseconds).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            if (Children.Contains(toast)) Children.Remove(toast);
        });
    }
}

internal sealed class SiDialogHost : Grid
{
    private TaskCompletionSource<bool>? _completion;
    private TaskCompletionSource<SiSecretInput?>? _secretCompletion;
    private TextBox? _secretInput;

    public SiDialogHost()
    {
        Background = Palette.Brush("#66000000");
        IsVisible = false;
        Focusable = true;
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            CancelActive();
        };
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "확인", bool danger = false)
    {
        CancelActive();
        _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new SiButton("취소", SiButtonVariant.Secondary);
        var confirm = new SiButton(confirmText, danger ? SiButtonVariant.Danger : SiButtonVariant.Primary);
        cancel.Click += (_, _) => Complete(false);
        confirm.Click += (_, _) => Complete(true);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        var body = new StackPanel { Spacing = 16, Width = 410 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Palette.Muted,
            FontSize = 11.5,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(actions);
        Children.Clear();
        Children.Add(new SiPanel(body, 22, 12, glass: true, elevated: true)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        IsVisible = true;
        Focus();
        return _completion.Task;
    }

    public Task<SiSecretInput?> PromptSecretAsync(
        string title,
        string message,
        string suggestedName,
        string confirmText = "암호화해 저장")
    {
        CancelActive();
        _secretCompletion = new TaskCompletionSource<SiSecretInput?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var name = new TextBox
        {
            Text = suggestedName,
            PlaceholderText = "예: GITHUB_TOKEN"
        };
        var secret = new TextBox
        {
            PasswordChar = '●',
            PlaceholderText = "API 키 또는 토큰을 붙여넣으세요"
        };
        _secretInput = secret;
        var reveal = new CheckBox { Content = "입력값 표시" };
        reveal.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty)
                secret.PasswordChar = reveal.IsChecked == true ? '\0' : '●';
        };
        var status = new TextBlock
        {
            Foreground = Palette.Danger,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new SiButton("취소", SiButtonVariant.Secondary);
        var confirm = new SiButton(confirmText, SiButtonVariant.Primary);
        cancel.Click += (_, _) => CompleteSecret(null);
        confirm.Click += (_, _) =>
        {
            try
            {
                var normalizedName = VaultStore.NormalizeName(name.Text ?? "");
                var value = secret.Text ?? "";
                if (value.Length == 0 || value.Any(char.IsControl))
                    throw new ArgumentException("키 값을 입력하세요.");
                CompleteSecret(new SiSecretInput(normalizedName, value));
                secret.Text = "";
            }
            catch (ArgumentException exception)
            {
                status.Text = exception.Message;
            }
        };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);

        var body = new StackPanel { Spacing = 14, Width = 420 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Palette.Muted,
            FontSize = 11.5,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock { Text = "키 이름", FontSize = 11, FontWeight = FontWeight.SemiBold });
        body.Children.Add(name);
        body.Children.Add(new TextBlock { Text = "키 값", FontSize = 11, FontWeight = FontWeight.SemiBold });
        body.Children.Add(secret);
        body.Children.Add(reveal);
        body.Children.Add(new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(7),
            Background = Palette.Raised,
            Child = new TextBlock
            {
                Text = "키 원문은 Agent와 LLM에 전달되지 않고 현재 Windows 사용자 계정으로 암호화됩니다.",
                Foreground = Palette.Muted,
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap
            }
        });
        body.Children.Add(status);
        body.Children.Add(actions);
        Children.Clear();
        Children.Add(new SiPanel(body, 22, 12, glass: true, elevated: true)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        IsVisible = true;
        Focus();
        Dispatcher.UIThread.Post(() => name.Focus());
        return _secretCompletion.Task;
    }

    private void Complete(bool result)
    {
        IsVisible = false;
        Children.Clear();
        var completion = _completion;
        _completion = null;
        completion?.TrySetResult(result);
    }

    private void CompleteSecret(SiSecretInput? result)
    {
        if (_secretInput is not null) _secretInput.Text = "";
        _secretInput = null;
        IsVisible = false;
        Children.Clear();
        var completion = _secretCompletion;
        _secretCompletion = null;
        completion?.TrySetResult(result);
    }

    private void CancelActive()
    {
        if (_secretInput is not null) _secretInput.Text = "";
        _secretInput = null;
        IsVisible = false;
        Children.Clear();
        var confirmation = _completion;
        _completion = null;
        confirmation?.TrySetResult(false);
        var secret = _secretCompletion;
        _secretCompletion = null;
        secret?.TrySetResult(null);
    }
}

internal sealed class SiSecretInput(string name, string secret)
{
    public string Name { get; } = name;
    public string Secret { get; private set; } = secret;
    public void Clear() => Secret = "";
}

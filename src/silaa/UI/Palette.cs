using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace silaa.UI;

/// <summary>
/// Black-and-white design tokens with restrained contrast for light and dark themes.
/// <see cref="Apply"/> runs once at startup before any window is built; the theme switch takes effect on restart.
/// </summary>
internal static class Palette
{
    public static bool IsDark { get; private set; }
    public static IBrush Background { get; private set; } = null!;    // app canvas
    public static IBrush Panel { get; private set; } = null!;         // sidebar, cards
    public static IBrush Raised { get; private set; } = null!;        // subtle fill: user bubble, chips, inputs
    public static IBrush Hover { get; private set; } = null!;
    public static IBrush Divider { get; private set; } = null!;       // 1px borders
    public static IBrush White { get; private set; } = null!;         // primary text (name kept from the dark-only era)
    public static IBrush Muted { get; private set; } = null!;
    public static IBrush Accent { get; private set; } = null!;
    public static IBrush AccentSoft { get; private set; } = null!;
    public static IBrush Glass { get; private set; } = null!;
    public static IBrush GlassStrong { get; private set; } = null!;
    public static IBrush GlassBorder { get; private set; } = null!;
    public static IBrush DropOverlay { get; private set; } = null!;
    public static IBrush OnAccent { get; private set; } = null!;
    public static IBrush Danger { get; private set; } = null!;
    public static IBrush DangerSoft { get; private set; } = null!;
    public static IBrush Success { get; private set; } = null!;
    public static IBrush Running { get; private set; } = null!;
    public static IBrush CodeBackground { get; private set; } = null!;
    public static IBrush InlineCode { get; private set; } = null!;
    public static IBrush InlineCodeText { get; private set; } = null!;
    public static IBrush Link { get; private set; } = null!;
    public static IBrush Reasoning { get; private set; } = null!;
    public static BoxShadows CardShadow { get; private set; }
    public static readonly FontFamily Mono = FontFamily.Parse("Cascadia Mono, Consolas, D2Coding, monospace");
    public static readonly FontFamily UiFont = FontFamily.Parse("Pretendard, Segoe UI Variable Text, Segoe UI, Malgun Gothic");

    static Palette() => Apply("dark");

    public static void Apply(string theme)
    {
        IsDark = theme != "light";
        string Pick(string light, string dark) => IsDark ? dark : light;
        Background = Brush(Pick("#F3F3F1", "#080808"));
        Panel = Brush(Pick("#FCFCFA", "#111111"));
        Raised = Brush(Pick("#ECECE8", "#1B1B1B"));
        Hover = Brush(Pick("#E6E6E1", "#242424"));
        Divider = Brush(Pick("#DCDCD6", "#2B2B2B"));
        White = Brush(Pick("#111111", "#F2F2EF"));
        Muted = Brush(Pick("#6B6B66", "#9A9A94"));
        Accent = Brush(Pick("#161616", "#F0F0EC"));
        AccentSoft = Brush(Pick("#E9E9E4", "#202020"));
        Glass = Brush(Pick("#E8FCFCFA", "#E8101010"));
        GlassStrong = Brush(Pick("#F5FCFCFA", "#F2111111"));
        GlassBorder = Brush(Pick("#B8CBCBC6", "#A83A3A3A"));
        DropOverlay = Brush(Pick("#F7FCFCFA", "#F20B0B0B"));
        OnAccent = Brush(Pick("#FFFFFF", "#0A0A0A"));
        Danger = Brush(Pick("#DC2626", "#F26464"));
        DangerSoft = Brush(Pick("#FEF2F2", "#3A1F22"));
        Success = Brush(Pick("#16A34A", "#3DD68C"));
        Running = Brush(Pick("#D97706", "#F5B544"));
        CodeBackground = Brush(Pick("#EDEDE9", "#050505"));
        InlineCode = Brush(Pick("#E6E6E1", "#242424"));
        InlineCodeText = Brush(Pick("#242424", "#E2E2DD"));
        Link = Brush(Pick("#303030", "#D4D4CF"));
        Reasoning = Brush(Pick("#6B6B66", "#92928C"));
        CardShadow = BoxShadows.Parse(IsDark ? "0 8 28 0 #28000000" : "0 10 28 0 #0A000000, 0 1 2 0 #08000000");
    }

    /// <summary>Hover/pressed states for the button roles, and the Fluent accent, so stock controls match.</summary>
    public static void InstallStyles(Application app, FluentTheme fluent)
    {
        app.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        app.Resources["SiBackgroundBrush"] = Background;
        app.Resources["SiPanelBrush"] = Panel;
        app.Resources["SiRaisedBrush"] = Raised;
        app.Resources["SiDividerBrush"] = Divider;
        app.Resources["SiForegroundBrush"] = White;
        app.Resources["SiMutedBrush"] = Muted;
        app.Resources["SiAccentBrush"] = Accent;
        app.Resources["SiAccentSoftBrush"] = AccentSoft;
        app.Resources["SiOnAccentBrush"] = OnAccent;
        var onAccent = ((ISolidColorBrush)OnAccent).Color;
        app.Resources["SiOnAccentSoftBrush"] = new SolidColorBrush(
            Color.FromArgb(36, onAccent.R, onAccent.G, onAccent.B));
        app.Resources["SiDangerBrush"] = Danger;
        app.Resources["SiDangerSoftBrush"] = DangerSoft;
        app.Resources["SiGlassBrush"] = GlassStrong;
        app.Resources["SiGlassBorderBrush"] = GlassBorder;
        app.Resources["SiDropOverlayBrush"] = DropOverlay;
        app.Resources["SiSidebarWidth"] = 232d;
        app.Resources["SiContentMaxWidth"] = 920d;
        app.Resources["SiPageMargin"] = new Thickness(32, 28, 32, 52);
        app.Resources["SiCompactPageMargin"] = new Thickness(24, 22, 24, 32);
        var accent = ((ISolidColorBrush)Accent).Color;
        fluent.Palettes[IsDark ? ThemeVariant.Dark : ThemeVariant.Light] = new ColorPaletteResources { Accent = accent };
        void State(string role, string pseudo, IBrush background, IBrush? foreground = null)
        {
            var style = new Style(x => x.OfType<Button>().Class(role).Class(pseudo).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"));
            style.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, background));
            if (foreground != null) style.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, foreground));
            app.Styles.Add(style);
        }
        var accentHover = Brush(IsDark ? "#DADAD5" : "#303030");
        State("primary", ":pointerover", accentHover, OnAccent);
        State("primary", ":pressed", accentHover, OnAccent);
        State("secondary", ":pointerover", Hover, White);
        State("secondary", ":pressed", Hover, White);
        State("ghost", ":pointerover", Hover, White);
        State("ghost", ":pressed", Hover, White);
        State("nav", ":pointerover", Hover);
        State("danger", ":pointerover", DangerSoft, Danger);

        var textBoxStyle = new Style(x => x.OfType<TextBox>());
        textBoxStyle.Setters.Add(new Setter(TextBox.BackgroundProperty, Raised));
        textBoxStyle.Setters.Add(new Setter(TextBox.ForegroundProperty, White));
        textBoxStyle.Setters.Add(new Setter(TextBox.BorderBrushProperty, Divider));
        textBoxStyle.Setters.Add(new Setter(TextBox.BorderThicknessProperty, new Thickness(1)));
        textBoxStyle.Setters.Add(new Setter(TextBox.CornerRadiusProperty, new CornerRadius(8)));
        textBoxStyle.Setters.Add(new Setter(TextBox.PaddingProperty, new Thickness(10, 8)));
        textBoxStyle.Setters.Add(new Setter(TextBox.FontSizeProperty, 12.5));
        app.Styles.Add(textBoxStyle);

        var comboStyle = new Style(x => x.OfType<ComboBox>());
        comboStyle.Setters.Add(new Setter(ComboBox.BackgroundProperty, Raised));
        comboStyle.Setters.Add(new Setter(ComboBox.ForegroundProperty, White));
        comboStyle.Setters.Add(new Setter(ComboBox.BorderBrushProperty, Divider));
        comboStyle.Setters.Add(new Setter(ComboBox.BorderThicknessProperty, new Thickness(1)));
        comboStyle.Setters.Add(new Setter(ComboBox.CornerRadiusProperty, new CornerRadius(8)));
        comboStyle.Setters.Add(new Setter(ComboBox.PaddingProperty, new Thickness(10, 7)));
        comboStyle.Setters.Add(new Setter(ComboBox.MinHeightProperty, 38d));
        app.Styles.Add(comboStyle);

        // Fluent's monochrome accent can make scroll thumbs look like solid black bars in light mode.
        // Keep them neutral and restrained in both themes while preserving clear interaction states.
        var scrollThumbStyle = new Style(x => x.OfType<ScrollBar>().Template().OfType<Thumb>());
        scrollThumbStyle.Setters.Add(new Setter(Thumb.BackgroundProperty, Muted));
        scrollThumbStyle.Setters.Add(new Setter(Thumb.OpacityProperty, 0.42d));
        app.Styles.Add(scrollThumbStyle);

        var scrollThumbHoverStyle = new Style(x =>
            x.OfType<ScrollBar>().Template().OfType<Thumb>().Class(":pointerover"));
        scrollThumbHoverStyle.Setters.Add(new Setter(Thumb.BackgroundProperty, Muted));
        scrollThumbHoverStyle.Setters.Add(new Setter(Thumb.OpacityProperty, 0.72d));
        app.Styles.Add(scrollThumbHoverStyle);

        var scrollThumbPressedStyle = new Style(x =>
            x.OfType<ScrollBar>().Template().OfType<Thumb>().Class(":pressed"));
        scrollThumbPressedStyle.Setters.Add(new Setter(Thumb.BackgroundProperty, Accent));
        scrollThumbPressedStyle.Setters.Add(new Setter(Thumb.OpacityProperty, 0.82d));
        app.Styles.Add(scrollThumbPressedStyle);

        // Composer text box: borderless inside its card in every state (Fluent otherwise repaints focus/hover).
        foreach (var pseudo in new[] { "", ":pointerover", ":focus" })
        {
            var style = new Style(x =>
            {
                var box = x.OfType<TextBox>().Class("composer");
                return (pseudo.Length > 0 ? box.Class(pseudo) : box).Template().OfType<Border>().Name("PART_BorderElement");
            });
            style.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0)));
            app.Styles.Add(style);
        }
    }

    /// <summary>Rounded square silaa "Si" brand mark.</summary>
    public static Border Logo(double size = 28) => SilaaBrand.Mark(size);

    public static SolidColorBrush Brush(string value) => new(Color.Parse(value));

    /// <summary>Primary (filled accent) or secondary (outlined) action button.</summary>
    public static Button ActionButton(string title, bool strong)
    {
        var button = new SiButton(title, strong ? SiButtonVariant.Primary : SiButtonVariant.Secondary);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    /// <summary>Compact borderless button for inline actions (copy, view, remove…).</summary>
    public static Button SmallButton(string title)
    {
        return new SiButton(title, SiButtonVariant.Ghost, SiButtonSize.Small);
    }

    public static TextBlock SectionLabel(string text) => new()
    {
        Text = text, Foreground = Muted, FontWeight = FontWeight.SemiBold,
        FontSize = 11, Margin = new Thickness(0, 6, 0, 2)
    };

    /// <summary>Small rounded label (status, kind, counts).</summary>
    public static Border Pill(string text, IBrush foreground, IBrush background) => new()
    {
        Background = background, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = foreground, FontSize = 11, FontWeight = FontWeight.Medium }
    };

    /// <summary>White/raised surface with hairline border and soft shadow.</summary>
    public static Border Surface(Control child, double padding = 16, double radius = 12) =>
        new SiPanel(child, padding, radius, elevated: true);

    public static Border GlassSurface(Control child, double padding = 16, double radius = 12) =>
        new SiPanel(child, padding, radius, glass: true, elevated: true);

    /// <summary>Copies text and briefly confirms on the button itself.</summary>
    public static async Task CopyAsync(Button source, string text)
    {
        var clipboard = TopLevel.GetTopLevel(source)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(text);
        var original = source.Content;
        source.Content = "복사됨";
        await Task.Delay(1200);
        source.Content = original;
    }
}

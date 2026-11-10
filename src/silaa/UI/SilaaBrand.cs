using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace silaa.UI;

/// <summary>silaa wordmark and Si app mark, with theme-aware colours.</summary>
internal static class SilaaBrand
{
    public static Control WordmarkView(double height, IBrush? fill = null) => new Viewbox
    {
        Height = height, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock
        {
            Text = "silaa", FontFamily = new FontFamily("Segoe UI"), FontSize = 36,
            FontWeight = FontWeight.SemiBold, Foreground = fill ?? Palette.White
        }
    };

    public static Border Mark(double size, IBrush? tile = null, IBrush? glyph = null) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.26), Background = tile ?? Palette.White,
        Child = new TextBlock
        {
            Text = "Si", FontFamily = new FontFamily("Segoe UI"), FontSize = size * 0.60,
            FontWeight = FontWeight.Bold, Foreground = glyph ?? Palette.Panel,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        }
    };

    public static WindowIcon? WindowIcon()
    {
        try
        {
            const int size = 64;
            var mark = Mark(size, Palette.Brush("#18181B"), Palette.Brush("#FFFFFF"));
            mark.Measure(new Size(size, size));
            mark.Arrange(new Rect(0, 0, size, size));
            using var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
            bitmap.Render(mark);
            using var stream = new MemoryStream();
            bitmap.Save(stream, new PngBitmapEncoderOptions());
            stream.Position = 0;
            return new WindowIcon(stream);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { return null; }
    }

    public static WindowIcon? ApplicationIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://silaa/app.ico"));
            return new WindowIcon(stream);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { return WindowIcon(); }
    }
}

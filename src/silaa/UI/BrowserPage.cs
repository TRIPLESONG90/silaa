using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using silaa.ViewModels;

namespace silaa.UI;

public sealed partial class BrowserPage : UserControl
{
    private NativeWebView _browser = null!;
    private BrowserViewModel? _viewModel;
    private Action<string, string>? _showError;

    public BrowserPage() => AvaloniaXamlLoader.Load(this);

    public BrowserPage(NavigationViewModel navigation, Uri? initialUri, Action returnToPage,
        Action<string, string> showError) : this(navigation, initialUri, returnToPage, showError, false)
    {
    }

    internal BrowserPage(NavigationViewModel navigation, Uri? initialUri, Action returnToPage,
        Action<string, string> showError, bool previewMode) : this()
    {
        _browser = this.FindControl<NativeWebView>("Browser") ?? throw new InvalidOperationException("Browser not found.");
        if (previewMode && _browser.Parent is Grid browserHost)
        {
            browserHost.Children.Remove(_browser);
            var preview = new Border
            {
                Background = Palette.Background,
                Child = new StackPanel
                {
                    Width = 420,
                    Spacing = 10,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "SILAA BROWSER", FontSize = 10, LetterSpacing = 1.4,
                            FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Palette.Muted
                        },
                        new TextBlock { Text = "주소를 입력해 시작하세요.", FontSize = 24, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                        new TextBlock
                        {
                            Text = "프로젝트의 웹 자료와 생성한 로컬 앱을 이곳에서 확인할 수 있습니다.",
                            FontSize = 11.5, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Palette.Muted
                        }
                    }
                }
            };
            Grid.SetRow(preview, 1);
            browserHost.Children.Add(preview);
        }
        _showError = showError;
        _viewModel = new BrowserViewModel(navigation, Navigate, () => _browser.GoBack(), () => _browser.GoForward(),
            () => _browser.Refresh(), returnToPage)
        {
            Address = initialUri?.AbsoluteUri ?? ""
        };
        DataContext = _viewModel;
        if (previewMode) return;
        _browser.NavigationCompleted += (_, args) =>
        {
            _viewModel.Status = args.IsSuccess ? "로드 완료" : "로드 실패";
            if (_browser.Source is not null) _viewModel.Address = _browser.Source.AbsoluteUri;
        };
        if (initialUri is not null)
            Dispatcher.UIThread.Post(() => _browser.Navigate(initialUri), DispatcherPriority.Loaded);
        else
            ShowStartPage();
    }

    private void Navigate()
    {
        if (_viewModel is null) return;
        var text = _viewModel.Address.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file"))
        {
            _viewModel.Status = "주소 확인";
            _showError?.Invoke("주소를 확인하세요", "HTTP, HTTPS 또는 file 주소를 입력하세요.");
            return;
        }
        _viewModel.Status = "불러오는 중";
        _browser.Navigate(uri);
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter) return;
        args.Handled = true;
        Navigate();
    }

    private void ShowStartPage()
    {
        var background = Palette.IsDark ? "#080808" : "#F3F3F1";
        var foreground = Palette.IsDark ? "#F2F2EF" : "#111111";
        var muted = Palette.IsDark ? "#9A9A94" : "#6B6B66";
        var html = $"<html><body style='margin:0;font-family:Segoe UI,sans-serif;background:{background};color:{foreground};display:grid;place-items:center;height:100vh'><main style='width:420px'><div style='font-size:12px;letter-spacing:.14em;color:{muted};margin-bottom:18px'>SILAA BROWSER</div><h2 style='font-size:24px;margin:0 0 10px'>주소를 입력해 시작하세요.</h2><p style='color:{muted};line-height:1.7;margin:0'>프로젝트의 웹 자료와 생성한 로컬 앱을 이곳에서 확인할 수 있습니다.</p></main></body></html>";
        Dispatcher.UIThread.Post(() => _browser.NavigateToString(html), DispatcherPriority.Loaded);
    }
}

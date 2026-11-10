using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Markup.Xaml;
using System.Runtime.InteropServices;
using System.Text.Json;
using silaa.Services;
using silaa.UI;

namespace silaa;

internal static class Program
{
    internal static SingleInstanceCoordinator? SingleInstance { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            CrashLog.Write("AppDomain", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => CrashLog.Write("Task", eventArgs.Exception);
        using var instance = new SingleInstanceCoordinator();
        if (!instance.IsPrimary)
        {
            _ = instance.SignalPrimaryAsync().GetAwaiter().GetResult();
            return;
        }
        SingleInstance = instance;
        try
        {
            AppBuilder.Configure<SilaaApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance = null;
        }
    }
}

internal static class CrashLog
{
    private const int EAbort = unchecked((int)0x80004004);

    public static void Write(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.AppendAllText(Path.Combine(AppPaths.DataRoot, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {source}: {exception}\n\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static bool IsRecoverableUiException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not COMException { HResult: EAbort }) continue;
            var origin = $"{current.Source}\n{current.StackTrace}";
            if (origin.Contains("WebView2", StringComparison.OrdinalIgnoreCase) ||
                origin.Contains("NativeWebViewControlHost", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

public sealed partial class SilaaApplication : Application
{
    private LocalStore _store = null!;
    private TrayIcon? _trayIcon;
    private TrayIcons? _trayIcons;

    public override void Initialize()
    {
        _store = new LocalStore();
        Palette.Apply(_store.Settings.Theme);
        AvaloniaXamlLoader.Load(this);
        var fluent = Styles.OfType<FluentTheme>().Single();
        Palette.InstallStyles(this, fluent);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            var recoverable = CrashLog.IsRecoverableUiException(eventArgs.Exception);
            CrashLog.Write(recoverable ? "UI-WebView2-Abort" : "UI", eventArgs.Exception);
            if (recoverable) eventArgs.Handled = true;
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The main window can be hidden for background Agent/cron work. Only an explicit
            // tray-menu exit should terminate the process and dispose the tray icon.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var harness = new LlmHarness(_store);
            var window = new MainWindow(_store, harness);
            desktop.MainWindow = window;
            Program.SingleInstance?.SetActivationHandler(() =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => window.ShowFromTray()));
            var open = new NativeMenuItem("silaa 열기");
            open.Click += (_, _) => window.ShowFromTray();
            var tasks = new NativeMenuItem("백그라운드 작업 열기");
            tasks.Click += (_, _) => window.ShowFromTray(openAutomation: true);
            var exit = new NativeMenuItem("silaa 완전히 종료");
            exit.Click += (_, _) => window.RequestExit();
            var menu = new NativeMenu();
            menu.Items.Add(open);
            menu.Items.Add(tasks);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exit);
            _trayIcon = new TrayIcon
            {
                Icon = SilaaBrand.ApplicationIcon(),
                ToolTipText = "silaa · 백그라운드 실행 중",
                Menu = menu,
                IsVisible = true
            };
            _trayIcon.Clicked += (_, _) => window.ShowFromTray();
            _trayIcons = [_trayIcon];
            TrayIcon.SetIcons(this, _trayIcons);
            window.SetTrayNotification((title, message, warning) =>
            {
                if (_trayIcon is not null) WindowsNotification.Show(_trayIcon, title, message, warning);
            });
            window.Opened += (_, _) => _ = RunAutomaticUpdateAsync(desktop, window);
            desktop.Exit += (_, _) =>
            {
                TrayIcon.SetIcons(this, null);
                if (_trayIcon is not null) _trayIcon.IsVisible = false;
                _trayIcon?.Dispose();
                _trayIcon = null;
                _trayIcons = null;
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task RunAutomaticUpdateAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window)
    {
        if (!_store.Settings.AutoUpdateEnabled) return;
        try
        {
            var updater = new ReleaseUpdateService(_store.Root);
            var previousFailure = updater.ReadFailureMarker();
            if (previousFailure is not null)
            {
                if (_trayIcon is not null)
                    WindowsNotification.Show(_trayIcon, "자동 업데이트 일시 중지",
                        "이전 업데이트가 실패해 재실행 루프를 막았습니다. 설정에서 자동 업데이트를 다시 켜면 재시도합니다.", true);
                CrashLog.Write("AutoUpdate-Paused", new InvalidOperationException(previousFailure));
                return;
            }
            var check = await updater.CheckAsync();
            if (check.Release is null) return;
            if (_trayIcon is not null)
                WindowsNotification.Show(_trayIcon, "silaa 업데이트", $"{check.Release.Tag}을 다운로드하고 있습니다.", false);
            var package = await updater.DownloadAndVerifyAsync(check.Release);
            if (!updater.LaunchInstaller(package))
            {
                if (_trayIcon is not null)
                    WindowsNotification.Show(_trayIcon, "업데이트 준비 완료",
                        "패키지를 검증했지만 현재 실행 위치에서는 자동 교체할 수 없습니다.", true);
                return;
            }
            if (_trayIcon is not null)
                WindowsNotification.Show(_trayIcon, "silaa 업데이트",
                    "검증이 끝났습니다. 업데이트 후 자동으로 다시 실행합니다.", false);
            await Task.Delay(350);
            desktop.Shutdown();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or
                                           InvalidOperationException or TaskCanceledException)
        {
            CrashLog.Write("AutoUpdate", exception);
            if (_trayIcon is not null)
                WindowsNotification.Show(_trayIcon, "자동 업데이트 실패", exception.Message, true);
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Markup.Xaml;
using System.Runtime.InteropServices;
using silaa.Services;
using silaa.UI;

namespace silaa;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            CrashLog.Write("AppDomain", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => CrashLog.Write("Task", eventArgs.Exception);
        AppBuilder.Configure<SilaaApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
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
}

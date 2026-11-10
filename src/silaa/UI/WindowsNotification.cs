using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace silaa.UI;

internal static class WindowsNotification
{
    private const uint NimModify = 1;
    private const uint NifInfo = 0x10;
    private const uint NiifUser = 0x4;
    private const uint NiifLargeIcon = 0x20;

    public static void Show(TrayIcon trayIcon, string title, string message, bool warning = false)
    {
        if (!OperatingSystem.IsWindows() ||
            !TryGetNativeIdentity(trayIcon, out var handle, out var id)) return;

        var icons = LoadApplicationIcons();
        var data = Create(handle, id);
        data.uFlags = NifInfo;
        data.szInfoTitle = Limit(title, 63);
        data.szInfo = Limit(message, 255);
        data.dwInfoFlags = NiifUser | NiifLargeIcon;
        data.hBalloonIcon = icons.Large != IntPtr.Zero ? icons.Large : icons.Small;
        data.uTimeoutOrVersion = 10_000;
        if (!ShellNotifyIcon(NimModify, ref data))
        {
            icons.Dispose();
            return;
        }

        _ = ReleaseIconsLaterAsync(icons);
    }

    private static bool TryGetNativeIdentity(TrayIcon trayIcon, out IntPtr handle, out uint id)
    {
        handle = IntPtr.Zero;
        id = 0;
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var implementation = typeof(TrayIcon).GetField("_impl", flags)?.GetValue(trayIcon);
            if (implementation is null ||
                implementation.GetType().FullName != "Avalonia.Win32.TrayIconImpl") return false;

            var uniqueId = implementation.GetType().GetField("_uniqueId", flags)?.GetValue(implementation);
            if (uniqueId is not int nativeId || nativeId <= 0) return false;

            var platformType = implementation.GetType().Assembly.GetType("Avalonia.Win32.Win32Platform");
            var platform = platformType?.GetProperty("Instance", flags)?.GetValue(null);
            var nativeHandle = platformType?.GetProperty("Handle", flags)?.GetValue(platform);
            if (nativeHandle is not IntPtr platformHandle || platformHandle == IntPtr.Zero) return false;

            handle = platformHandle;
            id = unchecked((uint)nativeId);
            return true;
        }
        catch (Exception exception) when (exception is ReflectionTypeLoadException or
                                           TargetInvocationException or InvalidCastException)
        {
            return false;
        }
    }

    private static async Task ReleaseIconsLaterAsync(IconHandles icons)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15)); }
        finally { icons.Dispose(); }
    }

    private static IconHandles LoadApplicationIcons()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var count = ExtractIconEx(executablePath, 0, out var large, out var small, 1);
            if (count > 0 && (large != IntPtr.Zero || small != IntPtr.Zero))
                return new IconHandles(large, small, ownsHandles: true);

            DestroyExtractedIcons(large, small);
        }
        return new IconHandles(IntPtr.Zero, IntPtr.Zero, ownsHandles: false);
    }

    private static void DestroyExtractedIcons(IntPtr large, IntPtr small)
    {
        if (small != IntPtr.Zero) DestroyIcon(small);
        if (large != IntPtr.Zero && large != small) DestroyIcon(large);
    }

    private static NotifyIconData Create(IntPtr handle, uint id) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = handle,
        uID = id,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string fileName,
        int iconIndex,
        out IntPtr largeIcon,
        out IntPtr smallIcon,
        uint iconCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    private sealed class IconHandles(IntPtr large, IntPtr small, bool ownsHandles) : IDisposable
    {
        private int _disposed;

        public IntPtr Large { get; } = large;
        public IntPtr Small { get; } = small;

        public void Dispose()
        {
            if (!ownsHandles || Interlocked.Exchange(ref _disposed, 1) != 0) return;
            DestroyExtractedIcons(Large, Small);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
}

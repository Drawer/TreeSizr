using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace TreeSizr;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Applies the light or dark color set (Themes/Light.xaml or Themes/Dark.xaml) and the matching title bar.
/// With <see cref="AppTheme.System"/> it follows the Windows app mode, also when that changes while running.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _colors;

    public static bool IsDark { get; private set; }

    public static void Initialize()
    {
        Apply();
        AppSettings.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.Theme)) Apply();
        };
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && AppSettings.Current.Theme == AppTheme.System)
                Application.Current.Dispatcher.BeginInvoke(Apply);
        };
    }

    private static void Apply()
    {
        bool dark = AppSettings.Current.Theme switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => SystemUsesDarkMode(),
        };
        if (_colors is not null && dark == IsDark) return;
        IsDark = dark;

        var colors = new ResourceDictionary { Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_colors is not null) merged.Remove(_colors);
        merged.Insert(0, colors);
        _colors = colors;

        foreach (Window window in Application.Current.Windows)
            ApplyTitleBar(window);
    }

    /// <summary>Gives the window a dark or light title bar. Call from OnSourceInitialized, before it is shown.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int)); // fails harmlessly before Windows 10 20H1
    }

    private static bool SystemUsesDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

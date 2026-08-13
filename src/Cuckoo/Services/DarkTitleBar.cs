using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Cuckoo.Services;

/// <summary>
/// Dark window title bar support for Windows 10 / Server 2022.
/// The WPF Fluent theme only adapts the title bar on Windows 11; on Win10-family
/// systems the DWM attribute has to be set explicitly.
/// </summary>
public static class DarkTitleBar
{
    // build 18985+ (Win10 2004, Server 2022, Win11); older 1809/1903 builds used 19
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>True when Windows is set to dark mode for apps.</summary>
    public static bool IsSystemDarkMode()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Applies the current system light/dark preference to the window's title bar.</summary>
    public static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        int useDark = IsSystemDarkMode() ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref useDark, sizeof(int));
    }

    /// <summary>Applies now and re-applies whenever the user switches the system theme.</summary>
    public static void Attach(Window window)
    {
        Apply(window);
        UserPreferenceChangedEventHandler handler = (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
                window.Dispatcher.BeginInvoke(() => Apply(window));
        };
        SystemEvents.UserPreferenceChanged += handler;
        window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= handler;
    }
}

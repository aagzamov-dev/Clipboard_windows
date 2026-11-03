using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Clipboard.Windows.Native;

// Keep a normal HWND: layered (AllowsTransparency) windows cannot use DWM acrylic.
internal static class WindowBackdrop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    internal static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
    }

    internal static void Apply(Window window)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        var app = (App)Application.Current;
        int dark = app.DarkTheme ? 1 : 0, rounded = 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int));
        bool transparency = true;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            if (key?.GetValue("EnableTransparency") is int enabled) transparency = enabled != 0;
        int backdrop = !SystemParameters.HighContrast && transparency && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) ? 3 : 1;
        bool acrylic = DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int)) == 0 && backdrop == 3;
        var margins = new Margins { Left = acrylic ? -1 : 0, Right = acrylic ? -1 : 0, Top = acrylic ? -1 : 0, Bottom = acrylic ? -1 : 0 };
        acrylic &= DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0;
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = acrylic ? Colors.Transparent : ((SolidColorBrush)app.Resources["Surface"]).Color;
        window.Background = acrylic ? Brushes.Transparent : (Brush)app.Resources["Surface"];
        window.Resources["WindowTint"] = acrylic ? app.Resources["AcrylicTint"] : app.Resources["Surface"];
    }
}

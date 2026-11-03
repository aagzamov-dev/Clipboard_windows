using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Clipboard.Core;

namespace Clipboard.Windows.Native;

internal static class PopupPlacement
{
    internal static void Position(nint popup, double width, double height)
    {
        if (!NativeMethods.GetCursorPos(out var pointer)) throw new InvalidOperationException("Cannot determine the pointer position.");
        nint monitor = NativeMethods.MonitorFromPoint(pointer, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(), Device = "" };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("Cannot determine the pointer monitor’s working area.");
        SetPosition(popup, info.Work.Left, info.Work.Top, 1, 1);
        double scale = Math.Max(96, NativeMethods.GetDpiForWindow(popup)) / 96.0;
        var bounds = PopupGeometry.Place(new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top),
            pointer.X, pointer.Y, scale, width, height);
        SetPosition(popup, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    internal static string MonitorKey(nint window = default)
    {
        NativeMethods.GetCursorPos(out var pointer);
        nint monitor = window == 0 ? NativeMethods.MonitorFromPoint(pointer, 2) : NativeMethods.MonitorFromWindow(window, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(), Device = "" };
        return NativeMethods.GetMonitorInfo(monitor, ref info) ? info.Device : "Default";
    }

    private static void SetPosition(nint popup, int x, int y, int width, int height)
    {
        if (!NativeMethods.SetWindowPos(popup, 0, x, y, width, height, 0x0014))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to position Clipboard.");
    }
}

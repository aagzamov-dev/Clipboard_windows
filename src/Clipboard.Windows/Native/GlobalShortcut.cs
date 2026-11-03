using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Clipboard.Core;

namespace Clipboard.Windows.Native;

internal sealed class GlobalShortcut : IDisposable
{
    private readonly HwndSource source;
    private readonly Action onPressed;
    private int activeId;
    private int nextId = 1;
    private Shortcut? activeShortcut;

    internal GlobalShortcut(nint window, Action onPressed)
    {
        source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("Popup handle is unavailable.");
        this.onPressed = onPressed;
        source.AddHook(HandleMessage);
    }

    internal void Apply(Shortcut shortcut, Action persist)
    {
        shortcut.Validate();
        if (shortcut == activeShortcut) { persist(); return; }
        int candidate = nextId;
        if (!NativeMethods.RegisterHotKey(source.Handle, candidate, (uint)shortcut.Modifiers | 0x4000, shortcut.Key))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "That shortcut is unavailable. Choose another combination.");
        try { persist(); }
        catch { NativeMethods.UnregisterHotKey(source.Handle, candidate); throw; }

        if (activeId != 0) NativeMethods.UnregisterHotKey(source.Handle, activeId);
        activeId = candidate;
        nextId = candidate == 1 ? 2 : 1;
        activeShortcut = shortcut;
    }

    private nint HandleMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == activeId && activeId != 0)
        {
            handled = true;
            onPressed();
        }
        return 0;
    }

    public void Dispose()
    {
        if (activeId != 0) NativeMethods.UnregisterHotKey(source.Handle, activeId);
        activeId = 0;
        source.RemoveHook(HandleMessage);
    }
}

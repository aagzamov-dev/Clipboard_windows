using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Clipboard.Windows.Native;

internal sealed class ForegroundTracker : IDisposable
{
    private delegate void WinEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
    private readonly WinEvent callback;
    private readonly nint hook;
    private nint previous;
    private uint previousProcess;

    internal ForegroundTracker()
    {
        callback = (_, _, window, _, _, _, _) => Remember(window);
        Remember(NativeMethods.GetForegroundWindow());
        hook = SetWinEventHook(3, 3, 0, callback, 0, 0, 2);
    }

    internal nint Destination(bool allowPrevious)
    {
        nint current = NativeMethods.GetForegroundWindow();
        if (IsDestination(current)) return current;
        NativeMethods.GetWindowThreadProcessId(previous, out uint pid);
        return allowPrevious && IsDestination(previous) && pid == previousProcess ? previous : 0;
    }

    private void Remember(nint window)
    {
        if (!IsDestination(window)) return;
        previous = window; NativeMethods.GetWindowThreadProcessId(window, out previousProcess);
    }

    private static bool IsDestination(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindow(window)) return false;
        NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        if (pid == Environment.ProcessId) return false;
        var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
        return name.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW");
    }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int max);
    public void Dispose() { if (hook != 0) UnhookWinEvent(hook); }
}

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Clipboard.Core;

namespace Clipboard.Windows.Native;

internal sealed class PasteCoordinator(ClipboardService clipboard)
{
    internal nint Target { get; private set; }
    private uint targetProcess;
    private CancellationTokenSource? pending;
    private bool busy;
    internal string LastStatus { get; private set; } = "No paste attempted.";

    internal void Remember(nint window)
    {
        Cancel(); Target = window;
        NativeMethods.GetWindowThreadProcessId(window, out targetProcess);
    }

    internal void Cancel() { pending?.Cancel(); }

    internal async Task<string> Paste(ClipboardItem item, bool plain, Action dismiss)
    {
        if (busy) return "A paste is already in progress.";
        busy = true; pending?.Dispose(); pending = new CancellationTokenSource(); var cancellation = pending.Token;
        nint target = Target; uint pid = targetProcess;
        bool ValidTarget()
        {
            NativeMethods.GetWindowThreadProcessId(target, out uint currentPid);
            return target != 0 && NativeMethods.IsWindow(target) && currentPid == pid && pid != Environment.ProcessId;
        }
        try
        {
            if (!ValidTarget()) return LastStatus = "The destination app is no longer available. Use Copy, then paste manually.";
            dismiss();
            if (NativeMethods.GetForegroundWindow() != target && !NativeMethods.SetForegroundWindow(target))
                return LastStatus = "Windows did not restore the destination. Use Copy, then Ctrl+V.";
            // Wait for both activation and released modifiers; never synthesize key-up for the user’s keys.
            for (int attempt = 0; attempt < 40; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!ValidTarget()) return LastStatus = "Destination closed before paste.";
                if (NativeMethods.GetForegroundWindow() == target && ModifiersReleased()) break;
                await Task.Delay(15, cancellation);
            }
            if (NativeMethods.GetForegroundWindow() != target || !ModifiersReleased())
                return LastStatus = "Paste stopped because focus or modifier keys were not ready.";
            uint generation = await clipboard.Write(item, plain, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!ValidTarget() || NativeMethods.GetForegroundWindow() != target || !ModifiersReleased() || NativeMethods.GetClipboardSequenceNumber() != generation)
                return LastStatus = "Clipboard or focus changed. Paste manually with Ctrl+V.";
            var inputs = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
            uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
            if (sent > 0 && sent < inputs.Length)
            {
                var releases = new[] { Key(0x56, true), Key(0x11, true) };
                NativeMethods.SendInput((uint)releases.Length, releases, Marshal.SizeOf<NativeMethods.Input>());
            }
            return LastStatus = sent == inputs.Length
                ? "Ctrl+V sent once. The destination controls whether it accepts the paste."
                : "Windows blocked automatic paste. Elevated apps may require manual Ctrl+V. Content was copied.";
        }
        catch (OperationCanceledException) { return LastStatus = "Paste cancelled."; }
        finally { busy = false; }
    }

    private static bool ModifiersReleased()
    {
        foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }) if ((NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0) return false;
        return true;
    }

    private static NativeMethods.Input Key(ushort code, bool up) => new()
    {
        Type = 1,
        Data = new() { Keyboard = new() { Key = code, Flags = up ? 2u : 0u } }
    };
}

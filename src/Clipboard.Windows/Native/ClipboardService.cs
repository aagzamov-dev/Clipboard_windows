using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipboard.Core;
using Board = System.Windows.Clipboard;

namespace Clipboard.Windows.Native;

internal sealed class ClipboardService : IDisposable
{
    private const string OwnFormat = "Clipboard.Windows.WriteToken";
    private readonly TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly Func<AppSettings> settings;
    private readonly Action<ClipboardItem> captured;
    private readonly Action<string> report;
    private HwndSource? source;
    private readonly SemaphoreSlim writes = new(1, 1);
    private bool reading;
    private volatile bool stopped;
    private volatile bool enabled;
    private DateTimeOffset pauseUntil;
    private bool manualPause;
    private uint observed;
    public bool IsPaused => manualPause || pauseUntil > DateTimeOffset.UtcNow;

    internal ClipboardService(Func<AppSettings> settings, Action<ClipboardItem> captured, Action<string> report)
    {
        this.settings = settings; this.captured = captured; this.report = report;
        thread = new Thread(Run) { IsBackground = true, Name = "Clipboard STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }

    private void Run()
    {
        try
        {
            source = new HwndSource(new HwndSourceParameters("Clipboard listener") { ParentWindow = -3 });
            source.AddHook(HandleMessage);
            if (!NativeMethods.AddClipboardFormatListener(source.Handle)) throw new InvalidOperationException("Windows could not enable clipboard notifications.");
            observed = NativeMethods.GetClipboardSequenceNumber();
            ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run();
        }
        catch (Exception error) { ready.TrySetException(error); report(error.Message); }
        finally
        {
            if (source is not null)
            {
                NativeMethods.RemoveClipboardFormatListener(source.Handle); source.Dispose();
            }
        }
    }

    internal async Task Configure(bool record, bool paused = false, DateTimeOffset until = default)
    {
        var dispatcher = await ready.Task;
        await dispatcher.InvokeAsync(() =>
        {
            enabled = record; manualPause = paused; pauseUntil = until;
            // Never capture pre-existing contents when starting or resuming.
            observed = NativeMethods.GetClipboardSequenceNumber();
        });
    }

    private nint HandleMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x031D && !reading && !stopped)
            _ = Capture();
        return 0;
    }

    private async Task Capture()
    {
        if (reading || stopped) return;
        reading = true;
        try
        {
            for (int attempt = 0; attempt < 5 && !stopped; attempt++)
            {
                uint sequence = NativeMethods.GetClipboardSequenceNumber();
                if (sequence == observed) return;
                var config = settings();
                if (!enabled || !config.OnboardingComplete || !config.StartMonitoring || IsPaused) { observed = sequence; return; }
                try
                {
                    var item = Read(config);
                    if (NativeMethods.GetClipboardSequenceNumber() != sequence) { await Task.Delay(25); continue; }
                    observed = sequence;
                    if (item is not null && enabled && !stopped && !IsPaused) captured(item);
                    return;
                }
                catch (Exception error) when (error is COMException or ExternalException)
                {
                    if (attempt == 4) { observed = sequence; report("Clipboard was busy. The next copy will be captured."); }
                    else await Task.Delay(25 * (attempt + 1));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or NotSupportedException or OutOfMemoryException)
                {
                    observed = sequence; report($"Skipped clipboard item: {error.Message}"); return;
                }
            }
        }
        catch (Exception error) { report($"Clipboard capture stopped for this item: {error.Message}"); }
        finally { reading = false; }
    }

    private ClipboardItem? Read(AppSettings config)
    {
        var data = Board.GetDataObject();
        if (data is null || data.GetDataPresent(OwnFormat, false) ||
            data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false) ||
            data.GetDataPresent("Clipboard Viewer Ignore", false) ||
            data.GetDataPresent("org.nspasteboard.ConcealedType", false) ||
            data.GetDataPresent("com.agilebits.onepassword", false)) return null;
        if (data.GetDataPresent("CanIncludeInClipboardHistory", false))
        {
            var flag = data.GetData("CanIncludeInClipboardHistory", false);
            if (flag is MemoryStream memory && (memory.Length != 4 || memory.ToArray().All(b => b == 0)) || flag is int number && number == 0 || flag is uint unsigned && unsigned == 0) return null;
        }
        string owner = ProcessName(NativeMethods.GetClipboardOwner());
        string foreground = ProcessName(NativeMethods.GetForegroundWindow());
        if (config.ExcludedApps.Any(excluded => new[] { owner, foreground }.Any(name =>
            string.Equals(Path.GetFileNameWithoutExtension(excluded), Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)))) return null;
        ClipboardItem? item = null;
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            item = new() { Kind = ItemKind.Files, Files = files, Source = owner };
        else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text)
        {
            string? rtf = data.GetDataPresent(DataFormats.Rtf, false) ? data.GetData(DataFormats.Rtf, false) as string : null;
            if (rtf is not null && System.Text.Encoding.UTF8.GetByteCount(rtf) > Limits.TextBytes) rtf = null;
            item = ClipboardItem.FromText(text) with { Rtf = rtf, Source = owner };
        }
        else if (data.GetDataPresent("PNG", false) && data.GetData("PNG", false) is MemoryStream png)
        {
            if (png.Length > Limits.ImageBytes) throw new InvalidDataException("Image exceeds 20 MB.");
            item = ImageItem(png, owner);
        }
        else if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight > Limits.ImagePixels) throw new InvalidDataException("Image exceeds 64 million pixels.");
            using var stream = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
            item = new() { Kind = ItemKind.Image, Image = stream.ToArray(), Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, Source = owner };
        }
        item?.Validate(); return item;
    }

    private static ClipboardItem ImageItem(MemoryStream stream, string owner)
    {
        byte[] bytes = stream.ToArray();
        using var input = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var image = decoder.Frames[0];
        return new() { Kind = ItemKind.Image, Image = bytes, Width = image.PixelWidth, Height = image.PixelHeight, Source = owner };
    }

    internal static string ProcessName(nint window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName + ".exe"; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return "Unknown app"; }
    }

    internal async Task<uint> Write(ClipboardItem item, bool plainText, CancellationToken cancellation = default)
    {
        item.Validate(Limits.DraftBytes);
        await writes.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            var dispatcher = await ready.Task; cancellation.ThrowIfCancellationRequested();
            return await (await dispatcher.InvokeAsync(() => WriteOnThread(item, plainText, cancellation), DispatcherPriority.Normal, cancellation));
        }
        finally { writes.Release(); }
    }

    private async Task<uint> WriteOnThread(ClipboardItem item, bool plainText, CancellationToken cancellation)
    {
        byte[] token = Guid.NewGuid().ToByteArray();
        var data = new DataObject();
        data.SetData(OwnFormat, new MemoryStream(token, writable: false), false);
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]), false);
        if (item.IsText)
        {
            data.SetText(item.Text!, TextDataFormat.UnicodeText);
            if (!plainText && item.Rtf is not null) data.SetData(DataFormats.Rtf, item.Rtf);
        }
        else if (item.Kind == ItemKind.Files)
        {
            if (item.Files!.Any(path => !File.Exists(path) && !Directory.Exists(path))) throw new FileNotFoundException("A referenced file has moved or is no longer available.");
            var files = new StringCollection(); files.AddRange(item.Files!); data.SetFileDropList(files);
        }
        else
        {
            using var stream = new MemoryStream(item.Image!);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            data.SetImage(bitmap); data.SetData("PNG", new MemoryStream(item.Image!, writable: false), false);
        }
        for (int attempt = 0; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try { Board.SetDataObject(data, copy: true); break; }
            catch (ExternalException) when (attempt < 4) { await Task.Delay(30 * (attempt + 1), cancellation); }
        }
        cancellation.ThrowIfCancellationRequested();
        uint sequence = NativeMethods.GetClipboardSequenceNumber();
        // Verification failure must never retry a successful write over another app's later copy.
        var current = Board.GetDataObject();
        if (current?.GetData(OwnFormat, false) is not MemoryStream marker || !marker.ToArray().SequenceEqual(token) || NativeMethods.GetClipboardSequenceNumber() != sequence)
            throw new InvalidOperationException("Another app changed the clipboard before the copy completed.");
        return sequence;
    }

    public void Dispose()
    {
        stopped = true; enabled = false;
        if (ready.Task.IsCompletedSuccessfully) ready.Task.Result.BeginInvokeShutdown(DispatcherPriority.Send);
        // A third-party delayed clipboard renderer must never block application exit.
        thread.Join(500);
    }
}

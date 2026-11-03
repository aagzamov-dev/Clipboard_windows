using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Clipboard.Core;
using Clipboard.Windows;
using Clipboard.Windows.Native;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private sealed class OffscreenApp : App
    {
        // WPF schedules OnStartup when the dispatcher pumps even without Application.Run.
        // Component tests must never start capture or open the real user's history.
        protected override void OnStartup(StartupEventArgs args) { }
    }
    private static int checks;
    private static string? renderDirectory;
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--render") { renderDirectory = Path.GetFullPath(args[1]); Directory.CreateDirectory(renderDirectory); }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = Run(); var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
        Console.WriteLine($"All {checks} Windows component checks passed (offscreen; no live window or clipboard access).");
    }

    private static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"clipboard-images-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var app = new OffscreenApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clipboard;component/ModernControls.xaml", UriKind.Relative) });
            app.ApplyTheme();
            using (var icon = Application.GetResourceStream(new Uri("pack://application:,,,/Clipboard;component/Assets/Clipboard.ico")).Stream)
                Check(icon.Length > 0, "Tray icon resolves from the app assembly when hosted by component tests");
            using var store = new HistoryStore(Path.Combine(directory, "history.db"));
            typeof(App).GetProperty("History", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, store);
            CheckNativeResize();
            byte[] png = CreatePng(1600, 800);
            string imagePath = Path.Combine(directory, "picture.png"); File.WriteAllBytes(imagePath, png);
            var image = new ClipboardItem { Kind = ItemKind.Image, Image = png, Width = 1600, Height = 800 };
            var file = new ClipboardItem { Kind = ItemKind.Files, Files = [imagePath] };
            var text = ClipboardItem.FromText("Selection changed to text");
            foreach (var item in new[] { image, file, text }) await store.Capture(item);
            var bitmap = await ImageContent.Load(store, image.Id, 160, default);
            Check(bitmap is { IsFrozen: true, PixelWidth: 160, PixelHeight: 80 }, "Stored image thumbnail is frozen and bounded to 160 pixels");
            bitmap = await ImageContent.Load(file, 1024, default);
            Check(bitmap is { IsFrozen: true, PixelWidth: 1024, PixelHeight: 512 }, "Explorer PNG reference decodes from a guarded local handle");
            Check((await store.Get(file.Id)).Kind == ItemKind.Files && (await store.Get(file.Id)).Files!.Single() == imagePath, "Image file preview preserves file-reference Copy/Paste semantics");
            int stride = (bitmap!.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
            bitmap.CopyPixels(new byte[stride * bitmap.PixelHeight], stride, 0);
            Check(true, "Decoded image remains usable after its input handle closes");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { await ImageContent.Load(image, 160, cancellation.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { Check(true, "Cancelled image requests do not decode"); }
            }
            foreach (string remote in new[] { @"\\invalid.invalid\share\picture.png", "//invalid.invalid/share/picture.png", @"\\?\UNC\invalid.invalid\share\picture.png", @"\\.\C:\picture.png" })
                Check(await ImageContent.Load(file with { Files = [remote] }, 160, default) is null, "Remote/device image reference rejected before file access");
            string linked = Path.Combine(directory, "linked"); CreateJunction(linked, directory);
            Check(await ImageContent.Load(file with { Files = [Path.Combine(linked, "picture.png")] }, 160, default) is null, "Image references through linked parent directories are rejected");
            Directory.Delete(linked);
            string emptyPath = Path.Combine(directory, "empty.png"); File.WriteAllBytes(emptyPath, []);
            string largePath = Path.Combine(directory, "large.png");
            using (var large = File.Create(largePath)) large.SetLength(Limits.ImageBytes + 1L);
            foreach (string invalid in new[] { emptyPath, largePath })
            {
                try { await ImageContent.Load(file with { Files = [invalid] }, 160, default); throw new Exception("Invalid image was accepted"); }
                catch (InvalidDataException) { Check(true, "Empty/oversized local image fails without unbounded decode"); }
            }
            var emptyFile = file with { Id = Guid.NewGuid(), Files = [emptyPath] }; await store.Capture(emptyFile);
            var portrait = image with { Id = Guid.NewGuid(), Image = CreatePng(100, 300), Width = 100, Height = 300 }; await store.Capture(portrait);
            using (var source = new HwndSource(new HwndSourceParameters("Clipboard offscreen component") { WindowStyle = unchecked((int)0x80000000), Width = 200, Height = 100 }))
            {
                // A hidden composition target exercises WPF load/visibility without showing a window.
                var thumbnail = new Image(); var root = new Border { Child = thumbnail };
                var thumbnailRows = await store.Query(new());
                HistoryThumbnail.SetItem(thumbnail, thumbnailRows.Single(row => row.Id == image.Id));
                source.RootVisual = root; root.Measure(new Size(200, 100)); root.Arrange(new Rect(0, 0, 200, 100));
                await WaitFor(() => thumbnail.Source is not null);
                Check(thumbnail.Source is BitmapSource { PixelWidth: 160, PixelHeight: 80 }, "Actual thumbnail attachment loads a visible realized image");
                root.Visibility = Visibility.Collapsed; await Task.Delay(30);
                Check(thumbnail.Source is null, "Hidden history pane cancels and releases its thumbnails");
                root.Visibility = Visibility.Visible; await WaitFor(() => thumbnail.Source is not null);
                Check(thumbnail.Source is BitmapSource, "Thumbnail reloads when its pane becomes visible again");
                HistoryThumbnail.SetItem(thumbnail, thumbnailRows.Single(row => row.Id == emptyFile.Id));
                await WaitFor(() => thumbnail.ToolTip is string);
                Check(thumbnail.Source is null, "Invalid image thumbnail is contained at the async UI boundary");
                HistoryThumbnail.SetItem(thumbnail, thumbnailRows.Single(row => row.Id == image.Id));
                HistoryThumbnail.SetItem(thumbnail, thumbnailRows.Single(row => row.Id == portrait.Id));
                await WaitFor(() => thumbnail.Source is BitmapSource { PixelHeight: 160 });
                Check(thumbnail.Source is BitmapSource { PixelWidth: 53, PixelHeight: 160 }, "Recycled row cannot display the previous image's completion");
                source.RootVisual = null;
            }
            var window = new HistoryWindow();
            var keepOpen = (ToggleButton)window.FindName("KeepOpenButton");
            int interaction = (int)typeof(HistoryWindow).GetField("interactionVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            int selection = (int)typeof(HistoryWindow).GetField("selectionVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            keepOpen.IsChecked = true; keepOpen.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Invoke(window, "OnDeactivated", window, EventArgs.Empty);
            Check(window.KeepOpen && window.Topmost && (int)typeof(HistoryWindow).GetField("selectionVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! == selection, "Keep open remains above apps and bypasses deactivation dismissal");
            Check((int)typeof(HistoryWindow).GetField("interactionVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! > interaction, "Focus changes invalidate pending actions even while keep open remains enabled");
            Invoke(window, "DismissForPaste");
            Check(window.KeepOpen && (int)typeof(HistoryWindow).GetField("selectionVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! == selection, "Pasting does not dismiss a kept-open picker");
            var paste = new PasteCoordinator(null!);
            typeof(App).GetProperty("Paste", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, paste);
            paste.Remember((nint)123); Invoke(window, "RememberActivation", (nint)0);
            Check(paste.Target == 0, "Activation with no previous window clears a stale paste destination");
            using (var own = new HwndSource(new HwndSourceParameters("Clipboard activation component") { WindowStyle = unchecked((int)0x80000000), Width = 10, Height = 10 }))
            {
                paste.Remember((nint)123); Invoke(window, "RememberActivation", own.Handle);
                Check(paste.Target == 0, "Kept-open activation rejects the app's own windows as paste targets");
            }
            keepOpen.IsChecked = false; keepOpen.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Invoke(window, "OnDeactivated", window, EventArgs.Empty);
            Check(!window.KeepOpen && (int)typeof(HistoryWindow).GetField("interactionVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! > interaction, "Disabling keep open restores automatic dismissal");
            keepOpen.IsChecked = true; keepOpen.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.Dismiss();
            Check(!window.KeepOpen && keepOpen.IsChecked == false, "Explicit close resets keep open for the next invocation");
            var rowPin = new ToggleButton { Tag = file.Id, IsChecked = true };
            await (Task)Invoke(window, "PinRow", rowPin)!;
            Check((await store.Get(file.Id)).Pinned && (await store.Query(new(Pinned: true))).Any(row => row.Id == file.Id), "Per-row pin action persists and appears in the pinned filter");
            rowPin.IsChecked = false; await (Task)Invoke(window, "PinRow", rowPin)!;
            Check(!(await store.Get(file.Id)).Pinned && !(await store.Query(new(Pinned: true))).Any(row => row.Id == file.Id), "Per-row unpin action persists and leaves the pinned filter");
            rowPin.Tag = Guid.NewGuid(); rowPin.IsChecked = true;
            using (var failed = new HistoryStore(Path.Combine(directory, "closed.db")))
            {
                failed.Dispose(); typeof(App).GetProperty("History", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, failed);
                await (Task)Invoke(window, "PinRow", rowPin)!;
                Check(rowPin.IsChecked == false && rowPin.IsEnabled, "Failed row pin restores the previous toggle state and enables the control");
                typeof(App).GetProperty("History", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, store);
            }
            window.ShowNotice("");
            await store.Pin(image.Id, true);
            var list = (ListBox)window.FindName("HistoryList");
            var rows = await store.Query(new());
            Field(window, "ready", false); list.ItemsSource = rows; Field(window, "rows", rows); Field(window, "ready", true);
            Select(window, rows.Single(row => row.Id == file.Id));
            CheckRowPinHit(window, file.Id);
            await CheckPreviewPaneSizing(window);
            Field(window, "previewVisible", true);
            ((FrameworkElement)window.FindName("PreviewPanel")).Visibility = Visibility.Visible;
            Invoke(window, "LayoutPreview");
            Select(window, rows.Single(row => row.Id == image.Id)); await Preview(window);
            Check(((Image)window.FindName("ImagePreview")).Source is BitmapSource && ((FrameworkElement)window.FindName("ImagePreviewFrame")).Visibility == Visibility.Visible, "Stored image is shown in the actual preview controls");
            Check(((FrameworkElement)window.FindName("TextToolbar")).Visibility == Visibility.Collapsed, "Image preview leaves space by hiding text editing controls");
            Select(window, rows.Single(row => row.Id == file.Id)); await Preview(window);
            Check(((Image)window.FindName("ImagePreview")).Source is BitmapSource, "Explorer image file is shown in preview mode");
            if (renderDirectory is not null) await RenderExamples(window, image.Id);
            int previewRequests = (int)typeof(HistoryWindow).GetField("selectionVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Invoke(window, "OnPreviewRow", new Button { Tag = text.Id }, new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitFor(() => ((TextBox)window.FindName("Editor")).Text == text.Text);
            Check((int)typeof(HistoryWindow).GetField("selectionVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! == previewRequests + 1, "Switching detail rows starts one preview request instead of duplicate reads");
            Select(window, rows.Single(row => row.Id == image.Id)); var pending = Preview(window);
            Select(window, rows.Single(row => row.Id == text.Id)); var replacement = Preview(window);
            await Task.WhenAll(pending, replacement);
            Check(((Image)window.FindName("ImagePreview")).Source is null && ((TextBox)window.FindName("Editor")).Text == text.Text, "Stale image completion cannot replace a newer text selection");
            Select(window, rows.Single(row => row.Id == image.Id)); pending = Preview(window); window.Dismiss(); await pending;
            Check(((Image)window.FindName("ImagePreview")).Source is null, "Dismissed preview does not retain a late image result");
            await Preview(window);
            Check(((Image)window.FindName("ImagePreview")).Source is BitmapSource, "Image selection reloads after dismissal");
            Select(window, rows.Single(row => row.Id == file.Id)); File.Delete(imagePath); await Preview(window);
            Check(((TextBox)window.FindName("FilePreview")).Visibility == Visibility.Visible && ((TextBox)window.FindName("FilePreview")).Text == imagePath, "Moved image files retain their path preview");
            Field(window, "ready", false); list.SelectedItems.Add(rows.Single(row => row.Id == image.Id)); Field(window, "ready", true);
            await Preview(window);
            Check(!((TextBlock)window.FindName("PreviewPlaceholder")).Text.Contains("Loading"), "Unsupported image multiselection shows an explanation instead of a loading state");
            var chrome = WindowChrome.GetWindowChrome(window);
            Check(chrome.CaptionHeight == 52 && WindowChrome.GetIsHitTestVisibleInChrome((Button)window.FindName("RecordingButton")), "Native title drag region leaves header buttons interactive");
            Check(window.Width == 440 && window.Height == 520 && new WindowLayouts(Path.Combine(directory, "layouts.json")).Get("test", false) == new PopupSize(440, 520), "Window and layout defaults use compact dimensions");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Select(HistoryWindow window, ItemSummary row)
    {
        Field(window, "ready", false); ((ListBox)window.FindName("HistoryList")).SelectedItem = row; Field(window, "ready", true);
    }
    private static void Field(HistoryWindow window, string name, object value) => typeof(HistoryWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static object? Invoke(HistoryWindow window, string name, params object?[] args) => typeof(HistoryWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, args);
    private static Task Preview(HistoryWindow window) => (Task)Invoke(window, "PreviewSelection")!;
    private static async Task CheckPreviewPaneSizing(HistoryWindow window)
    {
        var root = (FrameworkElement)window.Content; window.Content = null;
        var panel = (FrameworkElement)window.FindName("HistoryPanel");
        var preview = (FrameworkElement)window.FindName("PreviewPanel");
        ((FrameworkElement)window.FindName("EmptyState")).Visibility = Visibility.Collapsed;
        using var source = new HwndSource(new HwndSourceParameters("Clipboard fixed-list component") { WindowStyle = unchecked((int)0x80000000), Width = 1000, Height = 700 });
        try
        {
            source.RootVisual = root;
            SizeRoot(root, 440, 520);
            double width = panel.ActualWidth, height = panel.ActualHeight;
            var list = (ListBox)window.FindName("HistoryList");
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            double rowWidth = row.ActualWidth, rowHeight = row.ActualHeight;
            await (Task)Invoke(window, "SetPreview", true)!;
            SizeRoot(root, 840, 520);
            Check(Math.Abs(panel.ActualWidth - width) < 0.1 && Math.Abs(panel.ActualHeight - height) < 0.1 &&
                Math.Abs(row.ActualWidth - rowWidth) < 0.1 && Math.Abs(row.ActualHeight - rowHeight) < 0.1,
                "Opening preview preserves the actual list and item dimensions");
            double previewWidth = preview.ActualWidth, previewHeight = preview.ActualHeight;
            SizeRoot(root, 1000, 700);
            Check(Math.Abs(panel.ActualWidth - width) < 0.1 && Math.Abs(panel.ActualHeight - height) < 0.1 &&
                preview.ActualWidth > previewWidth && preview.ActualHeight > previewHeight,
                "Additional window width and height grow the preview while the list stays fixed");
            SizeRoot(root, 840, 380);
            Check(panel.ActualHeight <= ((FrameworkElement)window.FindName("ContentGrid")).ActualHeight + 0.1,
                "A physically shorter working area cannot make the fixed list overflow its content area");
            SizeRoot(root, 840, 520);
            Check(Math.Abs(panel.ActualHeight - height) < 0.1, "Restoring available height restores the captured list height");
            SizeRoot(root, 440, 520);
            Check(panel.Visibility == Visibility.Collapsed, "Screen-edge single-pane fallback does not compress the list");
            SizeRoot(root, 840, 520);
            Check(panel.Visibility == Visibility.Visible && Math.Abs(panel.ActualWidth - width) < 0.1 && Math.Abs(panel.ActualHeight - height) < 0.1,
                "Returning from compact preview restores the same list dimensions");
            await (Task)Invoke(window, "SetPreview", false)!; SizeRoot(root, 440, 520);
            Check(Math.Abs(panel.ActualWidth - width) < 0.1 && Math.Abs(panel.ActualHeight - height) < 0.1,
                "Closing preview restores the unchanged standalone list");
            window.Width = 600; window.Height = 750;
            SizeRoot(root, 600, 750); width = panel.ActualWidth; height = panel.ActualHeight;
            await (Task)Invoke(window, "SetPreview", true)!;
            var requested = window.SizeForMode(new PopupSize(840, 520));
            SizeRoot(root, (int)Math.Ceiling(requested.Width), (int)Math.Ceiling(requested.Height));
            Check(panel.Visibility == Visibility.Visible && Math.Abs(panel.ActualWidth - width) < 0.1 && preview.ActualWidth >= 240,
                "A custom wider list keeps its width and reserves usable preview space");
            Check(requested.Height >= 750 && Math.Abs(panel.ActualHeight - height) < 0.1,
                "A tall list cannot shrink to a shorter remembered preview height");
            window.Dismiss();
            var reopened = window.SizeForMode(new PopupSize(840, 520));
            Check(reopened == requested, "Reopening with preview active uses the same fixed-list size requirements");
            await (Task)Invoke(window, "SetPreview", false)!; SizeRoot(root, 440, 520);
        }
        finally { source.RootVisual = null; root.Width = root.Height = double.NaN; window.Width = 440; window.Height = 520; window.Content = root; }
    }
    private static void SizeRoot(FrameworkElement root, int width, int height)
    {
        root.Width = width; root.Height = height; root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
    }
    private static void CheckNativeResize()
    {
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(), Device = "" };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromPoint(new NativeMethods.Point(), 2), ref info)) throw new Exception("Test monitor unavailable");
        using var source = new HwndSource(new HwndSourceParameters("Clipboard anchored resize component")
        {
            WindowStyle = unchecked((int)0x80000000), PositionX = info.Work.Left + 12, PositionY = info.Work.Top + 12,
            Width = Math.Min(440, info.Work.Right - info.Work.Left - 24), Height = Math.Min(400, info.Work.Bottom - info.Work.Top - 24)
        });
        var sizes = new List<(int Width, int Height)>();
        source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == 0x0005) sizes.Add(((int)((long)lParam & 0xffff), (int)(((long)lParam >> 16) & 0xffff)));
            return 0;
        });
        if (!NativeMethods.GetWindowRect(source.Handle, out var before)) throw new Exception("Test window unavailable");
        PopupPlacement.Resize(source.Handle, 840, 520);
        if (!NativeMethods.GetWindowRect(source.Handle, out var expanded)) throw new Exception("Expanded window unavailable");
        Check(expanded.Left == before.Left && expanded.Top == before.Top, "Native detail expansion preserves the existing HWND position");
        PopupPlacement.Resize(source.Handle, 440, 520);
        if (!NativeMethods.GetWindowRect(source.Handle, out var closed)) throw new Exception("Closed-detail window unavailable");
        Check(closed.Left == before.Left && closed.Top == before.Top, "Native detail collapse preserves the existing HWND position");
        Check(sizes.Count > 0 && sizes.All(size => size.Width > 1 && size.Height > 1), "Detail resizing never makes an intermediate 1-by-1 layout pass");
    }
    private static void CheckRowPinHit(HistoryWindow window, Guid id)
    {
        var root = (FrameworkElement)window.Content; window.Content = null;
        using var source = new HwndSource(new HwndSourceParameters("Clipboard row-pin component") { WindowStyle = unchecked((int)0x80000000), Width = 440, Height = 520 });
        try
        {
            source.RootVisual = root; root.Measure(new Size(440, 520)); root.Arrange(new Rect(0, 0, 440, 520)); root.UpdateLayout();
            var button = Find<ToggleButton>(root, item => item.Tag is Guid tag && tag == id) ?? throw new Exception("Row pin was not realized");
            var hitTest = typeof(HistoryWindow).GetMethod("ClickedHistoryRow", BindingFlags.Static | BindingFlags.NonPublic)!;
            var child = VisualTreeHelper.GetChild(button, 0);
            Check(hitTest.Invoke(null, [child]) is null, "A realized selected row's pin-button content is excluded from paste hits");
            var row = ((ListBox)window.FindName("HistoryList")).ItemContainerGenerator.ContainerFromItem(((ListBox)window.FindName("HistoryList")).SelectedItem);
            Check(row is ListBoxItem && ReferenceEquals(hitTest.Invoke(null, [row]), row), "Ordinary selected row hits remain eligible for paste");
        }
        finally { source.RootVisual = null; window.Content = root; }
    }
    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        if (root is T item && match(item)) return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), match) is { } found) return found;
        return null;
    }
    private static async Task WaitFor(Func<bool> complete)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!complete()) { if (DateTime.UtcNow > timeout) throw new TimeoutException("Offscreen thumbnail did not complete"); await Task.Delay(10); }
    }
    private static async Task RenderExamples(HistoryWindow window, Guid imageId)
    {
        var root = (FrameworkElement)window.Content; window.Content = null;
        root.SetValue(TextElement.ForegroundProperty, window.Foreground);
        root.SetValue(TextElement.FontFamilyProperty, window.FontFamily);
        ((FrameworkElement)window.FindName("EmptyState")).Visibility = Visibility.Collapsed;
        using var source = new HwndSource(new HwndSourceParameters("Clipboard offscreen render") { WindowStyle = unchecked((int)0x80000000), Width = 440, Height = 520 });
        try
        {
            source.RootVisual = root;
            Render(root, 440, 440, "image-preview.png");
            Field(window, "previewVisible", false); ((FrameworkElement)window.FindName("PreviewPanel")).Visibility = Visibility.Collapsed; Invoke(window, "LayoutPreview");
            Render(root, 440, 520, "compact-list.png");
            await WaitFor(() => Images(root).Any(image => HistoryThumbnail.GetItem(image)?.Id == imageId && image.Source is not null));
            Render(root, 440, 520, "compact-list.png");
            await (Task)Invoke(window, "SetPreview", true)!;
            Render(root, 840, 520, "split-preview.png");
            await WaitFor(() => Images(root).Any(image => HistoryThumbnail.GetItem(image)?.Id == imageId && image.Source is not null));
            Render(root, 840, 520, "split-preview.png");
            Render(root, 1040, 650, "wide-preview.png");
            await (Task)Invoke(window, "SetPreview", false)!;
            Render(root, 340, 380, "small-list.png");
            Field(window, "previewVisible", true); ((FrameworkElement)window.FindName("PreviewPanel")).Visibility = Visibility.Visible; Invoke(window, "LayoutPreview");
        }
        finally { source.RootVisual = null; window.Content = root; }
    }
    private static IEnumerable<Image> Images(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Image image) yield return image;
            foreach (var nested in Images(child)) yield return nested;
        }
    }
    private static void Render(FrameworkElement root, int width, int height, string name)
    {
        SizeRoot(root, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(renderDirectory!, name)); encoder.Save(output);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine($"PASS: {message}"); checks++; }
    private static byte[] CreatePng(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 180; pixels[i + 1] = 110; pixels[i + 2] = 40; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint size, nint output, uint outputSize, out uint returned, nint overlapped);
    private static void CreateJunction(string path, string destination)
    {
        Directory.CreateDirectory(path);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + destination), print = Encoding.Unicode.GetBytes(destination);
        byte[] data = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0); BitConverter.GetBytes((ushort)(data.Length - 8)).CopyTo(data, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(data, 10); BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(data, 12); BitConverter.GetBytes((ushort)print.Length).CopyTo(data, 14);
        substitute.CopyTo(data, 16); print.CopyTo(data, 18 + substitute.Length);
        using var handle = CreateFile(path, 0x40000000, 7, 0, 3, 0x02200000, 0);
        if (!DeviceIoControl(handle, 0x000900A4, data, (uint)data.Length, 0, 0, out _, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}

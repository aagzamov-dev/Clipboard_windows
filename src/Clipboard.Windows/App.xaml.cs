using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Clipboard.Core;
using Clipboard.Windows.Native;
using Forms = System.Windows.Forms;

namespace Clipboard.Windows;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsInstance, quitting, manualPause;
    private DateTimeOffset pauseUntil, lastCleanup, lastUpdate;
    private bool updateBusy, sessionLocked, settingsUsable = true;
    private GlobalShortcut? shortcut;
    private ForegroundTracker? foregroundTracker;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Forms.ContextMenuStrip? trayMenu;
    private Forms.ToolStripMenuItem? pauseItem;
    private HistoryWindow? popup;
    private PopupSize? constrainedPopupSize;
    private SettingsWindow? settingsWindow;
    private readonly DispatcherTimer maintenance = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Channel<ClipboardItem> captures = Channel.CreateBounded<ClipboardItem>(new BoundedChannelOptions(4) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private Task captureWorker = Task.CompletedTask, layoutSave = Task.CompletedTask;
    private readonly WindowLayouts layouts = new(Path.Combine(DataDirectory, "window-layouts.json"));
    internal AppSettings Settings { get; private set; } = new();
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipboard");
    internal SettingsStore Store { get; } = new(Path.Combine(DataDirectory, "settings.json"));
    internal HistoryStore History { get; private set; } = null!;
    internal ClipboardService Clipboard { get; private set; } = null!;
    internal PasteCoordinator Paste { get; private set; } = null!;
    internal bool Recording => Settings.OnboardingComplete && Settings.StartMonitoring && !sessionLocked && !manualPause && pauseUntil <= DateTimeOffset.UtcNow;
    internal string LastStatus { get; private set; } = "";
    internal bool DarkTheme { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\Clipboard.Windows", out ownsInstance);
        if (!ownsInstance)
        {
            MessageBox.Show("Clipboard is already running. Use its tray icon or configured shortcut.", "Clipboard"); Shutdown(); return;
        }
        string? warning = null;
        try { Settings = Store.Load(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { settingsUsable = false; warning = $"Settings could not be loaded. Defaults are used for this session. The file is preserved until you explicitly save.\n\n{error.Message}"; }
        try
        {
            History = await Task.Run(() => new HistoryStore(Path.Combine(DataDirectory, "history.db")));
            lastCleanup = Settings.LastCleanup ?? default;
            Clipboard = new ClipboardService(() => Settings,
                item => { if (!captures.Writer.TryWrite(item)) Report("Capture queue is full. This copy was skipped."); }, Report);
            Paste = new PasteCoordinator(Clipboard);
            foregroundTracker = new ForegroundTracker();
            captureWorker = ProcessCaptures();
            ApplyTheme();
            popup = new HistoryWindow(); MainWindow = popup;
            nint handle = new WindowInteropHelper(popup).EnsureHandle();
            shortcut = new GlobalShortcut(handle, () => { if (settingsWindow?.IsActive != true) TogglePopup(); });
            using (var icon = GetResourceStream(new Uri("pack://application:,,,/Clipboard;component/Assets/Clipboard.ico")).Stream) trayIcon = new System.Drawing.Icon(icon);
            BuildTray();
            try { shortcut.Apply(Settings.Shortcut, () => { }); }
            catch (Win32Exception error) { warning = string.Join("\n\n", warning, error.Message); }
            await ConfigureCapture();
            maintenance.Tick += async (_, _) => await Guard(Maintain); maintenance.Start();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            if (warning is not null) { ShowSettings(); MessageBox.Show(warning, "Clipboard settings", MessageBoxButton.OK, MessageBoxImage.Warning); }
            else if (!e.Args.Contains("--background")) TogglePopup();
            await Maintain();
        }
        catch (Exception error)
        {
            MessageBox.Show($"Clipboard could not start. Existing history and settings were preserved.\n\n{error.Message}", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void BuildTray()
    {
        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Open Clipboard", null, (_, _) => Dispatcher.Invoke(OpenFromTray));
        pauseItem = (Forms.ToolStripMenuItem)trayMenu.Items.Add("Pause recording", null, (_, _) => Dispatcher.BeginInvoke(() => _ = Guard(ToggleRecording)));
        var durations = new Forms.ToolStripMenuItem("Pause for");
        foreach (int minutes in new[] { 5, 15, 60 })
            durations.DropDownItems.Add($"{minutes} minutes", null, (_, _) => Dispatcher.BeginInvoke(() => _ = Guard(() => PauseFor(minutes))));
        trayMenu.Items.Add(durations);
        trayMenu.Items.Add("Settings…", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        trayMenu.Items.Add("Quit Clipboard", null, (_, _) => Dispatcher.BeginInvoke(() => _ = Guard(RequestQuit)));
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "Clipboard", ContextMenuStrip = trayMenu, Visible = true };
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(OpenFromTray); };
        UpdateRecordingStatus();
    }

    private async Task ProcessCaptures()
    {
        await foreach (var item in captures.Reader.ReadAllAsync())
        {
            try
            {
                if (await History.Capture(item)) await (await Dispatcher.InvokeAsync(async () => { if (!quitting && popup is not null) await popup.Captured(); }));
            }
            catch (Exception error) { Report($"History could not save this item: {error.Message}"); }
        }
    }

    internal void TogglePopup() => TogglePopupCore(false);
    internal void RememberPasteDestination(nint previous)
    {
        Paste?.Remember(ForegroundTracker.IsDestination(previous) ? previous : 0);
    }
    private void OpenFromTray() => TogglePopupCore(true);
    private void TogglePopupCore(bool trayInvocation)
    {
        if (popup is null || quitting) return;
        if (popup.IsVisible) { Paste.Cancel(); popup.Dismiss(); return; }
        // Keep target ownership separate from placement: the popup always opens at the mouse.
        Paste.Remember(foregroundTracker?.Destination(trayInvocation) ?? NativeMethods.GetForegroundWindow());
        try
        {
            var size = layouts.Get(PopupPlacement.MonitorKey(), popup.PreviewVisible);
            PositionPopup(size.Width, size.Height);
            popup.Show(); popup.Activate(); popup.AnimateOpen(); _ = Guard(popup.Open);
        }
        catch (Exception error) { Report(error.Message); }
    }

    internal PopupSize? ResizePopup()
    {
        if (popup is null) return null;
        var size = layouts.Get(PopupPlacement.MonitorKey(new WindowInteropHelper(popup).Handle), popup.PreviewVisible);
        return PositionPopup(size.Width, size.Height, preservePosition: true);
    }

    private PopupSize? PositionPopup(double width, double height, bool preservePosition = false)
    {
        if (popup is null) return null;
        var requested = popup.SizeForMode(new PopupSize(width, height));
        width = requested.Width; height = requested.Height;
        double minWidth = 340, minHeight = popup.PreviewVisible ? 440 : 380;
        width = Math.Max(width, minWidth); height = Math.Max(height, minHeight);
        PopupSize? placed = null;
        popup.MinWidth = popup.MinHeight = 0;
        try
        {
            nint handle = new WindowInteropHelper(popup).Handle;
            placed = preservePosition ? PopupPlacement.Resize(handle, width, height) : PopupPlacement.Position(handle, width, height);
            constrainedPopupSize = Math.Abs(placed.Width - width) > 1 || Math.Abs(placed.Height - height) > 1 ? placed : null;
        }
        finally
        {
            popup.MinWidth = Math.Min(minWidth, placed?.Width ?? popup.ActualWidth);
            popup.MinHeight = Math.Min(minHeight, placed?.Height ?? popup.ActualHeight);
        }
        return placed;
    }

    internal void RememberPopupSize()
    {
        if (popup is null || popup.ActualWidth <= 1 || popup.ActualHeight <= 1) return;
        string key = PopupPlacement.MonitorKey(new WindowInteropHelper(popup).Handle);
        var size = new PopupSize(popup.ActualWidth, popup.ActualHeight);
        Task nextSave = Guard(() => layouts.Remember(key, popup.PreviewVisible, size, constrainedPopupSize));
        // A skipped constrained-size save must not hide an earlier write from shutdown.
        if (!nextSave.IsCompleted) layoutSave = nextSave;
    }

    internal void ShowSettings() => ShowSettings(false);
    internal void ShowSettings(bool history)
    {
        Paste?.Cancel(); popup?.Dismiss();
        if (settingsWindow is null) { settingsWindow = new SettingsWindow(this); settingsWindow.Closed += (_, _) => settingsWindow = null; }
        settingsWindow.Show(); settingsWindow.Activate();
        if (history) settingsWindow.ShowHistory();
    }

    internal async Task SaveSettings(AppSettings next)
    {
        next.Validate(); if (shortcut is null) throw new InvalidOperationException("Shortcut service is unavailable.");
        bool loginChanged = next.StartAtLogin != Settings.StartAtLogin;
        if (loginChanged) StartupRegistration.Set(next.StartAtLogin);
        try { shortcut.Apply(next.Shortcut, () => Store.Save(next)); }
        catch { if (loginChanged) StartupRegistration.Set(Settings.StartAtLogin); throw; }
        Settings = next; settingsUsable = true;
        ApplyTheme(); await ConfigureCapture(); popup?.UpdateStatus(); lastCleanup = default; await Maintain();
    }

    internal async Task ToggleRecording()
    {
        if (!settingsUsable) { ShowSettings(); Report("Review and explicitly save your settings before starting recording."); return; }
        if (!Settings.OnboardingComplete)
        {
            Store.Save(Settings with { OnboardingComplete = true }); Settings = Settings with { OnboardingComplete = true };
        }
        else if (!Settings.StartMonitoring) { await SaveSettings(Settings with { StartMonitoring = true }); }
        else manualPause = Recording;
        pauseUntil = default; await ConfigureCapture();
    }

    private async Task PauseFor(int minutes)
    {
        if (!Settings.OnboardingComplete) { TogglePopup(); return; }
        manualPause = false; pauseUntil = DateTimeOffset.UtcNow.AddMinutes(minutes); await ConfigureCapture();
    }

    private async Task ConfigureCapture()
    {
        await Clipboard.Configure(Settings.OnboardingComplete && Settings.StartMonitoring && !sessionLocked, manualPause, pauseUntil);
        await History.ResetDeduplication(); UpdateRecordingStatus();
    }

    private void UpdateRecordingStatus()
    {
        if (tray is not null) tray.Text = Recording ? "Clipboard — recording locally" : "Clipboard — paused";
        if (pauseItem is not null) pauseItem.Text = Recording ? "Pause recording" : "Resume recording";
        popup?.UpdateStatus();
    }

    private async Task Maintain()
    {
        if (quitting) return;
        if (pauseUntil != default && pauseUntil <= DateTimeOffset.UtcNow) { pauseUntil = default; await ConfigureCapture(); }
        if (Settings.OnboardingComplete && Settings.CheckForUpdates && UpdateService.Configured && DateTimeOffset.UtcNow - lastUpdate > TimeSpan.FromDays(1))
        { lastUpdate = DateTimeOffset.UtcNow; _ = CheckUpdates(false); }
        if (settingsUsable && DateTimeOffset.UtcNow - lastCleanup >= TimeSpan.FromDays(Settings.WeeklyCleanup ? 7 : 1))
        {
            lastCleanup = DateTimeOffset.UtcNow;
            try
            {
                await History.Cleanup(Settings, popup?.DraftIds);
                var next = Settings with { LastCleanup = lastCleanup }; Store.Save(next); Settings = next;
            }
            catch (Exception error)
            {
                lastCleanup = DateTimeOffset.UtcNow.AddDays(-(Settings.WeeklyCleanup ? 7 : 1)).AddMinutes(5);
                Report($"History cleanup: {error.Message}");
            }
        }
    }

    internal void Report(string message)
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Report(message)); return; }
        LastStatus = message; popup?.ShowNotice(message);
        if (popup?.IsVisible != true && !quitting && !string.IsNullOrEmpty(message) && !message.StartsWith("Ctrl+V sent", StringComparison.Ordinal))
            tray?.ShowBalloonTip(3500, "Clipboard", message, Forms.ToolTipIcon.Info);
    }

    internal async Task RequestQuit()
    {
        if (settingsWindow?.IsBusy == true) { Report("Wait for the current backup or settings operation before quitting."); return; }
        if (quitting || popup is not null && !await popup.ResolveDrafts()) return;
        quitting = true; maintenance.Stop(); Paste?.Cancel(); Clipboard?.Dispose(); captures.Writer.TryComplete();
        await captureWorker; await layoutSave; Shutdown();
    }

    internal async Task Guard(Func<Task> work)
    {
        try { await work(); } catch (Exception error) { Report(error.Message); }
    }

    internal Task<bool> ResolveDrafts(bool all) => popup?.ResolveClear(all) ?? Task.FromResult(true);
    internal void HistoryCleared() => popup?.DiscardDrafts();
    internal IReadOnlyCollection<Guid> ProtectedDraftIds => popup?.DraftIds ?? [];

    internal async Task<string> CheckUpdates(bool interactive)
    {
        if (updateBusy) return "An update check is already in progress.";
        updateBusy = true;
        try
        {
            var result = await UpdateService.Check();
            if (result.Release is not null)
            {
                if (interactive && MessageBox.Show(result.Message, "Clipboard update", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.Release.AbsoluteUri) { UseShellExecute = true });
                else if (!interactive) Report("A Clipboard update is available. Open Settings → About to review it.");
            }
            return result.Message;
        }
        catch (Exception error) { return $"Update check failed: {error.Message}"; }
        finally { updateBusy = false; }
    }

    private void OnSystemPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(ApplyTheme);
    private async void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
        { await (await Dispatcher.InvokeAsync(() => Guard(async () => { sessionLocked = true; popup?.Dismiss(); Paste.Cancel(); await Clipboard.Configure(false); UpdateRecordingStatus(); }))); }
        else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) await (await Dispatcher.InvokeAsync(() => Guard(async () => { sessionLocked = false; await ConfigureCapture(); })));
    }

    internal void ApplyTheme()
    {
        bool dark = Settings.Appearance == "Dark";
        if (Settings.Appearance == "System")
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        DarkTheme = dark;
        void ColorResource(string name, string darkColor, string lightColor, Brush highContrast)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : lightColor));
            brush.Freeze(); Resources[name] = SystemParameters.HighContrast ? highContrast : brush;
        }
        ColorResource("Surface", "#202024", "#F3F3F6", SystemColors.WindowBrush);
        ColorResource("Ink", "#F5F5F7", "#1B1B20", SystemColors.WindowTextBrush);
        ColorResource("SecondaryInk", "#B8B8C3", "#595963", SystemColors.WindowTextBrush);
        ColorResource("Line", "#38FFFFFF", "#22000000", SystemColors.ActiveBorderBrush);
        ColorResource("InputSurface", "#343439", "#FCFCFE", SystemColors.WindowBrush);
        ColorResource("CardSurface", "#70343439", "#B8FFFFFF", SystemColors.WindowBrush);
        ColorResource("HoverSurface", "#48484F", "#E9E9F1", SystemColors.ControlBrush);
        ColorResource("SelectionSurface", "#384959", "#DCECFB", SystemColors.HighlightBrush);
        ColorResource("SelectedInk", "#F5F5F7", "#1B1B20", SystemColors.HighlightTextBrush);
        ColorResource("SelectedTabInk", "#80CAFF", "#0067C0", SystemColors.HighlightTextBrush);
        ColorResource("Accent", "#80CAFF", "#0067C0", SystemColors.HighlightBrush);
        ColorResource("AccentInk", "#092338", "#FFFFFF", SystemColors.HighlightTextBrush);
        ColorResource("DropSurface", "#2D2D33", "#FAFAFD", SystemColors.WindowBrush);
        ColorResource("ScrollThumb", "#85858F", "#85858F", SystemColors.WindowTextBrush);
        ColorResource("AcrylicTint", "#20202024", "#40F8F8FC", SystemColors.WindowBrush);
        Resources["WindowTint"] = Resources["Surface"];
        Resources[SystemColors.WindowBrushKey] = Resources["Surface"];
        Resources[SystemColors.WindowTextBrushKey] = Resources["Ink"];
        Resources[SystemColors.ControlTextBrushKey] = Resources["Ink"];
        Resources[SystemColors.ControlBrushKey] = Resources["Surface"];
        foreach (Window window in Windows) WindowBackdrop.Apply(window);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        quitting = true; maintenance.Stop();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        shortcut?.Dispose(); foregroundTracker?.Dispose(); Clipboard?.Dispose(); tray?.Dispose(); trayMenu?.Dispose(); trayIcon?.Dispose(); History?.Dispose();
        if (ownsInstance) instance?.ReleaseMutex(); instance?.Dispose(); base.OnExit(e);
    }
}

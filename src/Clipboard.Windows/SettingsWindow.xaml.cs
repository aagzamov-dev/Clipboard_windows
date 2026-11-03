using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clipboard.Core;
using Microsoft.Win32;

namespace Clipboard.Windows;

public partial class SettingsWindow : Window
{
    private readonly App app;
    private Shortcut draftShortcut;
    internal bool IsBusy { get; private set; }

    internal SettingsWindow(App app)
    {
        InitializeComponent(); this.app = app; draftShortcut = app.Settings.Shortcut; Populate(app.Settings);
        VersionLabel.Text = $"Clipboard {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} · Windows 11";
        UpdateStatus.Text = UpdateService.Configured ? "Updates use the configured Windows release repository." : "A Windows release repository has not been configured for this build.";
    }

    private void Populate(AppSettings value)
    {
        draftShortcut = value.Shortcut; ShortcutInput.Text = draftShortcut.ToString();
        Animation.IsChecked = value.OpenAnimation; LaunchAtLogin.IsChecked = value.StartAtLogin;
        Appearance.SelectedIndex = value.Appearance == "Dark" ? 2 : value.Appearance == "Light" ? 1 : 0;
        StartMonitoring.IsChecked = value.StartMonitoring; Retention.Text = value.RetentionDays?.ToString(CultureInfo.InvariantCulture) ?? "";
        MaximumCount.Text = value.MaximumCount?.ToString(CultureInfo.InvariantCulture) ?? "";
        WeeklyCleanup.IsChecked = value.WeeklyCleanup; PinnedFirst.IsChecked = value.PinnedFirst;
        Separator.Text = value.MergeSeparator; ExcludedApps.Text = string.Join(Environment.NewLine, value.ExcludedApps);
        UpdateChecks.IsChecked = value.CheckForUpdates;
    }

    private void OnShortcutKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Tab or Key.Escape) return;
        e.Handled = true; var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
        var mods = Keyboard.Modifiers;
        var candidate = new Shortcut((mods.HasFlag(ModifierKeys.Control) ? ShortcutModifiers.Control : 0) |
            (mods.HasFlag(ModifierKeys.Alt) ? ShortcutModifiers.Alt : 0) | (mods.HasFlag(ModifierKeys.Shift) ? ShortcutModifiers.Shift : 0) |
            (mods.HasFlag(ModifierKeys.Windows) ? ShortcutModifiers.Windows : 0), (uint)KeyInterop.VirtualKeyFromKey(key));
        try { candidate.Validate(); draftShortcut = candidate; ShortcutInput.Text = candidate.ToString(); Status.Text = "Save to apply. A conflict preserves the current shortcut."; }
        catch (InvalidDataException error) { Status.Text = error.Message; }
    }

    private void OnReset(object sender, RoutedEventArgs e) { Populate(new()); Status.Text = "Save to apply defaults. Your clipboard history is kept."; }
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        IsBusy = true; SaveButton.IsEnabled = false;
        try
        {
            var next = app.Settings with
            {
                Shortcut = draftShortcut,
                ActiveDisplay = false,
                OpenAnimation = Animation.IsChecked == true,
                StartAtLogin = LaunchAtLogin.IsChecked == true,
                Appearance = ((ComboBoxItem)Appearance.SelectedItem).Content.ToString()!,
                StartMonitoring = StartMonitoring.IsChecked == true,
                RetentionDays = OptionalNumber(Retention.Text),
                MaximumCount = OptionalNumber(MaximumCount.Text),
                WeeklyCleanup = WeeklyCleanup.IsChecked == true,
                PinnedFirst = PinnedFirst.IsChecked == true,
                MergeSeparator = Separator.Text,
                ExcludedApps = ExcludedApps.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                CheckForUpdates = UpdateChecks.IsChecked == true
            };
            await app.SaveSettings(next); IsBusy = false; Close();
        }
        catch (Exception error) { Status.Text = error.Message; }
        finally { IsBusy = false; SaveButton.IsEnabled = true; }
    }
    private static int? OptionalNumber(string text) => string.IsNullOrWhiteSpace(text) ? null :
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : throw new InvalidDataException("Enter a positive whole number or leave the field blank.");

    private async Task Work(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; ExportButton.IsEnabled = ImportButton.IsEnabled = SaveButton.IsEnabled = false; Status.Text = "Working…";
        try { await action(); }
        catch (System.Security.Cryptography.CryptographicException) { Status.Text = "The backup password is wrong or the encrypted backup is damaged. Nothing was imported."; }
        catch (Exception error) { Status.Text = error.Message; }
        finally { IsBusy = false; ExportButton.IsEnabled = ImportButton.IsEnabled = SaveButton.IsEnabled = true; }
    }
    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        if (BackupPassword.Password.Length == 0 && MessageBox.Show(this, "Export an unencrypted backup? Anyone with the file can read your history.", "Backup", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog { Filter = "Clipboard backup|*.clipboard", FileName = $"Clipboard-{DateTime.Now:yyyy-MM-dd}.clipboard" };
        if (dialog.ShowDialog(this) != true) return;
        string password = BackupPassword.Password;
        await Work(async () => { await BackupService.Export(app.History, dialog.FileName, password); BackupPassword.Clear(); Status.Text = "History backup exported."; });
    }
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var dialog = new OpenFileDialog { Filter = "Clipboard backup|*.clipboard" }; if (dialog.ShowDialog(this) != true) return;
        string password = BackupPassword.Password;
        await Work(async () => { int count = await BackupService.Import(app.History, dialog.FileName, password); BackupPassword.Clear(); Status.Text = $"Imported {count:N0} entries. Existing entries and drafts were kept."; });
    }
    private void OnExportSettings(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var dialog = new SaveFileDialog { Filter = "Settings JSON|*.json", FileName = "Clipboard-settings.json" }; if (dialog.ShowDialog(this) != true) return;
        try { new SettingsStore(dialog.FileName).Save(app.Settings); Status.Text = "Settings exported."; } catch (Exception error) { Status.Text = error.Message; }
    }
    private void OnImportSettings(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var dialog = new OpenFileDialog { Filter = "Settings JSON|*.json" }; if (dialog.ShowDialog(this) != true) return;
        try { Populate(new SettingsStore(dialog.FileName).Load() with { StartAtLogin = app.Settings.StartAtLogin }); Status.Text = "Review imported settings, then Save to apply. Login and history consent are unchanged."; }
        catch (Exception error) { Status.Text = error.Message; }
    }
    private async void OnClearUnpinned(object sender, RoutedEventArgs e) => await Clear(false);
    private async void OnClearAll(object sender, RoutedEventArgs e) => await Clear(true);
    private async Task Clear(bool all)
    {
        if (IsBusy || MessageBox.Show(this, all ? "Permanently delete all history, including pinned entries?" : "Permanently delete all unpinned history?", "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Work(async () => { if (!await app.ResolveDrafts(all)) { Status.Text = "Clear cancelled. Resolve drafts first."; return; } await app.History.Clear(all); app.HistoryCleared(); Status.Text = "History cleared. Original files were untouched."; });
    }
    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(App.DataDirectory) { UseShellExecute = true }); } catch (Exception error) { Status.Text = error.Message; }
    }
    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await Work(async () => { UpdateStatus.Text = await app.CheckUpdates(true); Status.Text = "Update check finished."; });
    private void OnPasteStatus(object sender, RoutedEventArgs e) => PasteStatus.Text = app.Paste.LastStatus;
    private void OnClosing(object? sender, CancelEventArgs e) { if (IsBusy) { e.Cancel = true; Status.Text = "Wait for the current operation to finish."; } }
    private void OnCancel(object sender, RoutedEventArgs e) { if (!IsBusy) Close(); }
}

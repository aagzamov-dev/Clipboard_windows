using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clipboard.Core;

namespace Clipboard.Windows;

public partial class HistoryWindow : Window
{
    private App App => (App)Application.Current;
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DraftStore draftStore = new();
    private Dictionary<Guid, string> drafts => draftStore.Individual;
    private ClipboardItem? detail;
    private string? combinedOriginal;
    private string combinedDraft = "";
    private string? combinedKey;
    private bool settingEditor, ready, previewVisible;
    private int loadVersion, selectionVersion, interactionVersion, offset, dayOffset;
    private bool deliberatePasteDismissal, restoringSelection;
    private long cutoff = long.MaxValue;
    private Guid? undoToken;
    private IReadOnlyCollection<Guid> pendingClearDraftIds = [];
    private bool navigatingDay;
    private CancellationTokenSource? previewWork;
    private PopupSize? listPaneSize;
    private PopupSize? listWindowSize;
    private IReadOnlyList<ItemSummary> rows = [];
    internal IReadOnlyCollection<Guid> DraftIds => draftStore.ProtectedIds;
    internal bool HasDrafts => draftStore.HasChanges;
    internal bool IsPasting { get; private set; }
    internal bool PreviewVisible => previewVisible;
    internal bool KeepOpen => KeepOpenButton.IsChecked == true;
    internal double MinimumPreviewWidth => (listWindowSize?.Width ?? Width) + 240 + 8;
    internal PopupSize SizeForMode(PopupSize preferred) => previewVisible
        ? new(Math.Max(preferred.Width, MinimumPreviewWidth), Math.Max(preferred.Height, listWindowSize?.Height ?? Height))
        : preferred;

    public HistoryWindow()
    {
        InitializeComponent();
        Native.WindowBackdrop.Attach(this);
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
        searchTimer.Tick += async (_, _) => { searchTimer.Stop(); offset = 0; await Load(); };
        ready = true; UpdateStatus();
    }

    internal void UpdateStatus()
    {
        if (!ready) return;
        bool consent = App.Settings.OnboardingComplete;
        RecordingStatus.Text = !consent ? "Local history is off" : App.Recording ? "● Recording locally" : "Ⅱ Recording paused";
        RecordingButton.Content = !consent ? "Start" : App.Recording ? "Pause" : "Resume";
        StartButton.Visibility = consent ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = consent ? "Nothing here yet" : "Your clipboard, kept on your PC";
        EmptyDescription.Text = consent ? "Copy something or change the filters." : "Start local history to save text, images, and file references. No accounts or cloud sync.";
        ShortcutLabel.Text = $"{App.Settings.Shortcut}  ·  {rows.Count} items  ·  Saved until cleared";
    }

    internal async Task Open()
    {
        if (!previewVisible && !HasDrafts)
        {
            dayOffset = 0; Dates.SelectedIndex = 3; offset = 0;
        }
        int version = interactionVersion;
        long nextCutoff = await App.History.Cutoff(); if (!IsVisible || version != interactionVersion) return;
        cutoff = nextCutoff; RefreshButton.Visibility = Visibility.Collapsed;
        await Load();
    }

    internal void AnimateOpen()
    {
        Opacity = 1;
        if (App.Settings.OpenAnimation && SystemParameters.ClientAreaAnimation)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(65)) { FillBehavior = FillBehavior.Stop });
        Search.Focus();
    }

    internal void ShowNotice(string text) => Notice.Text = text;
    internal async Task Captured()
    {
        if (!IsVisible) return;
        if (HasDrafts || previewVisible || offset > 0 || HistoryList.SelectedItems.Count > 0 || Search.Text.Length > 0 || dayOffset != 0 || Dates.SelectedIndex != 3)
            RefreshButton.Visibility = Visibility.Visible;
        else { cutoff = await App.History.Cutoff(); await Load(); }
    }

    private HistoryQuery Query()
    {
        DateTime today = DateTime.Today;
        DateTimeOffset? from = null, to = null;
        int date = Dates.SelectedIndex;
        if (date != 3)
        {
            DateTime start = date == 2 ? today.AddDays(-6) : today.AddDays(date == 1 ? -1 : date == 4 ? dayOffset : 0);
            from = new DateTimeOffset(start); to = new DateTimeOffset(date == 2 ? today.AddDays(1) : start.AddDays(1));
        }
        NextDay.IsEnabled = date is 1 or 4 && dayOffset < 0;
        return new(Search.Text, Types.SelectedIndex > 0 ? (ItemKind)(Types.SelectedIndex - 1) : null,
            PinnedFilter.IsChecked == true, from, to, App.Settings.PinnedFirst, cutoff);
    }

    private async Task Load()
    {
        if (!ready || App.History is null || !IsVisible) return;
        int version = ++loadVersion;
        try
        {
            var results = await App.History.Query(Query(), offset);
            if (version != loadVersion) return;
            var selected = HistoryList.SelectedItems.Cast<ItemSummary>().Select(s => s.Id).ToHashSet();
            restoringSelection = true;
            try
            {
                rows = results; HistoryList.ItemsSource = rows;
                foreach (var row in rows.Where(r => selected.Contains(r.Id))) HistoryList.SelectedItems.Add(row);
            }
            finally { restoringSelection = false; }
            if (previewVisible) await PreviewSelection();
            EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PreviousPage.IsEnabled = offset > 0; NextPage.IsEnabled = rows.Count == Limits.PageSize;
            UpdateStatus();
        }
        catch (Exception error) { ShowNotice(error.Message); }
    }

    private void OnSearch(object sender, TextChangedEventArgs e) { if (!ready) return; ++interactionVersion; searchTimer.Stop(); searchTimer.Start(); }
    private async void OnFilter(object sender, RoutedEventArgs e) { if (!ready) return; ++interactionVersion; offset = 0; await Load(); }
    private async void OnDateFilter(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || navigatingDay) return;
        ++interactionVersion;
        dayOffset = Dates.SelectedIndex == 1 ? -1 : Dates.SelectedIndex == 0 ? 0 : dayOffset; offset = 0; await Load();
    }
    private async void OnPreviousDay(object sender, RoutedEventArgs e) { dayOffset--; await MoveDay(); }
    private async void OnNextDay(object sender, RoutedEventArgs e) { dayOffset = Math.Min(0, dayOffset + 1); await MoveDay(); }
    private async Task MoveDay()
    {
        ++interactionVersion; navigatingDay = true; SelectedDay.Content = DateTime.Today.AddDays(dayOffset).ToString("MMM d");
        Dates.SelectedIndex = dayOffset == 0 ? 0 : dayOffset == -1 ? 1 : 4; navigatingDay = false; offset = 0; await Load();
    }
    private async void OnRefresh(object sender, RoutedEventArgs e) => await App.Guard(async () => { offset = 0; cutoff = await App.History.Cutoff(); RefreshButton.Visibility = Visibility.Collapsed; await Load(); });
    private async void OnPreviousPage(object sender, RoutedEventArgs e) { offset = Math.Max(0, offset - Limits.PageSize); await Load(); }
    private async void OnNextPage(object sender, RoutedEventArgs e) { offset += Limits.PageSize; await Load(); }

    private async void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        ++interactionVersion;
        if (!ready || !previewVisible || restoringSelection) return;
        await PreviewSelection();
    }

    private async Task PreviewSelection()
    {
        previewWork?.Cancel(); previewWork?.Dispose(); previewWork = new CancellationTokenSource();
        var cancellation = previewWork.Token;
        int version = ++selectionVersion;
        detail = null; combinedOriginal = null; combinedKey = null; combinedDraft = "";
        Editor.IsEnabled = false; SaveButton.IsEnabled = false; TextActions.IsEnabled = false; ImagePreview.Source = null;
        Editor.Visibility = FilePreview.Visibility = ImagePreviewFrame.Visibility = TextToolbar.Visibility = PlainPasteButton.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Text = "Select an entry to preview"; PreviewPlaceholder.Visibility = Visibility.Visible;
        SetEditor(""); FilePreview.Clear(); PreviewTitle.Text = "Preview"; PreviewMetadata.Text = "";
        var selected = SelectedRows();
        if (selected.Length == 0) { detail = null; return; }
        PreviewPlaceholder.Text = "Loading preview…";
        try
        {
            if (selected.Length > 1)
            {
                if (selected.Any(s => s.Kind is ItemKind.Image or ItemKind.Files)) { PreviewPlaceholder.Text = "Select one image or file entry to preview, or select text entries to combine."; return; }
                if (selected.Sum(s => s.ByteCount) > Limits.DraftBytes) throw new InvalidOperationException("Combined selection exceeds 8 MB.");
                var texts = new List<string>();
                foreach (var summary in selected)
                {
                    var item = await App.History.Get(summary.Id);
                    if (version != selectionVersion) return;
                    texts.Add(drafts.GetValueOrDefault(item.Id, item.Text!));
                }
                if (version != selectionVersion) return;
                string key = string.Join(',', selected.Select(s => s.Id));
                combinedOriginal = string.Join(App.Settings.MergeSeparator, texts); combinedKey = key;
                combinedDraft = draftStore.Combined.TryGetValue(key, out var saved) ? saved.Text : combinedOriginal;
                if (Encoding.UTF8.GetByteCount(combinedDraft) > Limits.DraftBytes) throw new InvalidOperationException("Combined draft exceeds 8 MB.");
                detail = null; SetEditor(combinedDraft); ShowText(); PreviewTitle.Text = $"{selected.Length} selected · combined draft";
                PreviewMetadata.Text = "Copy or paste the combined draft. Original entries stay unchanged."; SaveButton.IsEnabled = false;
                return;
            }
            var value = await App.History.Get(selected[0].Id);
            if (version != selectionVersion) return;
            combinedKey = null; combinedOriginal = null; combinedDraft = ""; detail = value;
            PreviewTitle.Text = value.Pinned ? $"Pinned {value.Kind}" : value.Kind.ToString(); PreviewMetadata.Text = selected[0].Metadata;
            if (value.IsText)
            {
                SetEditor(drafts.GetValueOrDefault(value.Id, value.Text!)); ShowText(); SaveButton.IsEnabled = drafts.ContainsKey(value.Id);
            }
            else
            {
                BitmapSource? image = null;
                try { image = await ImageContent.Load(value, 1024, cancellation); }
                catch (OperationCanceledException) { return; }
                catch (Exception error)
                {
                    if (version != selectionVersion) return;
                    ShowNotice($"Image preview unavailable: {error.Message}");
                }
                if (version != selectionVersion || cancellation.IsCancellationRequested) return;
                if (image is not null)
                {
                    ImagePreview.Source = image; ImagePreviewFrame.Visibility = Visibility.Visible;
                    ImagePreview.ToolTip = value.Kind == ItemKind.Files ? string.Join(Environment.NewLine, value.Files!) : null;
                    if (value.Kind == ItemKind.Files)
                    {
                        PreviewTitle.Text = System.IO.Path.GetFileName(ImageContent.FilePath(value)) ?? "Image file";
                        PreviewMetadata.Text = $"{value.Created.ToLocalTime():g} · File reference";
                    }
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                }
                else if (value.Kind == ItemKind.Files)
                {
                    FilePreview.Visibility = Visibility.Visible; FilePreview.Text = string.Join(Environment.NewLine, value.Files!);
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                }
                else PreviewPlaceholder.Text = "This image could not be previewed.";
            }
        }
        catch (Exception error)
        {
            if (version != selectionVersion) return;
            PreviewPlaceholder.Text = "This entry could not be previewed.";
            ShowNotice(error.Message);
        }
    }

    private void SetEditor(string value)
    {
        settingEditor = true; Editor.IsUndoEnabled = false; Editor.Text = value; Editor.IsUndoEnabled = true; settingEditor = false;
    }
    private void ShowText()
    {
        ImagePreview.Source = null; Editor.IsEnabled = true; Editor.Visibility = Visibility.Visible;
        ImagePreviewFrame.Visibility = FilePreview.Visibility = PreviewPlaceholder.Visibility = Visibility.Collapsed;
        TextToolbar.Visibility = PlainPasteButton.Visibility = Visibility.Visible; TextActions.IsEnabled = true;
    }
    private void OnEditorChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || settingEditor) return;
        ++interactionVersion;
        string value = Editor.Text;
        try
        {
            if (detail?.IsText == true)
            {
                draftStore.SetIndividual(detail.Id, detail.Text!, value); SaveButton.IsEnabled = drafts.ContainsKey(detail.Id);
            }
            else if (combinedOriginal is not null && combinedKey is not null)
            {
                draftStore.SetCombined(combinedKey, combinedOriginal, value, HistoryList.SelectedItems.Cast<ItemSummary>().Select(row => row.Id).ToArray());
                combinedDraft = value;
            }
        }
        catch (Exception error)
        {
            SetEditor(detail is not null ? drafts.GetValueOrDefault(detail.Id, detail.Text ?? "") : combinedDraft); ShowNotice(error.Message);
        }
    }
    private async void OnSaveDraft(object sender, RoutedEventArgs e) => await SaveDraft();
    private async Task SaveDraft()
    {
        if (detail?.IsText != true || !drafts.TryGetValue(detail.Id, out var text)) return;
        Guid id = detail.Id;
        try
        {
            await App.History.SaveText(id, text);
            var saved = await App.History.Get(id);
            if (drafts.TryGetValue(id, out var latest) && latest == text) drafts.Remove(id);
            if (detail?.Id == id)
            {
                detail = saved;
                draftStore.SetIndividual(id, saved.Text!, Editor.Text);
                SaveButton.IsEnabled = drafts.ContainsKey(id);
            }
            ShowNotice("Saved to history."); RefreshButton.Visibility = Visibility.Visible;
        }
        catch (Exception error) { ShowNotice(error.Message); }
    }
    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (detail?.IsText == true) { drafts.Remove(detail.Id); SetEditor(detail.Text!); SaveButton.IsEnabled = false; }
        else if (combinedOriginal is not null) { combinedDraft = combinedOriginal; SetEditor(combinedDraft); if (combinedKey is not null) draftStore.Combined.Remove(combinedKey); }
    }
    private void OnTextAction(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || TextActions.SelectedIndex <= 0) return;
        Editor.Text = TextTools.Transform(Editor.Text, ((ComboBoxItem)TextActions.SelectedItem).Content.ToString()!); TextActions.SelectedIndex = 0;
    }

    private async Task<ClipboardItem?> CurrentItem(bool usePreview = true)
    {
        var actionDrafts = new Dictionary<Guid, string>(drafts);
        var selected = SelectedRows();
        if (selected.Length > 1)
        {
            if (selected.Any(row => row.Kind is ItemKind.Image or ItemKind.Files)) throw new InvalidOperationException("Select text entries to combine them.");
            string key = string.Join(',', selected.Select(row => row.Id));
            if (draftStore.Combined.TryGetValue(key, out var combined)) return ClipboardItem.FromText(combined.Text);
            if (selected.Sum(row => row.ByteCount) > Limits.DraftBytes) throw new InvalidOperationException("Combined selection exceeds 8 MB.");
            var texts = new List<string>();
            foreach (var row in selected) { var value = await App.History.Get(row.Id); texts.Add(actionDrafts.GetValueOrDefault(value.Id, value.Text!)); }
            var merged = ClipboardItem.FromText(string.Join(App.Settings.MergeSeparator, texts)); merged.Validate(Limits.DraftBytes); return merged;
        }
        if (selected.Length == 0) return null;
        var item = await App.History.Get(selected[0].Id);
        return item.IsText && actionDrafts.TryGetValue(item.Id, out var draft) ? ClipboardItem.FromText(draft) : item;
    }
    private async Task Copy(bool usePreview = true)
    {
        try { int version = interactionVersion; var item = await CurrentItem(usePreview); if (item is null || version != interactionVersion || !IsVisible) return; await App.Clipboard.Write(item, false); ShowNotice("Copied."); }
        catch (Exception error) { ShowNotice(error.Message); }
    }
    private async Task Paste(bool plain = false, bool usePreview = true)
    {
        if (IsPasting) return;
        IsPasting = true; int version = interactionVersion; nint target = App.Paste.Target;
        try
        {
            var item = await CurrentItem(usePreview); if (item is null) return;
            if (plain && !item.IsText) { ShowNotice("Plain-text paste is available for text entries."); return; }
            if (!IsVisible || version != interactionVersion || target != App.Paste.Target) return;
            deliberatePasteDismissal = true;
            string status = await App.Paste.Paste(item, plain, DismissForPaste); App.Report(status);
        }
        catch (Exception error) { App.Report(error.Message); }
        finally { IsPasting = false; deliberatePasteDismissal = false; }
    }
    private async void OnCopy(object sender, RoutedEventArgs e) => await Copy();
    private async void OnPaste(object sender, RoutedEventArgs e) => await Paste();
    private async void OnPastePlain(object sender, RoutedEventArgs e) => await Paste(true);
    private async void OnCopyRow(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        try { var item = await App.History.Get((Guid)((Button)sender).Tag); if (item.IsText && drafts.TryGetValue(item.Id, out var draft)) item = ClipboardItem.FromText(draft); await App.Clipboard.Write(item, false); ShowNotice("Copied."); }
        catch (Exception error) { ShowNotice(error.Message); }
    }
    private async void OnPreviewRow(object sender, RoutedEventArgs e)
    {
        e.Handled = true; Guid id = (Guid)((Button)sender).Tag;
        ++interactionVersion;
        bool previous = restoringSelection; restoringSelection = true;
        try { HistoryList.SelectedItem = rows.FirstOrDefault(row => row.Id == id); }
        finally { restoringSelection = previous; }
        await SetPreview(true); await PreviewSelection(); if (Editor.Visibility == Visibility.Visible) { Editor.Focus(); Editor.CaretIndex = Editor.Text.Length; }
    }
    private async void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || IsPasting) return;
        if (ClickedHistoryRow(e.OriginalSource as DependencyObject) is not null && HistoryList.SelectedItems.Count == 1) await Paste(false, false);
    }
    private static ListBoxItem? ClickedHistoryRow(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
        {
            if (source is ButtonBase or ScrollBar) return null;
            source = VisualTreeHelper.GetParent(source);
        }
        return source as ListBoxItem;
    }
    private void OnListTyping(object sender, TextCompositionEventArgs e) { if (e.Text.Length == 0) return; Search.Focus(); Search.Text += e.Text; Search.CaretIndex = Search.Text.Length; e.Handled = true; }
    private async void OnPinRow(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await PinRow((ToggleButton)sender);
    }
    private async Task PinRow(ToggleButton button)
    {
        bool pinned = button.IsChecked == true;
        Guid id = (Guid)button.Tag;
        button.IsEnabled = false;
        try { await App.History.Pin(id, pinned); await Load(); }
        catch (Exception error) { if (button.Tag is Guid current && current == id) button.SetCurrentValue(ToggleButton.IsCheckedProperty, !pinned); ShowNotice(error.Message); }
        finally { button.IsEnabled = true; }
    }
    private async void OnPin(object sender, RoutedEventArgs e) => await Pin();
    private async Task Pin()
    {
        try { foreach (var row in HistoryList.SelectedItems.Cast<ItemSummary>().ToArray()) await App.History.Pin(row.Id, !row.Pinned); await Load(); }
        catch (Exception error) { ShowNotice(error.Message); }
    }
    private async void OnDelete(object sender, RoutedEventArgs e) => await Delete();
    private async Task Delete()
    {
        var ids = HistoryList.SelectedItems.Cast<ItemSummary>().Select(row => row.Id).ToArray(); if (ids.Length == 0) return;
        if (ids.Any(draftStore.ProtectedIds.Contains) && MessageBox.Show(this, "Delete these entries and discard their unsaved drafts?", "Delete history", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            undoToken = await App.History.Delete(ids); draftStore.Remove(ids);
            UndoButton.Visibility = Visibility.Visible; ShowNotice("Deleted. Undo is available until cleanup."); await Load();
        }
        catch (Exception error) { ShowNotice(error.Message); }
    }
    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        try { if (undoToken is Guid token) await App.History.Undo(token); undoToken = null; UndoButton.Visibility = Visibility.Collapsed; await Load(); }
        catch (Exception error) { ShowNotice(error.Message); }
    }
    private Task SetPreview(bool visible)
    {
        if (previewVisible == visible) return Task.CompletedTask;
        if (visible)
        {
            listPaneSize = new PopupSize(Math.Max(1, HistoryPanel.ActualWidth), Math.Max(1, HistoryPanel.ActualHeight));
            listWindowSize = new PopupSize(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        }
        App.RememberPopupSize(); previewVisible = visible; PreviewPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) { listPaneSize = listWindowSize = null; ++selectionVersion; previewWork?.Cancel(); ImagePreview.Source = null; detail = null; }
        var resized = App.ResizePopup();
        if (resized is not null && listWindowSize is { } outer && listPaneSize is { } pane)
            LayoutPreviewForWidth(resized.Width - Math.Max(0, outer.Width - pane.Width));
        else LayoutPreview();
        return Task.CompletedTask;
    }
    private ItemSummary[] SelectedRows()
    {
        var ids = HistoryList.SelectedItems.Cast<ItemSummary>().Select(row => row.Id).ToHashSet();
        return rows.Where(row => ids.Contains(row.Id)).ToArray();
    }
    private void LayoutPreview() => LayoutPreviewForWidth(ContentGrid.ActualWidth);
    private void LayoutPreviewForWidth(double availableWidth)
    {
        double listWidth = listPaneSize?.Width ?? 414;
        // Fix the list's dimensions; all additional window space belongs to the preview.
        bool singlePane = previewVisible && availableWidth + 0.5 < listWidth + 8 + 240;
        HistoryPanel.Visibility = singlePane ? Visibility.Collapsed : Visibility.Visible;
        HistoryColumn.Width = singlePane ? new GridLength(0) : previewVisible ? new GridLength(listWidth) : new GridLength(1, GridUnitType.Star);
        HistoryPanel.Height = previewVisible && listPaneSize is { } size ? Math.Min(size.Height, ContentGrid.ActualHeight) : double.NaN;
        HistoryPanel.VerticalAlignment = previewVisible ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        PreviewColumn.Width = previewVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PreviewPanel.Margin = new Thickness(previewVisible && !singlePane ? 8 : 0, 0, 0, 0);
    }
    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e) => LayoutPreview();
    private async void OnClosePreview(object sender, RoutedEventArgs e) => await SetPreview(false);
    internal void DiscardDrafts()
    {
        draftStore.Remove(pendingClearDraftIds); pendingClearDraftIds = []; combinedOriginal = null; combinedDraft = ""; combinedKey = null; detail = null;
        SetEditor(""); HistoryList.ItemsSource = null; rows = []; ImagePreview.Source = null; UpdateStatus();
    }
    private void DismissForPaste() { if (!KeepOpen) Dismiss(); }
    internal void Dismiss() { KeepOpenButton.IsChecked = false; UpdateKeepOpen(); ++interactionVersion; BeginAnimation(OpacityProperty, null); Opacity = 1; Hide(); App.RememberPopupSize(); previewWork?.Cancel(); ImagePreview.Source = null; if (detail?.Kind == ItemKind.Image) detail = null; searchTimer.Stop(); ++loadVersion; ++selectionVersion; }
    private void OnKeepOpen(object sender, RoutedEventArgs e) => UpdateKeepOpen();
    private void UpdateKeepOpen()
    {
        KeepOpenButton.ToolTip = KeepOpen ? "Keep open is on. Click to turn off." : "Keep Clipboard open above other apps";
        System.Windows.Automation.AutomationProperties.SetName(KeepOpenButton, KeepOpen ? "Turn off keep open" : "Keep Clipboard open above other apps");
    }
    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0006 && ((long)wParam & 0xffff) is 1 or 2 && KeepOpen) RememberActivation(lParam);
        return 0;
    }
    private void RememberActivation(nint previous)
    {
        ++interactionVersion;
        App.RememberPasteDestination(previous);
    }
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (deliberatePasteDismissal) return;
        App.Paste?.Cancel();
        if (KeepOpen) ++interactionVersion; else Dismiss();
    }
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (IsVisible && previewVisible) _ = PreviewSelection(); }
    protected override void OnClosing(CancelEventArgs e) { e.Cancel = true; Dismiss(); }
    private void OnSettings(object sender, RoutedEventArgs e) => App.ShowSettings();
    private void OnClearHistory(object sender, RoutedEventArgs e) => App.ShowSettings(true);
    private void OnDismiss(object sender, RoutedEventArgs e) => Dismiss();
    private async void OnRecording(object sender, RoutedEventArgs e) { await App.Guard(App.ToggleRecording); UpdateStatus(); }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool editing = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;
        bool buttonFocus = Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase or ComboBox;
        if (buttonFocus && !ctrl && e.Key is Key.Enter or Key.Space) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true; App.Paste.Cancel();
            if (HistoryList.SelectedItems.Count > 1) HistoryList.SelectedItems.Clear(); else if (previewVisible) await SetPreview(false); else Dismiss(); e.Handled = true;
        }
        else if (ctrl && e.Key == Key.OemComma) { App.ShowSettings(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { Search.Focus(); Search.SelectAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S && previewVisible) { e.Handled = true; await SaveDraft(); }
        else if (ctrl && e.Key == Key.P && !editing) { e.Handled = true; await Pin(); }
        else if (ctrl && e.Key == Key.C && (!editing || shift)) { e.Handled = true; await Copy(); }
        else if (e.Key == Key.Enter && (!editing || ctrl || Search.IsKeyboardFocusWithin)) { e.Handled = true; await Paste(shift); }
        else if (e.Key == Key.Delete && !editing) { e.Handled = true; await Delete(); }
        else if (e.Key == Key.Down && Search.IsKeyboardFocusWithin) { HistoryList.Focus(); if (rows.Count > 0 && HistoryList.SelectedItem is null) HistoryList.SelectedIndex = 0; e.Handled = true; }
        else if (e.Key == Key.Space && !editing) { e.Handled = true; await SetPreview(!previewVisible); if (previewVisible) await PreviewSelection(); }
        else if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D9 && !editing)
        {
            e.Handled = true; int index = e.Key - Key.D1; if (index < rows.Count) { HistoryList.SelectedIndex = index; await Paste(shift, false); }
            e.Handled = true;
        }
    }

    internal async Task<bool> ResolveClear(bool includePinned)
    {
        var affected = new HashSet<Guid>();
        foreach (var id in draftStore.ProtectedIds)
        {
            try { if (includePinned || !(await App.History.Get(id)).Pinned) affected.Add(id); }
            catch (System.IO.InvalidDataException) { affected.Add(id); }
        }
        pendingClearDraftIds = affected;
        if (affected.Count == 0) return true;
        return MessageBox.Show("This will permanently delete entries with unsaved drafts and discard those affected drafts. Continue? Choose No to copy or save them elsewhere first.", "Unsaved drafts", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    internal async Task<bool> ResolveDrafts()
    {
        if (!HasDrafts) return true;
        var answer = MessageBox.Show("Save individual text drafts before quitting? Combined drafts can be copied from the preview. Choose Cancel to return.", "Unsaved drafts", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;
        if (draftStore.Combined.Count != 0) { ShowNotice("Copy the combined draft or Revert it before quitting."); return false; }
        try { foreach (var draft in drafts.ToArray()) { await App.History.SaveText(draft.Key, draft.Value); drafts.Remove(draft.Key); } return true; }
        catch (Exception error) { ShowNotice(error.Message); return false; }
    }
}

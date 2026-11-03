using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private IReadOnlyList<ItemSummary> rows = [];
    internal IReadOnlyCollection<Guid> DraftIds => draftStore.ProtectedIds;
    internal bool HasDrafts => draftStore.HasChanges;
    internal bool IsPasting { get; private set; }
    internal bool PreviewVisible => previewVisible;

    public HistoryWindow()
    {
        InitializeComponent();
        searchTimer.Tick += async (_, _) => { searchTimer.Stop(); offset = 0; await Load(); };
        SizeChanged += (_, _) => { if (previewVisible) PreviewColumn.Width = new GridLength(Math.Min(408, ActualWidth * 0.45)); };
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
        ShortcutLabel.Text = $"{App.Settings.Shortcut} · {rows.Count} items";
    }

    internal async Task Open()
    {
        if (!previewVisible && !HasDrafts)
        {
            dayOffset = 0; Dates.SelectedIndex = 0; offset = 0;
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
        if (HasDrafts || previewVisible || offset > 0 || HistoryList.SelectedItems.Count > 0 || Search.Text.Length > 0 || dayOffset != 0 || Dates.SelectedIndex != 0)
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
        int version = ++selectionVersion;
        detail = null; combinedOriginal = null; combinedKey = null; combinedDraft = "";
        Editor.IsEnabled = false; SaveButton.IsEnabled = false; TextActions.IsEnabled = false; ImagePreview.Source = null;
        SetEditor(""); FilePreview.Clear(); PreviewTitle.Text = "Preview"; PreviewMetadata.Text = "";
        var selected = HistoryList.SelectedItems.Cast<ItemSummary>().OrderBy(row => Array.IndexOf(rows.ToArray(), row)).ToArray();
        if (selected.Length == 0) { detail = null; return; }
        try
        {
            if (selected.Length > 1)
            {
                if (selected.Any(s => s.Kind is ItemKind.Image or ItemKind.Files)) { ShowNotice("Select text entries to combine them."); return; }
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
            else if (value.Kind == ItemKind.Image)
            {
                Editor.Visibility = FilePreview.Visibility = Visibility.Collapsed; ImagePreview.Visibility = Visibility.Visible;
                using var stream = new System.IO.MemoryStream(value.Image!);
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; if (value.Height > value.Width) image.DecodePixelHeight = Math.Min(value.Height, 1024); else image.DecodePixelWidth = Math.Min(value.Width, 1024); image.StreamSource = stream; image.EndInit(); image.Freeze();
                ImagePreview.Source = image; TextActions.IsEnabled = SaveButton.IsEnabled = false;
            }
            else
            {
                ImagePreview.Source = null; Editor.Visibility = ImagePreview.Visibility = Visibility.Collapsed; FilePreview.Visibility = Visibility.Visible;
                FilePreview.Text = string.Join(Environment.NewLine, value.Files!); TextActions.IsEnabled = SaveButton.IsEnabled = false;
            }
        }
        catch (Exception error) { ShowNotice(error.Message); }
    }

    private void SetEditor(string value)
    {
        settingEditor = true; Editor.IsUndoEnabled = false; Editor.Text = value; Editor.IsUndoEnabled = true; settingEditor = false;
    }
    private void ShowText()
    {
        ImagePreview.Source = null; Editor.IsEnabled = true; Editor.Visibility = Visibility.Visible; ImagePreview.Visibility = FilePreview.Visibility = Visibility.Collapsed; TextActions.IsEnabled = true;
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
        var selected = HistoryList.SelectedItems.Cast<ItemSummary>().OrderBy(row => Array.IndexOf(rows.ToArray(), row)).ToArray();
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
            string status = await App.Paste.Paste(item, plain, Dismiss); App.Report(status);
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
        HistoryList.SelectedItem = rows.FirstOrDefault(row => row.Id == id); await SetPreview(true); await PreviewSelection(); if (Editor.Visibility == Visibility.Visible) { Editor.Focus(); Editor.CaretIndex = Editor.Text.Length; }
    }
    private async void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || IsPasting) return;
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not ListBoxItem)
        {
            if (source is Button or System.Windows.Controls.Primitives.ScrollBar) return;
            source = VisualTreeHelper.GetParent(source);
        }
        if (source is ListBoxItem && HistoryList.SelectedItems.Count == 1) await Paste(false, false);
    }
    private void OnListTyping(object sender, TextCompositionEventArgs e) { if (e.Text.Length == 0) return; Search.Focus(); Search.Text += e.Text; Search.CaretIndex = Search.Text.Length; e.Handled = true; }
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
    private async Task SetPreview(bool visible)
    {
        App.RememberPopupSize(); previewVisible = visible; PreviewPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; PreviewColumn.Width = new GridLength(visible ? 408 : 0);
        if (!visible) { ++selectionVersion; ImagePreview.Source = null; detail = null; }
        App.ResizePopup(visible ? 972 : 540, ActualHeight);
        await Task.CompletedTask;
    }
    private async void OnClosePreview(object sender, RoutedEventArgs e) => await SetPreview(false);
    internal void DiscardDrafts()
    {
        draftStore.Remove(pendingClearDraftIds); pendingClearDraftIds = []; combinedOriginal = null; combinedDraft = ""; combinedKey = null; detail = null;
        SetEditor(""); HistoryList.ItemsSource = null; rows = []; ImagePreview.Source = null; UpdateStatus();
    }
    internal void Dismiss() { ++interactionVersion; BeginAnimation(OpacityProperty, null); Opacity = 1; Hide(); App.RememberPopupSize(); ImagePreview.Source = null; if (detail?.Kind == ItemKind.Image) detail = null; searchTimer.Stop(); ++loadVersion; ++selectionVersion; }
    private void OnDeactivated(object? sender, EventArgs e) { if (!deliberatePasteDismissal) { App.Paste.Cancel(); Dismiss(); } }
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (IsVisible && previewVisible) _ = PreviewSelection(); }
    protected override void OnClosing(CancelEventArgs e) { e.Cancel = true; Dismiss(); }
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is TextBlock && e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void OnSettings(object sender, RoutedEventArgs e) => App.ShowSettings();
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

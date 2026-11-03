using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Clipboard.Core;

namespace Clipboard.Windows;

public static class HistoryThumbnail
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached("Item", typeof(ItemSummary), typeof(HistoryThumbnail), new PropertyMetadata(null, Changed));
    private static readonly DependencyProperty WorkProperty = DependencyProperty.RegisterAttached("Work", typeof(CancellationTokenSource), typeof(HistoryThumbnail));
    public static void SetItem(DependencyObject target, ItemSummary value) => target.SetValue(ItemProperty, value);
    public static ItemSummary? GetItem(DependencyObject target) => (ItemSummary?)target.GetValue(ItemProperty);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Image image) return;
        image.Loaded -= Loaded; image.Unloaded -= Unloaded;
        image.IsVisibleChanged -= VisibilityChanged;
        image.Loaded += Loaded; image.Unloaded += Unloaded;
        image.IsVisibleChanged += VisibilityChanged;
        Stop(image); if (image.IsLoaded && image.IsVisible) Start(image);
    }
    private static void Loaded(object sender, RoutedEventArgs args) => Start((Image)sender);
    private static void Unloaded(object sender, RoutedEventArgs args) => Stop((Image)sender);
    private static void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        var image = (Image)sender;
        if (image.IsLoaded && image.IsVisible) Start(image); else Stop(image);
    }
    private static void Stop(Image image)
    {
        if (image.GetValue(WorkProperty) is CancellationTokenSource work) { work.Cancel(); work.Dispose(); }
        image.ClearValue(WorkProperty); image.Source = null; image.ToolTip = null;
    }
    private static async void Start(Image image)
    {
        Stop(image);
        if (!image.IsLoaded || !image.IsVisible) return;
        if (GetItem(image) is not { Kind: ItemKind.Image or ItemKind.Files } item || Application.Current is not App app || app.History is null) return;
        var work = new CancellationTokenSource(); var cancellation = work.Token;
        image.SetValue(WorkProperty, work);
        try
        {
            var bitmap = await ImageContent.Load(app.History, item.Id, 160, cancellation);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(image.GetValue(WorkProperty), work)) image.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!cancellation.IsCancellationRequested && ReferenceEquals(image.GetValue(WorkProperty), work)) image.ToolTip = "Image preview unavailable. The file may have moved or its format may be unsupported.";
        }
    }
}

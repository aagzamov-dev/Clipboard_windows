using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Clipboard.Core;

namespace Clipboard.Windows;

internal static class ImageContent
{
    // Bound payload reads and decodes together, including virtualized list thumbnails.
    private static readonly SemaphoreSlim workers = new(2, 2);

    internal static async Task<BitmapSource?> Load(HistoryStore history, Guid id, int edge, CancellationToken cancellation)
    {
        await workers.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var item = await history.Get(id).ConfigureAwait(false);
            return await Task.Run(() => Decode(item, edge, cancellation), cancellation).ConfigureAwait(false);
        }
        finally { workers.Release(); }
    }

    internal static async Task<BitmapSource?> Load(ClipboardItem item, int edge, CancellationToken cancellation)
    {
        await workers.WaitAsync(cancellation).ConfigureAwait(false);
        try { return await Task.Run(() => Decode(item, edge, cancellation), cancellation).ConfigureAwait(false); }
        finally { workers.Release(); }
    }

    internal static string? FilePath(ClipboardItem item) => item.Kind == ItemKind.Files
        ? item.Files?.FirstOrDefault(path => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/' &&
            Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico")
        : null;

    private static BitmapSource? Decode(ClipboardItem item, int edge, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (edge is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(edge));
        byte[] bytes;
        if (item.Kind == ItemKind.Image) bytes = item.Image ?? throw new InvalidDataException("Image data is missing.");
        else if (FilePath(item) is { } path)
        {
            using var file = Native.LocalImageFile.Open(path);
            if (file is null) return null;
            long length = file.Length;
            if (length is < 1 or > Limits.ImageBytes) throw new InvalidDataException("Image preview supports files up to 20 MB.");
            bytes = new byte[checked((int)length)]; file.ReadExactly(bytes);
        }
        else return null;
        if (bytes.Length is < 1 or > Limits.ImageBytes) throw new InvalidDataException("Image preview exceeds 20 MB.");
        cancellation.ThrowIfCancellationRequested();
        using var input = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || (long)frame.PixelWidth * frame.PixelHeight > Limits.ImagePixels)
            throw new InvalidDataException("Image preview exceeds 64 million pixels.");
        int width = frame.PixelWidth, height = frame.PixelHeight;
        input.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (height > width) image.DecodePixelHeight = Math.Min(height, edge); else image.DecodePixelWidth = Math.Min(width, edge);
        image.StreamSource = input; image.EndInit(); image.Freeze();
        cancellation.ThrowIfCancellationRequested(); return image;
    }
}

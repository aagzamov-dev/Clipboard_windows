namespace Clipboard.Core;

public readonly record struct PixelRect(int Left, int Top, int Width, int Height);

public static class PopupGeometry
{
    public static PixelRect Resize(PixelRect work, PixelRect current, double scale, double width, double height)
    {
        if (work.Width <= 0 || work.Height <= 0 || current.Width <= 0 || current.Height <= 0 ||
            !double.IsFinite(scale) || scale <= 0 || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(work));
        // Keep an on-screen anchor unchanged. Only repair an already off-screen window.
        int x = Math.Clamp(current.Left, work.Left, work.Left + work.Width - Math.Min(current.Width, work.Width));
        int y = Math.Clamp(current.Top, work.Top, work.Top + work.Height - Math.Min(current.Height, work.Height));
        int w = Math.Clamp((int)Math.Round(width * scale), 1, work.Left + work.Width - x);
        int h = Math.Clamp((int)Math.Round(height * scale), 1, work.Top + work.Height - y);
        return new(x, y, w, h);
    }

    public static PixelRect Place(PixelRect work, int pointerX, int pointerY, double scale, double width, double height)
    {
        if (work.Width <= 0 || work.Height <= 0 || !double.IsFinite(scale) || scale <= 0 ||
            !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(work));
        int margin = Math.Min((int)Math.Round(12 * scale), Math.Min(work.Width, work.Height) / 4);
        int w = Math.Clamp((int)Math.Round(width * scale), 1, Math.Max(1, work.Width - margin * 2));
        int h = Math.Clamp((int)Math.Round(height * scale), 1, Math.Max(1, work.Height - margin * 2));
        int x = Math.Clamp(pointerX + margin, work.Left + margin, work.Left + work.Width - margin - w);
        int y = Math.Clamp(pointerY + margin, work.Top + margin, work.Top + work.Height - margin - h);
        return new(x, y, w, h);
    }
}

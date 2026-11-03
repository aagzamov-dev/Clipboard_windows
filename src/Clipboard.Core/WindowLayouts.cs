using System.Text.Json;

namespace Clipboard.Core;

public sealed record PopupSize(double Width, double Height);

public sealed class WindowLayouts
{
    private readonly string path;
    private readonly Dictionary<string, PopupSize> sizes;
    private readonly SemaphoreSlim gate = new(1, 1);

    public WindowLayouts(string path)
    {
        this.path = path;
        try
        {
            sizes = File.Exists(path) && new FileInfo(path).Length < 65536
                ? JsonSerializer.Deserialize<Dictionary<string, PopupSize>>(File.ReadAllText(path)) ?? [] : [];
            if (sizes.Count > 64 || sizes.Any(p => p.Key.Length > 128 || !Valid(p.Value))) sizes = [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { sizes = []; }
    }

    private static bool Valid(PopupSize? size) => size is not null && double.IsFinite(size.Width) && double.IsFinite(size.Height) &&
        size.Width is >= 320 and <= 2000 && size.Height is >= 300 and <= 1600;

    public PopupSize Get(string monitor, bool preview)
    {
        lock (sizes) return sizes.GetValueOrDefault(monitor + (preview ? ":preview" : ":list"), new(preview ? 972 : 540, 594));
    }

    public Task Remember(string monitor, bool preview, PopupSize size)
    {
        if (!Valid(size) || monitor.Length > 100) return Task.CompletedTask;
        lock (sizes)
        {
            sizes[monitor + (preview ? ":preview" : ":list")] = size;
            while (sizes.Count > 64) sizes.Remove(sizes.Keys.First());
        }
        return Save();
    }

    private async Task Save()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await Task.Run(() =>
            {
                string json; lock (sizes) json = JsonSerializer.Serialize(sizes);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(temporary, json); File.Move(temporary, path, overwrite: true);
            }).ConfigureAwait(false);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } finally { gate.Release(); } }
    }
}

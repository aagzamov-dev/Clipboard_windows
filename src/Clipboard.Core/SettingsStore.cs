using System.Text.Json;

namespace Clipboard.Core;

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        if (new FileInfo(Path).Length > 1024 * 1024) throw new InvalidDataException("Settings file is too large.");
        using var stream = File.OpenRead(Path);
        var settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions)
            ?? throw new InvalidDataException("Settings file is empty.");
        settings.Validate();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        settings.Validate();
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Clipboard.Core;

public enum ItemKind { Text, Link, Code, Image, Files }

public static class Limits
{
    public const int TextBytes = 2 * 1024 * 1024;
    public const int ImageBytes = 20 * 1024 * 1024;
    public const int ImagePixels = 64_000_000;
    public const int DraftBytes = 8 * 1024 * 1024;
    public const int Files = 100;
    public const int PageSize = 100;
}

public sealed record ClipboardItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public ItemKind Kind { get; init; }
    public string? Text { get; init; }
    public string? Rtf { get; init; }
    public byte[]? Image { get; init; }
    public string[]? Files { get; init; }
    public string Source { get; init; } = "";
    public bool Pinned { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    [JsonIgnore]
    public bool IsText => Kind is ItemKind.Text or ItemKind.Link or ItemKind.Code;
    [JsonIgnore]
    public long ByteCount => Encoding.UTF8.GetByteCount(Text ?? "") + Encoding.UTF8.GetByteCount(Rtf ?? "") +
        (Image?.LongLength ?? 0) + (Files?.Sum(f => (long)Encoding.UTF8.GetByteCount(f ?? "")) ?? 0);
    [JsonIgnore]
    public string Preview => Kind switch
    {
        ItemKind.Image => $"Image · {Width} × {Height}",
        ItemKind.Files => string.Join(" · ", (Files ?? []).Select(System.IO.Path.GetFileName)),
        _ => (Text ?? "").Replace('\r', ' ').Replace('\n', ' ')
    };

    public static ClipboardItem FromText(string text) => new() { Kind = Classify(text), Text = text };
    public static ItemKind Classify(string text)
    {
        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length != 0)
            return ItemKind.Link;
        string[] prefixes = ["import ", "func ", "let ", "const ", "function ", "class ", "def ", "SELECT "];
        return prefixes.Any(text.StartsWith) ? ItemKind.Code : ItemKind.Text;
    }

    public void Validate(int textLimit = Limits.TextBytes)
    {
        if (Id == Guid.Empty || !Enum.IsDefined(Kind) || Source is null || Source.Length > 512 ||
            ByteCount > Limits.ImageBytes + 2L * Limits.TextBytes)
            throw new InvalidDataException("Invalid clipboard item.");
        if (IsText)
        {
            if (Text is null || Encoding.UTF8.GetByteCount(Text) > textLimit ||
                Encoding.UTF8.GetByteCount(Rtf ?? "") > Limits.TextBytes || Image is not null || Files is not null)
                throw new InvalidDataException("Text exceeds the supported limit or contains invalid representations.");
        }
        else if (Kind == ItemKind.Image)
        {
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (Image is null || Image.Length is < 24 or > Limits.ImageBytes || !Image.AsSpan(0, 8).SequenceEqual(signature) ||
                Width < 1 || Height < 1 || (long)Width * Height > Limits.ImagePixels || Text is not null || Rtf is not null || Files is not null)
                throw new InvalidDataException("Invalid or oversized image.");
            int actualWidth = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(Image.AsSpan(16, 4));
            int actualHeight = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(Image.AsSpan(20, 4));
            if (actualWidth != Width || actualHeight != Height) throw new InvalidDataException("Image dimensions do not match its content.");
        }
        else if (Files is null || Files.Length is < 1 or > Limits.Files ||
                 Files.Any(f => string.IsNullOrEmpty(f) || f.Length > 32767 || f.Contains('\0') || !IsAbsoluteFile(f)) ||
                 Text is not null || Rtf is not null || Image is not null)
            throw new InvalidDataException("Invalid file references.");
    }

    private static bool IsAbsoluteFile(string value) => Path.IsPathFullyQualified(value) ||
        (value.Length > 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\') || value.StartsWith(@"\\");

    public string Hash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(byte[] bytes)
        {
            Span<byte> length = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, bytes.LongLength);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        Add(Encoding.UTF8.GetBytes(Kind.ToString()));
        Add(Encoding.UTF8.GetBytes(Text ?? "")); Add(Encoding.UTF8.GetBytes(Rtf ?? ""));
        Add(Image ?? []);
        foreach (var file in Files ?? []) Add(Encoding.UTF8.GetBytes(file));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

public sealed record ItemSummary(Guid Id, ItemKind Kind, string Preview, DateTimeOffset Created, string Source, bool Pinned, long ByteCount)
{
    public string KindLabel => Pinned ? $"Pinned · {Kind}" : Kind.ToString();
    public string SourceLabel => string.IsNullOrWhiteSpace(Source) ? "Unknown app" : Source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Source[..^4] : Source;
    public string TimeLabel => Created.ToLocalTime().Date == DateTime.Today ? Created.ToLocalTime().ToString("HH:mm") : Created.ToLocalTime().ToString("MMM d, HH:mm");
    public string ListMetadata => $"{KindLabel}  ·  {SourceLabel}  ·  {TimeLabel}";
    public string Metadata => $"{Created.ToLocalTime():g}  ·  {Source}  ·  {ByteCount:N0} bytes";
}

public sealed record HistoryQuery(string Search = "", ItemKind? Kind = null, bool Pinned = false,
    DateTimeOffset? From = null, DateTimeOffset? To = null, bool PinnedFirst = false, long Cutoff = long.MaxValue);

public static class TextTools
{
    public static string Transform(string text, string action) => action switch
    {
        "Trim whitespace" => text.Trim(),
        "Remove blank lines" => string.Join(Environment.NewLine, text.Replace("\r\n", "\n").Split('\n').Where(s => !string.IsNullOrWhiteSpace(s))),
        "UPPERCASE" => text.ToUpperInvariant(),
        "lowercase" => text.ToLowerInvariant(),
        "Title Case" => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower(System.Globalization.CultureInfo.CurrentCulture)),
        _ => text
    };
}

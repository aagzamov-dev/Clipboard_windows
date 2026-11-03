namespace Clipboard.Core;

[Flags]
public enum ShortcutModifiers : uint
{
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public sealed record Shortcut(ShortcutModifiers Modifiers, uint Key)
{
    public static Shortcut Default { get; } = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x56);

    public void Validate()
    {
        const ShortcutModifiers allowed = ShortcutModifiers.Alt | ShortcutModifiers.Control | ShortcutModifiers.Shift;
        // Windows-key shortcuts are reserved by the OS. Limit the editor to letters, digits, and F1–F24.
        bool supportedKey = Key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x87;
        if ((Modifiers & ~allowed) != 0 || (Modifiers & (ShortcutModifiers.Alt | ShortcutModifiers.Control)) == 0 || !supportedKey)
            throw new InvalidDataException("Choose Ctrl or Alt with a letter, number, or function key.");
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        parts.Add(Key >= 0x70 ? $"F{Key - 0x70 + 1}" : ((char)Key).ToString());
        return string.Join("+", parts);
    }
}

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public Shortcut Shortcut { get; init; } = Shortcut.Default;
    public bool ActiveDisplay { get; init; } = false;
    public bool OnboardingComplete { get; init; }
    public bool StartMonitoring { get; init; } = true;
    // Retained for compatibility with existing settings imports; active history never expires.
    public int? RetentionDays { get; init; }
    public int? MaximumCount { get; init; }
    public bool WeeklyCleanup { get; init; }
    public DateTimeOffset? LastCleanup { get; init; }
    public bool PinnedFirst { get; init; }
    public string Appearance { get; init; } = "System";
    public string[] ExcludedApps { get; init; } = [];
    public string MergeSeparator { get; init; } = "\n\n";
    public bool OpenAnimation { get; init; } = true;
    public bool StartAtLogin { get; init; }
    public bool CheckForUpdates { get; init; } = true;

    public void Validate()
    {
        if (Version != 1) throw new InvalidDataException("Unsupported settings version. The existing settings file was preserved.");
        if (Shortcut is null) throw new InvalidDataException("Missing shortcut settings.");
        Shortcut.Validate();
        if (RetentionDays is < 1 or > 36500 || MaximumCount is < 1 or > 1_000_000 ||
            Appearance is not ("System" or "Light" or "Dark") || ExcludedApps is null || ExcludedApps.Length > 100 ||
            ExcludedApps.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 512) ||
            MergeSeparator is null || System.Text.Encoding.UTF8.GetByteCount(MergeSeparator) > 256)
            throw new InvalidDataException("Some settings are outside the supported range.");
    }
}

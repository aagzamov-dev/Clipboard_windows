using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Clipboard.Core;
using Microsoft.Data.Sqlite;

string directory = Path.Combine(Path.GetTempPath(), $"clipboard-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory); int checks = 0;
try
{
    var settingsPath = Path.Combine(directory, "settings.json"); var settingsStore = new SettingsStore(settingsPath);
    var defaults = settingsStore.Load();
    Check(defaults.Shortcut.ToString() == "Ctrl+Alt+V" && !defaults.ActiveDisplay && !defaults.OnboardingComplete && defaults.RetentionDays == 30, "Private, mouse-positioned defaults");
    Check(!File.Exists(settingsPath), "Default load never writes preferences");
    var custom = defaults with { Shortcut = new(ShortcutModifiers.Control | ShortcutModifiers.Shift, 0x70), ExcludedApps = ["KeePass.exe"], Appearance = "Dark" };
    settingsStore.Save(custom);
    Check(JsonSerializer.Serialize(settingsStore.Load()) == JsonSerializer.Serialize(custom), "All custom settings survive restart");
    string saved = File.ReadAllText(settingsPath);
    foreach (var invalid in new[] { custom with { Version = 2 }, custom with { Shortcut = new(ShortcutModifiers.Windows, 0x56) },
        custom with { Shortcut = new(ShortcutModifiers.Shift, 0x56) }, custom with { RetentionDays = 0 }, custom with { MaximumCount = -1 },
        custom with { ExcludedApps = [""] }, custom with { Appearance = "Unknown" }, custom with { MergeSeparator = new string('x', 257) } })
        Throws<InvalidDataException>(() => settingsStore.Save(invalid));
    Check(File.ReadAllText(settingsPath) == saved, "Invalid preferences preserve saved file");
    File.WriteAllText(settingsPath, "{broken"); Throws<JsonException>(() => settingsStore.Load());
    Check(File.ReadAllText(settingsPath) == "{broken", "Corrupt settings preserved");
    foreach (string invalid in new[] { "null", "{\"Version\":2}", "{\"Shortcut\":null}", new string(' ', 1024 * 1024 + 1) })
    { File.WriteAllText(settingsPath, invalid); Throws<InvalidDataException>(() => settingsStore.Load()); }
    string blocked = Path.Combine(directory, "blocked.json"); Directory.CreateDirectory(blocked);
    try { new SettingsStore(blocked).Save(defaults); throw new Exception("Expected failed replacement"); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    Check(Directory.GetFiles(directory, ".settings-*.tmp").Length == 0, "Failed settings save cleans temporary file");

    using var store = new HistoryStore(Path.Combine(directory, "history.db"));
    var first = ClipboardItem.FromText("alpha café Unicode 你好") with { Created = DateTimeOffset.UtcNow.AddSeconds(-2), Source = "editor.exe" };
    Check(await store.Capture(first), "Capture commits first text");
    Check(!await store.Capture(first with { Id = Guid.NewGuid() }), "Consecutive duplicate ignored");
    var second = ClipboardItem.FromText("const beta = 2;") with { Source = "code.exe" };
    await store.Capture(second);
    var repeated = first with { Id = Guid.NewGuid(), Created = DateTimeOffset.UtcNow };
    Check(await store.Capture(repeated), "Nonconsecutive recopy retained");
    var page = await store.Query(new()); Check(page.Count == 3 && page[0].Id == repeated.Id, "Newest-first paged summaries");
    Check((await store.Query(new(Search: "café"))).Count == 2, "Unicode indexed search");
    Check((await store.Query(new(Search: "alpha \" OR *"))).Count == 0, "Search cannot inject FTS syntax");
    Check((await store.Query(new(Kind: ItemKind.Code))).Single().Id == second.Id, "Type filter");
    Check((await store.Query(new(From: DateTimeOffset.UtcNow.AddDays(-1), To: DateTimeOffset.UtcNow.AddDays(1)))).Count == 3, "Date bounds");
    long cutoff = await store.Cutoff(); var late = ClipboardItem.FromText("later capture"); await store.Capture(late);
    Check((await store.Query(new(Cutoff: cutoff))).Count == 3, "Stable cutoff excludes new captures");
    await store.Pin(first.Id, true);
    Check((await store.Query(new(Pinned: true))).Single().Id == first.Id && (await store.Get(first.Id)).Pinned, "Pin state matches summary and detail");
    await store.SaveText(second.Id, "edited searchable value");
    Check((await store.Query(new(Search: "searchable"))).Single().Id == second.Id && (await store.Get(second.Id)).Rtf is null, "Explicit save updates search and removes stale RTF");
    Check((await store.Query(new(Search: "beta"))).Count == 0, "Editing removes old search terms");
    Guid undo = await store.Delete([second.Id, late.Id]);
    Check((await store.Query(new())).Count == 2, "Bulk soft delete hides entries");
    await store.Undo(undo); Check((await store.Query(new())).Count == 4, "Undo restores selection");
    var empty = ClipboardItem.FromText(""); await store.Capture(empty); Check((await store.Get(empty.Id)).Text == "", "Empty text round-trips");
    var image = new ClipboardItem { Kind = ItemKind.Image, Image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="), Width = 1, Height = 1 };
    await store.Capture(image); Check((await store.Get(image.Id)).Image!.SequenceEqual(image.Image), "PNG image round-trip");
    Throws<InvalidDataException>(() => (image with { Width = 2 }).Validate());
    var file = new ClipboardItem { Kind = ItemKind.Files, Files = [@"C:\Documents\example.txt"] }; await store.Capture(file);
    Check((await store.Get(file.Id)).Files!.Single() == file.Files[0], "File references round-trip without touching originals");
    Throws<InvalidDataException>(() => (file with { Files = ["relative.txt"] }).Validate());
    Throws<InvalidDataException>(() => ClipboardItem.FromText(new string('x', Limits.TextBytes + 1)).Validate());
    Check(ClipboardItem.Classify("https://example.com") == ItemKind.Link && ClipboardItem.Classify("def test():") == ItemKind.Code, "Link/code classification");

    var drafts = new DraftStore(); Guid id1 = Guid.NewGuid(), id2 = Guid.NewGuid();
    drafts.SetIndividual(id1, "old", "new"); drafts.SetCombined("selection", "a\nb", "combined edit", [id1, id2]);
    Check(drafts.ProtectedIds.Count == 2 && drafts.HasChanges, "Single and combined drafts protect original entries");
    Throws<InvalidDataException>(() => drafts.SetIndividual(id2, "old", new string('x', Limits.TextBytes + 1)));
    Throws<InvalidDataException>(() => drafts.SetCombined("large", "", new string('x', Limits.DraftBytes), [id2]));
    Check(drafts.Combined["selection"].Text == "combined edit", "Rejected draft leaves previous edits intact");
    drafts.Remove([id1]); Check(!drafts.HasChanges, "Deleting originals clears related draft sessions");
    Check(TextTools.Transform(" A \n\n B ", "Remove blank lines") == " A " + Environment.NewLine + " B ", "Text tools preserve nonblank content");

    foreach (var work in new[] { new PixelRect(-1920, 0, 1920, 1040), new PixelRect(0, -1440, 2560, 1400), new PixelRect(0, 0, 800, 560) })
        foreach (double scale in new[] { 1.0, 1.5, 2.0, 3.0 })
            foreach (var pointer in new[] { (work.Left, work.Top), (work.Left + work.Width - 1, work.Top + work.Height - 1) })
            {
                var placed = PopupGeometry.Place(work, pointer.Item1, pointer.Item2, scale, 972, 594);
                if (placed.Left < work.Left || placed.Top < work.Top || placed.Left + placed.Width > work.Left + work.Width || placed.Top + placed.Height > work.Top + work.Height)
                    throw new Exception("Popup escaped working area");
            }
    Check(true, "Mouse placement fits negative origins, corners, taskbars and 100–300% DPI");

    string layoutsPath = Path.Combine(directory, "layouts.json");
    var layouts = new WindowLayouts(layoutsPath);
    await layouts.Remember("DISPLAY1", false, new(620, 700)); await layouts.Remember("DISPLAY2", true, new(1100, 760));
    var reopenedLayouts = new WindowLayouts(layoutsPath);
    Check(reopenedLayouts.Get("DISPLAY1", false) == new PopupSize(620, 700) && reopenedLayouts.Get("DISPLAY2", true) == new PopupSize(1100, 760), "Per-monitor list and preview sizes persist independently");
    await layouts.Remember("DISPLAY1", false, new(double.NaN, 700));
    Check(layouts.Get("DISPLAY1", false).Width == 620, "Invalid layout cannot replace valid dimensions");
    File.WriteAllText(layoutsPath, "{broken"); Check(new WindowLayouts(layoutsPath).Get("missing", false).Width == 540, "Malformed layout safely falls back");

    await ThrowsAsync<InvalidDataException>(() => BackupService.Export(store, store.Path, ""));
    string backup = Path.Combine(directory, "encrypted.clipboard"); await BackupService.Export(store, backup, "correct horse battery");
    using var restored = new HistoryStore(Path.Combine(directory, "restored.db"));
    int imported = await BackupService.Import(restored, backup, "correct horse battery");
    Check(imported == (await store.Query(new())).Count, "Encrypted backup restores history");
    Check(await BackupService.Import(restored, backup, "correct horse battery") == 0, "Repeated import is idempotent");
    await ThrowsAsync<CryptographicException>(() => BackupService.Import(restored, backup, "wrong password"));
    byte[] bytes = File.ReadAllBytes(backup); bytes[50] ^= 1; string tampered = Path.Combine(directory, "tampered.clipboard"); File.WriteAllBytes(tampered, bytes);
    await ThrowsAsync<CryptographicException>(() => BackupService.Import(restored, tampered, "correct horse battery"));
    File.WriteAllBytes(tampered, File.ReadAllBytes(backup)[..^20]);
    await ThrowsAsync<EndOfStreamException>(() => BackupService.Import(restored, tampered, "correct horse battery"));
    Check((await restored.Query(new())).Count == imported, "Wrong passwords, tampering and truncation preserve live history");
    await restored.SaveText(first.Id, "conflicting identity");
    await ThrowsAsync<InvalidDataException>(() => BackupService.Import(restored, backup, "correct horse battery"));
    Check((await restored.Get(first.Id)).Text == "conflicting identity", "Conflicting backup rolls back entire import");
    var secret = ClipboardItem.FromText("SECRET_DELETE_SENTINEL_9384"); await store.Capture(secret); await store.Delete([secret.Id]);
    string plain = Path.Combine(directory, "plain.clipboard"); await BackupService.Export(store, plain, "");
    using (var archive = ZipFile.OpenRead(plain))
    using (var data = new MemoryStream())
    {
        archive.Entries.Single().Open().CopyTo(data);
        Check(!System.Text.Encoding.UTF8.GetString(data.ToArray()).Contains(secret.Text!, StringComparison.Ordinal), "Deleted plaintext absent from exported database and free pages");
    }
    using var merged = new HistoryStore(Path.Combine(directory, "merged.db"));
    Check(await BackupService.Import(merged, plain, "") == imported, "Plain ZIP backup round-trip");
    string traversal = Path.Combine(directory, "traversal.clipboard");
    using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escape.db").Open()); writer.Write("bad"); }
    await ThrowsAsync<InvalidDataException>(() => BackupService.Import(merged, traversal, ""));
    Check(!File.Exists(Path.Combine(directory, "escape.db")), "Archive traversal rejected");

    string hostileDb = Path.Combine(directory, "hostile.db");
    using (var hostile = new SqliteConnection($"Data Source={hostileDb};Pooling=False"))
    {
        hostile.Open(); using var command = hostile.CreateCommand(); command.CommandText = "PRAGMA user_version=1; CREATE VIEW entries AS SELECT 1 AS id"; command.ExecuteNonQuery();
    }
    string hostileZip = Path.Combine(directory, "hostile.clipboard");
    using (var archive = ZipFile.Open(hostileZip, ZipArchiveMode.Create)) archive.CreateEntryFromFile(hostileDb, "history.db");
    await ThrowsAsync<InvalidDataException>(() => BackupService.Import(merged, hostileZip, ""));
    Check((await merged.Query(new())).Count == imported, "Untrusted SQLite view rejected without live mutations");
    using (var conflictSource = new HistoryStore(Path.Combine(directory, "conflict-source.db")))
    {
        var insertedBeforeConflict = ClipboardItem.FromText("must roll back"); await conflictSource.Capture(insertedBeforeConflict);
        await conflictSource.Capture(first with { Text = "conflicting payload" });
        string conflictingZip = Path.Combine(directory, "conflict.clipboard"); await BackupService.Export(conflictSource, conflictingZip, "");
        await ThrowsAsync<InvalidDataException>(() => BackupService.Import(merged, conflictingZip, ""));
        await ThrowsAsync<InvalidDataException>(() => merged.Get(insertedBeforeConflict.Id));
        Check(true, "Import rolls back rows inserted before a later conflict");
    }
    var old = ClipboardItem.FromText("old unpinned") with { Created = DateTimeOffset.UtcNow.AddDays(-60) };
    var protectedOld = ClipboardItem.FromText("old unsaved draft") with { Created = DateTimeOffset.UtcNow.AddDays(-60) };
    await store.Capture(old); await store.Capture(protectedOld);
    await store.Cleanup(defaults, [protectedOld.Id]);
    await ThrowsAsync<InvalidDataException>(() => store.Get(old.Id));
    Check((await store.Get(protectedOld.Id)).Text == protectedOld.Text && (await store.Get(first.Id)).Pinned, "Retention protects pinned entries and unsaved drafts");
    await store.Clear(false); Check((await store.Query(new())).Single().Id == first.Id, "Clear unpinned preserves pins");
    await store.Clear(true); Check((await store.Query(new())).Count == 0, "Explicit clear all");

    using var paged = new HistoryStore(Path.Combine(directory, "paged.db"));
    var watch = Stopwatch.StartNew();
    for (int index = 0; index < 10000; index++) await paged.Capture(ClipboardItem.FromText($"performance entry {index:D5}"));
    Check((await paged.Query(new())).Count == Limits.PageSize && (await paged.Query(new(), Limits.PageSize)).Count == Limits.PageSize, "Large history stays paged");
    var timer = Stopwatch.StartNew(); Check((await paged.Query(new(Search: "09999"))).Count == 1, "Indexed large-history lookup");
    Console.WriteLine($"10,000 durable inserts: {watch.Elapsed.TotalSeconds:F2}s; indexed search: {timer.Elapsed.TotalMilliseconds:F1}ms");
    var closingStore = new HistoryStore(Path.Combine(directory, "closing.db"));
    var inFlight = Enumerable.Range(0, 20).Select(index => closingStore.Capture(ClipboardItem.FromText($"shutdown {index}"))).ToArray();
    await Task.Run(closingStore.Dispose);
    foreach (var task in inFlight) { try { await task; } catch (ObjectDisposedException) { } }
    await ThrowsAsync<ObjectDisposedException>(() => closingStore.Query(new()));
    Check(true, "Disposal serializes against in-flight operations and rejects new work");
    Console.WriteLine($"All {checks} regression checks passed.");
}
finally { Directory.Delete(directory, recursive: true); }

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAILED: {name}");
    checks++; Console.WriteLine($"PASS: {name}");
}
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { checks++; return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { checks++; return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

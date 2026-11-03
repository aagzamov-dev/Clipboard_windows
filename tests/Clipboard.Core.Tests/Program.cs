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
    Check(defaults.Shortcut.ToString() == "Ctrl+Alt+V" && !defaults.ActiveDisplay && !defaults.OnboardingComplete && defaults.RetentionDays is null && defaults.MaximumCount is null, "Private, mouse-positioned defaults keep unlimited history");
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
    foreach (var work in new[] { new PixelRect(-1920, 0, 1920, 1040), new PixelRect(0, -1440, 2560, 1400), new PixelRect(0, 0, 800, 560) })
        foreach (double scale in new[] { 1.0, 1.5, 2.0, 3.0 })
        {
            int w = Math.Min((int)Math.Round(440 * scale), work.Width - 24), h = Math.Min((int)Math.Round(520 * scale), work.Height - 24);
            foreach (var current in new[] { new PixelRect(work.Left + 12, work.Top + 12, w, h), new PixelRect(work.Left + work.Width - 12 - w, work.Top + work.Height - 12 - h, w, h) })
            {
                var expanded = PopupGeometry.Resize(work, current, scale, 840, 520);
                var closed = PopupGeometry.Resize(work, expanded, scale, 440, 520);
                if (expanded.Left != current.Left || expanded.Top != current.Top || closed.Left != current.Left || closed.Top != current.Top ||
                    expanded.Left + expanded.Width > work.Left + work.Width || expanded.Top + expanded.Height > work.Top + work.Height)
                    throw new Exception("Detail resize moved its anchor or escaped the working area");
            }
        }
    Check(true, "Opening and closing details preserves the anchor at monitor corners and 100–300% DPI");
    Check(PopupGeometry.Resize(new(0, 0, 1920, 1040), new(1480, 660, 440, 380), 1, 840, 520) == new PixelRect(1480, 660, 440, 380), "Details near right and bottom edges use the available space without shifting");
    var repaired = PopupGeometry.Resize(new(0, 0, 800, 560), new(2000, 2000, 440, 520), 1, 840, 520);
    Check(repaired.Left >= 0 && repaired.Top >= 0 && repaired.Left + repaired.Width <= 800 && repaired.Top + repaired.Height <= 560 && repaired.Width >= 440, "An already off-screen resize is repaired without collapsing the popup");
    Throws<ArgumentOutOfRangeException>(() => PopupGeometry.Resize(new(0, 0, 800, 560), new(10, 10, 440, 520), double.NaN, 840, 520));

    string layoutsPath = Path.Combine(directory, "layouts.json");
    var layouts = new WindowLayouts(layoutsPath);
    await layouts.Remember("DISPLAY1", false, new(620, 700)); await layouts.Remember("DISPLAY2", true, new(1100, 760));
    var reopenedLayouts = new WindowLayouts(layoutsPath);
    Check(reopenedLayouts.Get("DISPLAY1", false) == new PopupSize(620, 700) && reopenedLayouts.Get("DISPLAY2", true) == new PopupSize(1100, 760), "Per-monitor list and preview sizes persist independently");
    await layouts.Remember("EDGE", true, new(840, 520));
    var edgeBounds = PopupGeometry.Resize(new(0, 0, 1920, 1040), new(1480, 660, 440, 380), 1, 840, 520);
    var constrained = new PopupSize(edgeBounds.Width, edgeBounds.Height);
    await layouts.Remember("EDGE", true, constrained, constrained);
    var centered = layouts.Get("EDGE", true);
    Check(centered == new PopupSize(840, 520) && PopupGeometry.Resize(new(0, 0, 1920, 1040), new(100, 80, 440, 520), 1, centered.Width, centered.Height).Width == 840, "Edge-constrained preview close preserves the preferred size for reopening at the center");
    var afterEdgeDismiss = new WindowLayouts(layoutsPath);
    Check(afterEdgeDismiss.Get("EDGE", true) == new PopupSize(840, 520), "Dismissing an edge-constrained preview does not persist its temporary fit");
    await layouts.Remember("EDGE", true, new(700, 480), constrained);
    Check(new WindowLayouts(layoutsPath).Get("EDGE", true) == new PopupSize(700, 480), "An intentional resize replaces the preferred size after an automatic constraint");
    await layouts.Remember("DISPLAY1", false, new(double.NaN, 700));
    Check(layouts.Get("DISPLAY1", false).Width == 620, "Invalid layout cannot replace valid dimensions");
    File.WriteAllText(layoutsPath, "{broken"); Check(new WindowLayouts(layoutsPath).Get("missing", false) == new PopupSize(440, 520), "Malformed layout safely falls back to compact size");

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
    Check((await store.Get(old.Id)).Text == old.Text, "Default maintenance keeps old active history");
    await store.Cleanup(defaults with { RetentionDays = 1, MaximumCount = 1 }, [protectedOld.Id]);
    Check((await store.Get(old.Id)).Text == old.Text, "Legacy age and count settings cannot expire active history");
    await store.ClearOlderThan(DateTimeOffset.UtcNow.AddDays(-30), [protectedOld.Id]);
    await ThrowsAsync<InvalidDataException>(() => store.Get(old.Id));
    Check((await store.Get(protectedOld.Id)).Text == protectedOld.Text && (await store.Get(first.Id)).Pinned, "Manual age cleanup protects pinned entries and unsaved drafts");
    await store.Clear(false); Check((await store.Query(new())).Single().Id == first.Id, "Clear unpinned preserves pins");
    await store.Clear(true); Check((await store.Query(new())).Count == 0, "Explicit clear all");

    foreach (int days in new[] { 7, 30 })
    {
        using var ageStore = new HistoryStore(Path.Combine(directory, $"age-{days}.db"));
        var ageCutoff = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds());
        var older = ClipboardItem.FromText("older") with { Created = ageCutoff.AddMilliseconds(-1) };
        var boundary = ClipboardItem.FromText("boundary") with { Created = ageCutoff };
        var recent = ClipboardItem.FromText("recent") with { Created = ageCutoff.AddDays(1) };
        var pin = ClipboardItem.FromText("pinned old") with { Created = ageCutoff.AddDays(-1), Pinned = true };
        var draft = ClipboardItem.FromText("draft old") with { Created = ageCutoff.AddDays(-1) };
        var combined = ClipboardItem.FromText("combined original") with { Created = ageCutoff.AddDays(-1) };
        foreach (var value in new[] { older, boundary, recent, pin, draft, combined }) await ageStore.Capture(value);
        var ageDrafts = new DraftStore(); ageDrafts.SetIndividual(draft.Id, draft.Text!, "unsaved");
        ageDrafts.SetCombined("selection", "original", "combined edit", [combined.Id]);
        Check(await ageStore.ClearOlderThan(ageCutoff, ageDrafts.ProtectedIds) == 1, $"{days}-day cleanup removes only strictly older eligible entries");
        var survivors = (await ageStore.Query(new())).Select(row => row.Id).ToHashSet();
        Check(survivors.SetEquals(new[] { boundary.Id, recent.Id, pin.Id, draft.Id, combined.Id }), $"{days}-day cleanup keeps boundary, recent, pinned, individual and combined drafts");
        Check(await ageStore.ClearOlderThan(ageCutoff, ageDrafts.ProtectedIds) == 0, $"{days}-day cleanup is idempotent");
        var duplicateProbe = ClipboardItem.FromText("dedup after no-op cleanup");
        await ageStore.Capture(duplicateProbe);
        await ageStore.ClearOlderThan(ageCutoff, ageDrafts.ProtectedIds);
        Check(!await ageStore.Capture(ClipboardItem.FromText(duplicateProbe.Text!)), $"{days}-day no-op cleanup preserves consecutive duplicate suppression");
    }
    using (var allOld = new HistoryStore(Path.Combine(directory, "all-old.db")))
    {
        var tied = DateTimeOffset.UtcNow.AddDays(-90);
        var older = ClipboardItem.FromText("oldest") with { Created = tied.AddDays(-1) };
        var firstTie = ClipboardItem.FromText("same time first") with { Created = tied };
        var latest = ClipboardItem.FromText("same time latest") with { Created = tied };
        foreach (var value in new[] { older, firstTie, latest }) await allOld.Capture(value);
        Check(await allOld.ClearOlderThan(DateTimeOffset.UtcNow.AddDays(-7)) == 2 && (await allOld.Query(new())).Single().Id == latest.Id, "Age cleanup preserves newest entry even when every copy is old, including timestamp ties");
    }

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

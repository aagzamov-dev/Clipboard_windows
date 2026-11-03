using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clipboard.Core;

public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? lastHash;
    private volatile bool disposed;
    public string Path { get; }

    public HistoryStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(version.ExecuteScalar()) > 1) { connection.Dispose(); throw new InvalidDataException("History was created by a newer Clipboard version. It was preserved."); }
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
            PRAGMA secure_delete=ON;
            CREATE TABLE IF NOT EXISTS entries (
                id TEXT NOT NULL UNIQUE, created INTEGER NOT NULL, kind INTEGER NOT NULL,
                preview TEXT NOT NULL, source TEXT NOT NULL, pinned INTEGER NOT NULL,
                bytes INTEGER NOT NULL, hash TEXT NOT NULL, search_text TEXT NOT NULL,
                payload BLOB NOT NULL, deleted TEXT, deleted_at INTEGER);
            CREATE INDEX IF NOT EXISTS entries_date ON entries(created DESC);
            CREATE INDEX IF NOT EXISTS entries_active ON entries(deleted, pinned, created DESC);
            CREATE VIRTUAL TABLE IF NOT EXISTS search USING fts5(search_text, content='entries', content_rowid='rowid', tokenize='unicode61');
            CREATE TRIGGER IF NOT EXISTS entries_insert AFTER INSERT ON entries BEGIN
                INSERT INTO search(rowid, search_text) VALUES(new.rowid, new.search_text); END;
            CREATE TRIGGER IF NOT EXISTS entries_delete AFTER DELETE ON entries BEGIN
                INSERT INTO search(search,rowid,search_text) VALUES('delete',old.rowid,old.search_text); END;
            CREATE TRIGGER IF NOT EXISTS entries_update AFTER UPDATE OF search_text ON entries BEGIN
                INSERT INTO search(search,rowid,search_text) VALUES('delete',old.rowid,old.search_text);
                INSERT INTO search(rowid, search_text) VALUES(new.rowid,new.search_text); END;
            PRAGMA user_version=1;
            """);
    }

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private async Task<T> Run<T>(Func<T> work)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync().ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await Task.Run(work).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public Task<bool> Capture(ClipboardItem item) => Run(() =>
    {
        item.Validate(); string hash = item.Hash();
        if (hash == lastHash) return false;
        Insert(item, hash); lastHash = hash; return true;
    });

    public Task ResetDeduplication() => Run(() => { lastHash = null; return true; });

    private void Insert(ClipboardItem item, string hash, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO entries(id,created,kind,preview,source,pinned,bytes,hash,search_text,payload)
            VALUES($id,$date,$kind,$preview,$source,$pinned,$bytes,$hash,$text,$payload)
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString()); command.Parameters.AddWithValue("$date", item.Created.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$kind", (int)item.Kind); command.Parameters.AddWithValue("$preview", string.Concat(item.Preview.Take(300)));
        command.Parameters.AddWithValue("$source", item.Source); command.Parameters.AddWithValue("$pinned", item.Pinned);
        command.Parameters.AddWithValue("$bytes", item.ByteCount); command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$text", item.Text ?? string.Join("\n", item.Files ?? []));
        command.Parameters.AddWithValue("$payload", JsonSerializer.SerializeToUtf8Bytes(item)); command.ExecuteNonQuery();
    }

    public Task<long> Cutoff() => Run(() =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT coalesce(max(rowid),0) FROM entries";
        return Convert.ToInt64(command.ExecuteScalar());
    });

    public Task<IReadOnlyList<ItemSummary>> Query(HistoryQuery query, int offset = 0) => Run<IReadOnlyList<ItemSummary>>(() =>
    {
        if (offset < 0 || query.Search.Length > 1024) throw new InvalidDataException("Invalid history query.");
        using var command = connection.CreateCommand();
        var terms = Regex.Matches(query.Search, @"[\p{L}\p{N}_]+").Select(m => $"\"{m.Value}\"*").Take(32).ToArray();
        string search = terms.Length != 0 ? "AND rowid IN (SELECT rowid FROM search WHERE search MATCH $search)" :
            query.Search.Length != 0 ? "AND instr(search_text,$search)>0" : "";
        command.CommandText = $"""
            SELECT id,kind,preview,created,source,pinned,bytes FROM entries
            WHERE deleted IS NULL AND rowid <= $cutoff AND ($kind IS NULL OR kind=$kind)
            AND ($pinned=0 OR pinned=1) AND ($from IS NULL OR created >= $from) AND ($to IS NULL OR created < $to)
            {search} ORDER BY {(query.PinnedFirst ? "pinned DESC," : "")} created DESC,rowid DESC LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$search", terms.Length != 0 ? string.Join(" AND ", terms) : query.Search);
        command.Parameters.AddWithValue("$cutoff", query.Cutoff); command.Parameters.AddWithValue("$kind", query.Kind is null ? DBNull.Value : (int)query.Kind);
        command.Parameters.AddWithValue("$pinned", query.Pinned);
        command.Parameters.AddWithValue("$from", (object?)query.From?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)query.To?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Limits.PageSize); command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader(); var result = new List<ItemSummary>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), (ItemKind)reader.GetInt32(1), reader.GetString(2),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetString(4), reader.GetBoolean(5), reader.GetInt64(6)));
        return result;
    });

    private ClipboardItem Read(Guid id, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT payload,pinned FROM entries WHERE id=$id AND deleted IS NULL";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("This history item is no longer available.");
        var item = JsonSerializer.Deserialize<ClipboardItem>((byte[])reader[0]) ?? throw new InvalidDataException("Invalid history item.");
        item = item with { Pinned = reader.GetBoolean(1) }; item.Validate(); return item;
    }

    public Task<ClipboardItem> Get(Guid id) => Run(() => Read(id));

    public Task SaveText(Guid id, string text) => Run(() =>
    {
        var old = Read(id); if (!old.IsText) throw new InvalidDataException("Only text can be edited.");
        var item = old with { Text = text, Rtf = null, Kind = ClipboardItem.Classify(text) }; item.Validate();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE entries SET payload=$payload,hash=$hash,search_text=$text,preview=$preview,kind=$kind,bytes=$bytes WHERE id=$id";
        command.Parameters.AddWithValue("$payload", JsonSerializer.SerializeToUtf8Bytes(item)); command.Parameters.AddWithValue("$hash", item.Hash());
        command.Parameters.AddWithValue("$text", text); command.Parameters.AddWithValue("$preview", string.Concat(item.Preview.Take(300)));
        command.Parameters.AddWithValue("$kind", (int)item.Kind); command.Parameters.AddWithValue("$bytes", item.ByteCount);
        command.Parameters.AddWithValue("$id", id.ToString()); command.ExecuteNonQuery(); lastHash = null; return true;
    });

    public Task Pin(Guid id, bool pinned) => Run(() =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "UPDATE entries SET pinned=$pinned WHERE id=$id AND deleted IS NULL";
        command.Parameters.AddWithValue("$pinned", pinned); command.Parameters.AddWithValue("$id", id.ToString()); command.ExecuteNonQuery(); return true;
    });

    public Task<Guid> Delete(IReadOnlyList<Guid> ids) => Run(() =>
    {
        var token = Guid.NewGuid(); using var transaction = connection.BeginTransaction();
        foreach (var id in ids.Distinct())
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE entries SET deleted=$token,deleted_at=$now WHERE id=$id AND deleted IS NULL";
            command.Parameters.AddWithValue("$token", token.ToString()); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$id", id.ToString()); command.ExecuteNonQuery();
        }
        transaction.Commit(); lastHash = null; return token;
    });

    public Task Undo(Guid token) => Run(() =>
    {
        using var command = connection.CreateCommand(); command.CommandText = "UPDATE entries SET deleted=NULL,deleted_at=NULL WHERE deleted=$token";
        command.Parameters.AddWithValue("$token", token.ToString()); command.ExecuteNonQuery(); return true;
    });

    public Task Cleanup(AppSettings settings, IReadOnlyCollection<Guid>? protectedIds = null) => Run(() =>
    {
        var protectedList = protectedIds?.Distinct().ToArray() ?? [];
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        string protect = protectedList.Length == 0 ? "" : $"AND id NOT IN ({string.Join(',', protectedList.Select((_, i) => "$id" + i))})";
        for (int i = 0; i < protectedList.Length; i++) command.Parameters.AddWithValue("$id" + i, protectedList[i].ToString());
        command.CommandText = $"""
            DELETE FROM entries WHERE deleted_at < $grace {protect}
            """;
        command.Parameters.AddWithValue("$grace", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
        command.ExecuteNonQuery(); transaction.Commit(); return true;
    });

    public Task<int> ClearOlderThan(DateTimeOffset cutoff, IReadOnlyCollection<Guid>? protectedIds = null) => Run(() =>
    {
        var protectedList = protectedIds?.Distinct().ToArray() ?? [];
        using var command = connection.CreateCommand();
        string protect = protectedList.Length == 0 ? "" : $"AND id NOT IN ({string.Join(',', protectedList.Select((_, i) => "$id" + i))})";
        command.CommandText = $"""
            DELETE FROM entries WHERE pinned=0 AND deleted IS NULL AND created < $cutoff
                AND rowid NOT IN (SELECT rowid FROM entries WHERE deleted IS NULL ORDER BY created DESC,rowid DESC LIMIT 1)
                {protect}
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
        for (int i = 0; i < protectedList.Length; i++) command.Parameters.AddWithValue("$id" + i, protectedList[i].ToString());
        int count = command.ExecuteNonQuery(); if (count > 0) lastHash = null; return count;
    });

    public Task Clear(bool includePinned) => Run(() =>
    {
        Execute(includePinned ? "DELETE FROM entries" : "DELETE FROM entries WHERE pinned=0"); lastHash = null;
        Execute("PRAGMA wal_checkpoint(TRUNCATE)"); return true;
    });

    internal async Task Snapshot(string destination)
    {
        await Run(() =>
        {
            using var count = connection.CreateCommand(); count.CommandText = "SELECT count(*) FROM entries WHERE deleted IS NULL";
            if (Convert.ToInt64(count.ExecuteScalar()) > 200_000) throw new InvalidDataException("Backup exceeds the 200,000-entry limit.");
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
            target.Open(); connection.BackupDatabase(target); return true;
        }).ConfigureAwait(false);
        await Task.Run(() =>
        {
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
            target.Open(); using var scrub = target.CreateCommand();
            scrub.CommandText = "PRAGMA secure_delete=ON; DELETE FROM entries WHERE deleted IS NOT NULL; INSERT INTO search(search) VALUES('rebuild'); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            scrub.ExecuteNonQuery();
        }).ConfigureAwait(false);
    }

    private static SqliteConnection OpenBackup(string source)
    {
        var input = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        input.Open();
        try
        {
            using var check = input.CreateCommand();
            check.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA query_only=ON;"; check.ExecuteNonQuery();
            check.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name='entries' AND type='table' AND rootpage>0 AND sql NOT LIKE '%VIRTUAL%'";
            if (Convert.ToInt32(check.ExecuteScalar()) != 1) throw new InvalidDataException("Backup entries must be an ordinary table.");
            check.CommandText = "PRAGMA table_info(entries)";
            using (var columns = check.ExecuteReader())
            {
                var schema = new Dictionary<string, string>();
                while (columns.Read()) schema[columns.GetString(1)] = columns.GetString(2).ToUpperInvariant();
                string[] textColumns = ["id", "preview", "source", "hash", "search_text", "deleted"];
                string[] intColumns = ["created", "kind", "pinned", "bytes", "deleted_at"];
                if (schema.Count != 12 || textColumns.Any(name => !schema.TryGetValue(name, out var type) || type != "TEXT") ||
                    intColumns.Any(name => !schema.TryGetValue(name, out var type) || type != "INTEGER") || schema.GetValueOrDefault("payload") != "BLOB")
                    throw new InvalidDataException("Unexpected backup schema.");
            }
            check.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(check.ExecuteScalar()) != 1) throw new InvalidDataException("Unsupported backup version.");
            check.CommandText = "PRAGMA quick_check";
            if (!Equals(check.ExecuteScalar(), "ok")) throw new InvalidDataException("The backup database is damaged.");
            check.CommandText = "SELECT count(*) FROM entries";
            if (Convert.ToInt64(check.ExecuteScalar()) > 200_000) throw new InvalidDataException("Backup has too many entries.");
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    internal async Task<int> Import(string source)
    {
        using var input = await Task.Run(() => OpenBackup(source)).ConfigureAwait(false);
        return await Run(() =>
        {
            using var command = input.CreateCommand(); command.CommandText = "SELECT id,hash,payload,pinned,created,source,kind,bytes FROM entries WHERE deleted IS NULL";
            using var reader = command.ExecuteReader(); using var transaction = connection.BeginTransaction(); int count = 0;
            while (reader.Read())
            {
                if (reader.GetBytes(2, 0, null, 0, 0) > 40 * 1024 * 1024) throw new InvalidDataException("Oversized backup item.");
                var item = JsonSerializer.Deserialize<ClipboardItem>((byte[])reader[2]) ?? throw new InvalidDataException("Invalid backup item.");
                item = item with { Pinned = reader.GetBoolean(3) }; item.Validate();
                string hash = item.Hash();
                if (item.Id.ToString() != reader.GetString(0) || hash != reader.GetString(1) || item.Created.ToUnixTimeMilliseconds() != reader.GetInt64(4) ||
                    item.Source != reader.GetString(5) || (int)item.Kind != reader.GetInt32(6) || item.ByteCount != reader.GetInt64(7))
                    throw new InvalidDataException("Backup content checksum mismatch.");
                using var existing = connection.CreateCommand(); existing.Transaction = transaction;
                existing.CommandText = "SELECT hash,created,source FROM entries WHERE id=$id"; existing.Parameters.AddWithValue("$id", item.Id.ToString());
                using var previous = existing.ExecuteReader();
                if (previous.Read())
                {
                    if (previous.GetString(0) != hash || previous.GetInt64(1) != item.Created.ToUnixTimeMilliseconds() || previous.GetString(2) != item.Source)
                        throw new InvalidDataException("A backup item conflicts with existing history. Nothing was imported.");
                    continue;
                }
                previous.Close();
                Insert(item, hash, transaction); count++;
            }
            transaction.Commit(); lastHash = null; return count;
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        gate.Wait();
        try { if (disposed) return; disposed = true; connection.Dispose(); }
        finally { gate.Release(); }
    }
}

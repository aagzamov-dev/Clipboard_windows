using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Clipboard.Core;

public static class BackupService
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CLIPBK01");
    private const int ChunkSize = 64 * 1024;
    private const long MaxArchive = 2L * 1024 * 1024 * 1024;

    public static async Task Export(HistoryStore store, string destination, string password)
    {
        string fullDestination = System.IO.Path.GetFullPath(destination);
        if (new[] { store.Path, store.Path + "-wal", store.Path + "-shm" }.Any(path => string.Equals(path, fullDestination, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Choose a backup file outside the live history database files.");
        if (password.Length is > 0 and < 8) throw new InvalidDataException("Use a backup password with at least 8 characters.");
        string temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(destination))!, $".clipboard-{Guid.NewGuid():N}.tmp");
        string work = Directory.CreateTempSubdirectory("clipboard-backup-").FullName;
        try
        {
            string database = System.IO.Path.Combine(work, "history.db");
            await store.Snapshot(database).ConfigureAwait(false);
            await Task.Run(() =>
            {
                string zip = System.IO.Path.Combine(work, "backup.zip");
                if (new FileInfo(database).Length > MaxArchive) throw new InvalidDataException("History exceeds the 2 GB backup limit.");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                    archive.CreateEntryFromFile(database, "history.db", CompressionLevel.Fastest);
                if (new FileInfo(zip).Length > MaxArchive) throw new InvalidDataException("Backup exceeds the 2 GB archive limit.");
                if (password.Length == 0) File.Copy(zip, temporary);
                else Encrypt(zip, temporary, password);
                File.Move(temporary, destination, overwrite: true);
            }).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            Directory.Delete(work, recursive: true);
        }
    }

    public static async Task<int> Import(HistoryStore store, string source, string password)
    {
        string work = Directory.CreateTempSubdirectory("clipboard-restore-").FullName;
        try
        {
            string database = await Task.Run(() =>
            {
                if (new FileInfo(source).Length > MaxArchive + 1024 * 1024) throw new InvalidDataException("Backup exceeds the archive limit.");
                string zip = System.IO.Path.Combine(work, "backup.zip");
                using (var input = File.OpenRead(source))
                {
                    var header = new byte[8]; input.ReadExactly(header);
                    if (header.SequenceEqual(Magic)) Decrypt(input, zip, password, header);
                    else File.Copy(source, zip);
                }
                using var archive = ZipFile.OpenRead(zip);
                if (archive.Entries.Count != 1 || archive.Entries[0].FullName != "history.db" ||
                    archive.Entries[0].Length > MaxArchive || archive.Entries[0].Length < 100)
                    throw new InvalidDataException("Invalid backup contents.");
                string target = System.IO.Path.Combine(work, "history.db");
                using var output = File.Create(target);
                using var content = archive.Entries[0].Open();
                var buffer = new byte[ChunkSize]; long total = 0; int count;
                while ((count = content.Read(buffer)) != 0)
                {
                    total += count;
                    if (total > MaxArchive || total > archive.Entries[0].Length) throw new InvalidDataException("Oversized backup contents.");
                    output.Write(buffer, 0, count);
                }
                return target;
            }).ConfigureAwait(false);
            return await store.Import(database).ConfigureAwait(false);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 310_000, HashAlgorithmName.SHA256, 32);

    private static void Encrypt(string source, string destination, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16), prefix = RandomNumberGenerator.GetBytes(8);
        byte[] header = [.. Magic, .. salt, .. prefix], key = Derive(password, salt);
        try
        {
            using var aes = new AesGcm(key, 16); using var input = File.OpenRead(source); using var output = File.Create(destination);
            output.Write(header); byte[] plain = new byte[ChunkSize], cipher = new byte[ChunkSize], tag = new byte[16];
            uint sequence = 0;
            while (true)
            {
                int count = input.ReadAtLeast(plain, ChunkSize, throwOnEndOfStream: false);
                byte[] nonce = Nonce(prefix, sequence++), aad = Associated(header, count, sequence);
                aes.Encrypt(nonce, plain.AsSpan(0, count), cipher.AsSpan(0, count), tag, aad);
                output.Write(aad.AsSpan(header.Length, 4)); output.Write(cipher, 0, count); output.Write(tag);
                // An authenticated empty final record prevents truncation at a chunk boundary.
                if (count == 0) break;
            }
            output.Flush(flushToDisk: true);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void Decrypt(Stream input, string destination, string password, byte[] magic)
    {
        byte[] salt = new byte[16], prefix = new byte[8]; input.ReadExactly(salt); input.ReadExactly(prefix);
        byte[] header = [.. magic, .. salt, .. prefix], key = Derive(password, salt);
        try
        {
            using var aes = new AesGcm(key, 16); using var output = File.Create(destination);
            byte[] cipher = new byte[ChunkSize], plain = new byte[ChunkSize], tag = new byte[16], length = new byte[4];
            uint sequence = 0; long total = 0;
            while (true)
            {
                input.ReadExactly(length); int count = BinaryPrimitives.ReadInt32LittleEndian(length);
                if (count < 0 || count > ChunkSize || (total += count) > MaxArchive) throw new InvalidDataException("Invalid encrypted backup length.");
                input.ReadExactly(cipher.AsSpan(0, count)); input.ReadExactly(tag);
                aes.Decrypt(Nonce(prefix, sequence++), cipher.AsSpan(0, count), tag, plain.AsSpan(0, count), Associated(header, count, sequence));
                if (count == 0)
                {
                    if (input.ReadByte() != -1) throw new InvalidDataException("Unexpected backup data.");
                    break;
                }
                output.Write(plain, 0, count);
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] Nonce(byte[] prefix, uint sequence)
    {
        byte[] nonce = new byte[12]; prefix.CopyTo(nonce, 0); BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), sequence); return nonce;
    }

    private static byte[] Associated(byte[] header, int count, uint sequence)
    {
        byte[] aad = new byte[header.Length + 8]; header.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(header.Length), count);
        BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(header.Length + 4), sequence); return aad;
    }
}

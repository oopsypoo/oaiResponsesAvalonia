using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using oaiResponsesAvalonia.Data;
using oaiResponsesAvalonia.Services;

namespace oaiProtonSync.Services;

public sealed record InitialBaselineResult(string Directory, int Sessions, int Messages, int MediaReferences);

/// <summary>
/// Experimental one-time union of independently created histories. This is NOT a sync engine.
/// It only writes inside a new disposable workspace and does not alter either input archive.
/// </summary>
public sealed class InitialBaselineService
{
    private readonly string _root;

    public InitialBaselineService() : this(Path.Combine(AppPaths.DataDirectory, "ProtonSync", "BaselineTests")) { }
    internal InitialBaselineService(string root) => _root = root;

    public async Task<InitialBaselineResult> CreateAsync(string firstArchive, string secondArchive,
        Action<string> progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!File.Exists(firstArchive) || !File.Exists(secondArchive))
            throw new FileNotFoundException("Both snapshot archives must exist.");
        string workspace = Path.Combine(_root, $"baseline_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        bool success = false;
        try
        {
            // Compare and restore the same private copies, not files that could change between checks.
            string first = Path.Combine(workspace, "first.zip"), second = Path.Combine(workspace, "second.zip");
            await CopyAsync(firstArchive, first, cancellationToken);
            await CopyAsync(secondArchive, second, cancellationToken);
            progress("Verifying and comparing the two private snapshot copies…");
            var comparison = await new SnapshotComparisonService().CompareAsync(first, second, progress, cancellationToken);
            if (string.Equals(comparison.FirstSnapshotId, comparison.SecondSnapshotId, StringComparison.Ordinal))
                throw new InvalidDataException("Both snapshots have the same ID; an initial union requires independent histories.");
            if (comparison.Candidates.Count != 0)
                throw new InvalidDataException("Possible shared sessions were found. The initial union cannot safely treat these histories as independent.");
            progress("No candidate matches were found. This does not prove the histories are independent.");

            var restore = new IsolatedSnapshotRestoreService(workspace);
            var a = await restore.RestoreAsync(first, progress, cancellationToken);
            var b = await restore.RestoreAsync(second, progress, cancellationToken);
            var check = new RestoredAppCompatibilityService();
            var countA = await check.CheckAsync(a, cancellationToken);
            var countB = await check.CheckAsync(b, cancellationToken);
            await RejectExistingBaselineAsync(a.DatabasePath, cancellationToken);
            await RejectExistingBaselineAsync(b.DatabasePath, cancellationToken);
            await CheckKnownSchemaAsync(a.DatabasePath, cancellationToken);
            await CheckKnownSchemaAsync(b.DatabasePath, cancellationToken);
            progress("Copying the second history and its media into the first disposable copy…");
            await CopyMediaAsync(b, a, cancellationToken);
            await CombineDatabasesAsync(a.DatabasePath, b.DatabasePath, cancellationToken);
            var result = await check.CheckAsync(a, cancellationToken);
            if (result.Sessions != countA.Sessions + countB.Sessions ||
                result.Messages != countA.Messages + countB.Messages ||
                result.MediaReferences != countA.MediaReferences + countB.MediaReferences)
                throw new InvalidDataException("Combined counts do not match the input histories.");
            Directory.Delete(b.RestoreDirectory, recursive: true);
            File.Delete(first);
            File.Delete(second);
            // The source manifest describes the original snapshot, not the new combined database.
            File.Delete(Path.Combine(a.RestoreDirectory, "manifest.json"));
            await File.WriteAllTextAsync(Path.Combine(a.RestoreDirectory, "experimental-baseline.json"),
                "{\"kind\":\"disposable-initial-baseline\",\"version\":1}\n", cancellationToken);
            progress("Disposable baseline created and checked. It is not a verified snapshot or a sync-enabled database.");
            success = true;
            return new InitialBaselineResult(a.RestoreDirectory, result.Sessions, result.Messages, result.MediaReferences);
        }
        finally
        {
            if (!success)
            {
                try { Directory.Delete(workspace, recursive: true); }
                catch (Exception ex) { progress($"Could not remove incomplete baseline workspace {workspace}: {ex.Message}"); }
            }
        }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, token);
    }

    private static DbContextOptions<AppDbContext> Options(string path, SqliteOpenMode mode) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = mode, Pooling = false }.ToString()).Options;

    private static async Task RejectExistingBaselineAsync(string path, CancellationToken token)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('ExperimentalSessionIdentity', 'ExperimentalMessageIdentity');";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 0)
            throw new InvalidDataException("An input already contains experimental identities. Repeated imports require a future sync-aware merge.");
    }

    private static async Task CheckKnownSchemaAsync(string path, CancellationToken token)
    {
        await using var db = new AppDbContext(Options(path, SqliteOpenMode.ReadOnly));
        await db.Database.OpenConnectionAsync(token);
        var expectedTables = new HashSet<string>(new[] { "Sessions", "Messages", "Media" }, StringComparer.OrdinalIgnoreCase);
        await using (var tables = db.Database.GetDbConnection().CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            await using var reader = await tables.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                if (!expectedTables.Contains(reader.GetString(0)))
                    throw new InvalidDataException($"Unsupported database table: {reader.GetString(0)}. Initial union cannot safely copy unknown data.");
        }
        foreach (var entity in db.Model.GetEntityTypes())
        {
            string? table = entity.GetTableName();
            if (table is null || !expectedTables.Contains(table)) continue;
            var columns = entity.GetProperties().Select(p => p.GetColumnName()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                string name = reader.GetString(1);
                if (!columns.Remove(name))
                    throw new InvalidDataException($"Unsupported column {table}.{name}. Initial union cannot safely copy unknown data.");
            }
            if (columns.Count != 0)
                throw new InvalidDataException($"The {table} schema is missing expected columns. Update the app before combining snapshots.");
        }
    }

    private static async Task CopyMediaAsync(IsolatedSnapshotRestoreResult from, IsolatedSnapshotRestoreResult to, CancellationToken token)
    {
        // The app resolves by basename across both folders. A collision in either folder must
        // have identical bytes, otherwise an existing reference could resolve to the wrong file.
        foreach (string sourceFolder in new[] { from.ImagesDirectory, from.FilesDirectory })
        foreach (string source in Directory.EnumerateFiles(sourceFolder))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(source);
            var matches = new[] { to.ImagesDirectory, to.FilesDirectory }
                .SelectMany(Directory.EnumerateFiles)
                .Where(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 0)
            {
                await using var input = File.OpenRead(source);
                byte[] expected = await SHA256.HashDataAsync(input, token);
                foreach (string match in matches)
                {
                    if (!string.Equals(Path.GetFileName(match), name, StringComparison.Ordinal))
                        throw new InvalidDataException($"Media filenames differ only by letter case: {name}. Cannot combine portably.");
                    await using var existing = File.OpenRead(match);
                    byte[] actual = await SHA256.HashDataAsync(existing, token);
                    if (!expected.SequenceEqual(actual))
                        throw new InvalidDataException($"Media filename collision with different content: {name}");
                }
            }
            else
            {
                string targetFolder = sourceFolder == from.ImagesDirectory ? to.ImagesDirectory : to.FilesDirectory;
                await CopyAsync(source, Path.Combine(targetFolder, name), token);
            }
        }
    }

    private static async Task CombineDatabasesAsync(string targetPath, string sourcePath, CancellationToken token)
    {
        await using var source = new AppDbContext(Options(sourcePath, SqliteOpenMode.ReadOnly));
        var sessions = await source.Sessions.AsNoTracking().Include(s => s.Messages).ThenInclude(m => m.MediaFiles)
            .AsSplitQuery().ToListAsync(token);
        await using var target = new AppDbContext(Options(targetPath, SqliteOpenMode.ReadWrite));
        await target.Database.OpenConnectionAsync(token);
        await target.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;", token);
        await using var transaction = await target.Database.BeginTransactionAsync(token);
        foreach (var oldSession in sessions)
        {
            token.ThrowIfCancellationRequested();
            var session = new ChatSession();
            target.Entry(session).CurrentValues.SetValues(oldSession);
            session.Id = 0;
            foreach (var oldMessage in oldSession.Messages.OrderBy(m => m.Id))
            {
                var message = new ChatMessage();
                target.Entry(message).CurrentValues.SetValues(oldMessage);
                message.Id = 0;
                message.ChatSessionId = 0;
                foreach (var oldMedia in oldMessage.MediaFiles.OrderBy(m => m.Id))
                {
                    var media = new MediaFile();
                    target.Entry(media).CurrentValues.SetValues(oldMedia);
                    media.Id = 0;
                    media.ChatMessageId = 0;
                    message.MediaFiles.Add(media);
                }
                session.Messages.Add(message);
            }
            target.Sessions.Add(session);
        }
        await target.SaveChangesAsync(token);
        // Experimental identity mappings travel with this database, but the application does not
        // yet maintain them on edits/deletes. They must never be used for live synchronization.
        await target.Database.ExecuteSqlRawAsync("CREATE TABLE ExperimentalSessionIdentity (SessionId INTEGER PRIMARY KEY REFERENCES Sessions(Id), SyncId TEXT NOT NULL UNIQUE);", token);
        await target.Database.ExecuteSqlRawAsync("CREATE TABLE ExperimentalMessageIdentity (MessageId INTEGER PRIMARY KEY REFERENCES Messages(Id), SyncId TEXT NOT NULL UNIQUE);", token);
        var sessionIds = await target.Sessions.AsNoTracking().Select(s => s.Id).ToListAsync(token);
        var messageIds = await target.Messages.AsNoTracking().Select(m => m.Id).ToListAsync(token);
        foreach (int id in sessionIds)
            await target.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ExperimentalSessionIdentity (SessionId, SyncId) VALUES ({id}, {Guid.NewGuid().ToString("D")});", token);
        foreach (int id in messageIds)
            await target.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ExperimentalMessageIdentity (MessageId, SyncId) VALUES ({id}, {Guid.NewGuid().ToString("D")});", token);
        await transaction.CommitAsync(token);
        await using var command = target.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var integrity = (await command.ExecuteScalarAsync(token))?.ToString();
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Combined database integrity check failed: {integrity}");
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token)) throw new InvalidDataException("Combined database has invalid foreign key references.");
    }
}

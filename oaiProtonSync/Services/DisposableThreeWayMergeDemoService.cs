using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using oaiResponsesAvalonia.Data;
using oaiResponsesAvalonia.Services;

namespace oaiProtonSync.Services;

public sealed record DisposableMergeDemoResult(string ResultDirectory, string Workspace, int Added, int Edited, int Conflicts, int RepeatedChanges, int Sessions, int Messages, int Deletions, int DeleteConflicts, int RepeatedDeletions);

/// <summary>
/// A controlled rehearsal, not a general database merge. Only the synthetic messages
/// created by this service are eligible for merging; no existing record is modified.
/// </summary>
public sealed class DisposableThreeWayMergeDemoService
{
    private readonly string _root;
    public DisposableThreeWayMergeDemoService() : this(Path.Combine(AppPaths.DataDirectory, "ProtonSync", "BaselineTests")) { }
    public DisposableThreeWayMergeDemoService(string root) => _root = root;

    public async Task<DisposableMergeDemoResult> RunAsync(string sourceFolder, Action<string> report, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder)) throw new InvalidDataException("Choose a disposable baseline folder first.");
        string source = Path.GetFullPath(sourceFolder);
        string marker = Path.Combine(source, "experimental-baseline.json");
        string sourceDb = Path.Combine(source, "localhistory.db");
        if (!File.Exists(marker) || !File.Exists(sourceDb)) throw new InvalidDataException("The baseline must contain its database and experimental-baseline.json.");
        EnsureNoLinks(source);
        EnsureNoLinks(marker);
        EnsureNoLinks(sourceDb);
        foreach (var folder in new[] { "images", "files" })
            if (!Directory.Exists(Path.Combine(source, folder))) throw new InvalidDataException($"The baseline is missing its {folder} folder.");

        Directory.CreateDirectory(_root);
        EnsureNoLinks(_root);
        string workspace = Path.Combine(_root, $"baseline_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            string baseDir = Directory.CreateDirectory(Path.Combine(workspace, "shared-base")).FullName;
            string aDir = Directory.CreateDirectory(Path.Combine(workspace, "branch-A")).FullName;
            string bDir = Directory.CreateDirectory(Path.Combine(workspace, "branch-B")).FullName;
            string resultDir = Directory.CreateDirectory(Path.Combine(workspace, $"restore_{Guid.NewGuid():N}")).FullName;
            File.Copy(marker, Path.Combine(baseDir, "experimental-baseline.json"));
            File.Copy(marker, Path.Combine(aDir, "experimental-baseline.json"));
            File.Copy(marker, Path.Combine(bDir, "experimental-baseline.json"));
            File.Copy(marker, Path.Combine(resultDir, "experimental-baseline.json"));
            string baseDb = Path.Combine(baseDir, "localhistory.db"), aDb = Path.Combine(aDir, "localhistory.db"),
                bDb = Path.Combine(bDir, "localhistory.db"), resultDb = Path.Combine(resultDir, "localhistory.db");
            report("Copying the selected baseline and its media into a new disposable workspace…");
            CopyDatabase(sourceDb, baseDb);
            foreach (string folder in new[] { "images", "files" }) CopyDirectory(Path.Combine(source, folder), Path.Combine(resultDir, folder), token);
            // Validate all pre-existing identities before creating the simulated changes.
            await new BaselineIdentityComparisonService().CompareAsync(baseDir, baseDir, token);
            var setup = await CreateScenarioAsync(baseDb, token);
            var deletion = await DisposableDeletionRehearsal.SetupAsync(baseDb, token);
            CopyDatabase(baseDb, aDb);
            CopyDatabase(baseDb, bDb);
            CopyDatabase(baseDb, resultDb);
            report("Simulating independent additions, a one-sided edit, and conflicting edits to synthetic conversations.");
            await ChangeBranchAsync(aDb, setup, true, token);
            await ChangeBranchAsync(bDb, setup, false, token);
            report("Simulating a message deletion, a session deletion, and a delete-versus-edit conflict on synthetic records…");
            await DisposableDeletionRehearsal.ChangeBranchesAsync(aDb, bDb, deletion, token);
            var deleted = await DisposableDeletionRehearsal.ApplyAsync(baseDb, aDb, bDb, resultDb, deletion, token);
            if (deleted != (2, 1)) throw new InvalidDataException("Unexpected deletion rehearsal result.");
            var merge = await ApplyScenarioAsync(baseDb, aDb, bDb, resultDb, setup, token);
            if (merge != (2, 1, 1)) throw new InvalidDataException("Unexpected rehearsal result; the disposable result was not retained.");
            report("Reapplying the same simulated changes to check for duplicates…");
            var repeated = await ApplyScenarioAsync(baseDb, aDb, bDb, resultDb, setup, token);
            var repeatedDeletion = await DisposableDeletionRehearsal.ApplyAsync(baseDb, aDb, bDb, resultDb, deletion, token);
            if (repeatedDeletion != (0, 1)) throw new InvalidDataException("Repeated deletion was not idempotent.");
            if (repeated != (0, 0, 1)) throw new InvalidDataException("Repeated application was not idempotent; the disposable result was not retained.");
            token.ThrowIfCancellationRequested();
            await using (var connection = new SqliteConnection(Connection(resultDb, true)))
            {
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(Convert.ToString(await command.ExecuteScalarAsync(token)), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SQLite integrity check failed.");
                command.CommandText = "PRAGMA foreign_key_check";
                await using var reader = await command.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token)) throw new InvalidDataException("SQLite foreign-key check failed.");
            }
            var check = await new RestoredAppCompatibilityService().CheckAsync(new IsolatedSnapshotRestoreResult(resultDir, resultDb,
                Path.Combine(resultDir, "images"), Path.Combine(resultDir, "files"), "experimental", 0, "ok"), token);
            await new BaselineIdentityComparisonService().CompareAsync(resultDir, resultDir, token);
            return new DisposableMergeDemoResult(resultDir, workspace, merge.Item1, merge.Item2, merge.Item3,
                repeated.Item1 + repeated.Item2, check.Sessions, check.Messages, deleted.Item1, deleted.Item2, repeatedDeletion.Item1);
        }
        catch
        {
            try { Directory.Delete(workspace, true); } catch { report($"Could not remove incomplete rehearsal workspace: {workspace}"); }
            throw;
        }
    }

    private sealed record Scenario(int SessionA, int SessionB, Guid EditId, Guid ConflictId);
    private static async Task<Scenario> CreateScenarioAsync(string path, CancellationToken token)
    {
        await using var db = Context(path);
        ChatSession Session(string title) => new(EndpointType.Responses, "Responses", "[MERGE REHEARSAL] " + title);
        var a = Session("Additions from A"); var b = Session("Additions from B");
        var edit = Session("One-sided edit"); var conflict = Session("Conflicting edit");
        var editMessage = new ChatMessage { Role = "user", Content = "Rehearsal: original one-sided edit" };
        var conflictMessage = new ChatMessage { Role = "user", Content = "Rehearsal: original conflicting edit" };
        edit.Messages.Add(editMessage); conflict.Messages.Add(conflictMessage);
        db.Sessions.AddRange(a, b, edit, conflict);
        await db.SaveChangesAsync(token);
        return new Scenario(a.Id, b.Id, await IdentityAsync(db, editMessage.Id, token), await IdentityAsync(db, conflictMessage.Id, token));
    }

    private static async Task ChangeBranchAsync(string path, Scenario scenario, bool a, CancellationToken token)
    {
        await using var db = Context(path);
        db.Messages.Add(new ChatMessage { ChatSessionId = a ? scenario.SessionA : scenario.SessionB, Role = "user", Content = a ? "Rehearsal: new message from A" : "Rehearsal: new message from B" });
        if (a) (await FindAsync(db, scenario.EditId, token))!.Content = "Rehearsal: edited only on A";
        (await FindAsync(db, scenario.ConflictId, token))!.Content = a ? "Rehearsal: conflicting edit on A" : "Rehearsal: conflicting edit on B";
        await db.SaveChangesAsync(token);
    }

    private static async Task<(int, int, int)> ApplyScenarioAsync(string baseline, string a, string b, string output, Scenario scenario, CancellationToken token)
    {
        await using var original = Context(baseline); await using var left = Context(a); await using var right = Context(b); await using var result = Context(output);
        await using var tx = await result.Database.BeginTransactionAsync(token);
        int additions = 0, edits = 0, conflicts = 0;
        // Only synthetic records are merged. Never infer deletions or merge arbitrary data.
        foreach (var (branch, session) in new[] { (left, scenario.SessionA), (right, scenario.SessionB) })
        {
            var newMessage = await branch.Messages.SingleAsync(m => m.ChatSessionId == session, token);
            var id = await IdentityAsync(branch, newMessage.Id, token);
            var existing = await FindAsync(result, id, token);
            if (existing == null)
            {
                var copy = new ChatMessage(); result.Entry(copy).CurrentValues.SetValues(newMessage);
                copy.Id = 0; result.Messages.Add(copy); await result.SaveChangesAsync(token);
                await result.Database.ExecuteSqlInterpolatedAsync($"UPDATE ExperimentalMessageIdentity SET SyncId = {id.ToString()} WHERE MessageId = {copy.Id}", token);
                additions++;
            }
            else if (existing.Content != newMessage.Content || existing.ChatSessionId != session)
                throw new InvalidDataException("A synthetic addition has an unexpected identity or content.");
        }
        foreach (var id in new[] { scenario.EditId, scenario.ConflictId })
        {
            var before = await FindAsync(original, id, token) ?? throw new InvalidDataException("Missing baseline message.");
            var leftMessage = await FindAsync(left, id, token) ?? throw new InvalidDataException("Missing branch A message.");
            var rightMessage = await FindAsync(right, id, token) ?? throw new InvalidDataException("Missing branch B message.");
            var target = await FindAsync(result, id, token) ?? throw new InvalidDataException("Missing result message.");
            string x = leftMessage.Content, y = rightMessage.Content, old = before.Content;
            if (x != old && y != old && x != y)
            {
                if (target.Content != old) throw new InvalidDataException("Conflicting result was unexpectedly modified.");
                conflicts++; // Preserve the baseline; both versions remain in branch-A and branch-B.
                continue;
            }
            string chosen = x != old ? x : y;
            if (target.Content == old && chosen != old) { target.Content = chosen; edits++; }
            else if (target.Content != chosen) throw new InvalidDataException("Result diverged from the expected simulated edit.");
        }
        await result.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        return (additions, edits, conflicts);
    }

    private static async Task<Guid> IdentityAsync(AppDbContext db, int id, CancellationToken token)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT SyncId FROM ExperimentalMessageIdentity WHERE MessageId = $id";
        command.Parameters.AddWithValue("$id", id);
        if (!Guid.TryParse(Convert.ToString(await command.ExecuteScalarAsync(token)), out var value) || value == Guid.Empty)
            throw new InvalidDataException("A synthetic message has no valid identity.");
        return value;
    }

    private static async Task<ChatMessage?> FindAsync(AppDbContext db, Guid id, CancellationToken token)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT MessageId FROM ExperimentalMessageIdentity WHERE lower(SyncId) = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        var key = await command.ExecuteScalarAsync(token);
        return key == null ? null : await db.Messages.FindAsync(new object[] { Convert.ToInt32(key) }, token);
    }

    private static string Connection(string path, bool readOnly = false) => new SqliteConnectionStringBuilder
        { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
    private static AppDbContext Context(string path) => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Connection(path)).Options);
    private static void CopyDatabase(string from, string to)
    {
        using var source = new SqliteConnection(Connection(from, true));
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = to, Pooling = false }.ToString());
        source.Open(); target.Open(); source.BackupDatabase(target);
    }
    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Symbolic links are not supported in the rehearsal.");
    }
    private static void CopyDirectory(string source, string destination, CancellationToken token)
    {
        EnsureNoLinks(source); Directory.CreateDirectory(destination);
        foreach (string item in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested(); EnsureNoLinks(item);
            string target = Path.Combine(destination, Path.GetFileName(item));
            if (Directory.Exists(item)) CopyDirectory(item, target, token); else File.Copy(item, target);
        }
    }
}

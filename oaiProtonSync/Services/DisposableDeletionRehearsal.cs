using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using oaiResponsesAvalonia.Data;
using oaiResponsesAvalonia.Services;

namespace oaiProtonSync.Services;

/// <summary>
/// Controlled deletion experiment on synthetic records in rehearsal database copies only.
/// Not a general sync implementation: timestamps, real data, arbitrary cascading deletes,
/// missing markers, and conflict resolution are deliberately outside its scope.
/// </summary>
internal static class DisposableDeletionRehearsal
{
    internal sealed record Scenario(int DeleteMessage, int ConflictMessage, int DeleteSession, int ChildMessage, int KeeperMessage,
        string DeleteMessageSyncId, string ConflictMessageSyncId, string DeleteSessionSyncId, string ChildMessageSyncId, string SharedMediaPath);

    private static AppDbContext Context(string path) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()).Options);

    internal static async Task<Scenario> SetupAsync(string path, CancellationToken token)
    {
        await using var db = Context(path);
        var messageSession = new ChatSession(EndpointType.Responses, "Responses", "[MERGE REHEARSAL] Message deletion");
        var conflictSession = new ChatSession(EndpointType.Responses, "Responses", "[MERGE REHEARSAL] Delete versus edit");
        var deletedSession = new ChatSession(EndpointType.Responses, "Responses", "[MERGE REHEARSAL] Session deletion");
        var keeperSession = new ChatSession(EndpointType.Responses, "Responses", "[MERGE REHEARSAL] Shared media retained");
        var message = new ChatMessage { Role = "user", Content = "Rehearsal: delete this message" };
        var conflict = new ChatMessage { Role = "user", Content = "Rehearsal: original delete-versus-edit message" };
        var child = new ChatMessage { Role = "user", Content = "Rehearsal: child of deleted session" };
        var keeper = new ChatMessage { Role = "user", Content = "Rehearsal: still references shared media" };
        string sharedPath = $"media/rehearsal_shared_{Guid.NewGuid():N}.txt";
        child.MediaFiles.Add(new MediaFile { LocalPath = sharedPath, MediaType = "text/plain" });
        keeper.MediaFiles.Add(new MediaFile { LocalPath = sharedPath, MediaType = "text/plain" });
        messageSession.Messages.Add(message);
        conflictSession.Messages.Add(conflict);
        deletedSession.Messages.Add(child);
        keeperSession.Messages.Add(keeper);
        db.Sessions.AddRange(messageSession, conflictSession, deletedSession, keeperSession);
        await db.SaveChangesAsync(token);
        await db.Database.OpenConnectionAsync(token);
        var c = (SqliteConnection)db.Database.GetDbConnection();
        string messageId = await IdentityAsync(c, "ExperimentalMessageIdentity", "MessageId", message.Id, token);
        string conflictId = await IdentityAsync(c, "ExperimentalMessageIdentity", "MessageId", conflict.Id, token);
        string sessionId = await IdentityAsync(c, "ExperimentalSessionIdentity", "SessionId", deletedSession.Id, token);
        string childId = await IdentityAsync(c, "ExperimentalMessageIdentity", "MessageId", child.Id, token);
        await using var tx = await c.BeginTransactionAsync(token);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        // This capability is restricted to explicitly registered synthetic IDs in these copies.
        cmd.CommandText = """
            CREATE TABLE ExperimentalRehearsalAllowedDeletion (Kind TEXT NOT NULL, LocalId INTEGER NOT NULL, PRIMARY KEY (Kind, LocalId));
            CREATE TABLE ExperimentalRehearsalTombstone (Kind TEXT NOT NULL, SyncId TEXT NOT NULL, PRIMARY KEY (Kind, SyncId));
            DROP TRIGGER ExperimentalMessageIdentity_BlockDelete;
            DROP TRIGGER ExperimentalSessionIdentity_BlockDelete;
            CREATE TRIGGER ExperimentalRehearsalMessageDelete BEFORE DELETE ON Messages BEGIN
                SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM ExperimentalRehearsalAllowedDeletion WHERE Kind='message' AND LocalId=OLD.Id)
                    THEN RAISE(ABORT, 'Only synthetic rehearsal records may be deleted.') END;
                INSERT INTO ExperimentalRehearsalTombstone (Kind, SyncId)
                    SELECT 'message', SyncId FROM ExperimentalMessageIdentity WHERE MessageId=OLD.Id;
                DELETE FROM ExperimentalMessageIdentity WHERE MessageId=OLD.Id;
                DELETE FROM ExperimentalRehearsalAllowedDeletion WHERE Kind='message' AND LocalId=OLD.Id;
            END;
            CREATE TRIGGER ExperimentalRehearsalSessionDelete BEFORE DELETE ON Sessions BEGIN
                SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM ExperimentalRehearsalAllowedDeletion WHERE Kind='session' AND LocalId=OLD.Id)
                    THEN RAISE(ABORT, 'Only synthetic rehearsal records may be deleted.') END;
                SELECT CASE WHEN EXISTS (SELECT 1 FROM Messages WHERE ChatSessionId=OLD.Id)
                    THEN RAISE(ABORT, 'Only empty synthetic sessions may be deleted.') END;
                INSERT INTO ExperimentalRehearsalTombstone (Kind, SyncId)
                    SELECT 'session', SyncId FROM ExperimentalSessionIdentity WHERE SessionId=OLD.Id;
                DELETE FROM ExperimentalSessionIdentity WHERE SessionId=OLD.Id;
                DELETE FROM ExperimentalRehearsalAllowedDeletion WHERE Kind='session' AND LocalId=OLD.Id;
            END;
            INSERT INTO ExperimentalRehearsalAllowedDeletion VALUES ('message', $message), ('message', $conflict), ('message', $child), ('session', $session);
            """;
        cmd.Parameters.AddWithValue("$message", message.Id);
        cmd.Parameters.AddWithValue("$conflict", conflict.Id);
        cmd.Parameters.AddWithValue("$session", deletedSession.Id);
        cmd.Parameters.AddWithValue("$child", child.Id);
        await cmd.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
        return new(message.Id, conflict.Id, deletedSession.Id, child.Id, keeper.Id, messageId, conflictId, sessionId, childId, sharedPath);
    }

    internal static async Task ChangeBranchesAsync(string a, string b, Scenario s, CancellationToken token)
    {
        await using (var db = Context(a))
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Messages WHERE Id = {s.DeleteMessage}", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Messages WHERE Id = {s.ConflictMessage}", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Media WHERE ChatMessageId = {s.ChildMessage}", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Messages WHERE Id = {s.ChildMessage}", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Sessions WHERE Id = {s.DeleteSession}", token);
            await tx.CommitAsync(token);
        }
        await using (var db = Context(b))
        {
            var conflict = await db.Messages.SingleAsync(m => m.Id == s.ConflictMessage, token);
            conflict.Content = "Rehearsal: edited on B while A deleted";
            await db.SaveChangesAsync(token);
        }
    }

    internal static async Task<(int, int)> ApplyAsync(string baseline, string a, string b, string result, Scenario s, CancellationToken token)
    {
        await using var original = Context(baseline);
        await using var left = Context(a);
        await using var right = Context(b);
        await using var output = Context(result);
        foreach (var db in new[] { original, left, right, output }) await db.Database.OpenConnectionAsync(token);
        var lc = (SqliteConnection)left.Database.GetDbConnection();
        if (!await HasMarkerAsync(lc, "message", s.DeleteMessageSyncId, token) ||
            !await HasMarkerAsync(lc, "message", s.ConflictMessageSyncId, token) ||
            !await HasMarkerAsync(lc, "message", s.ChildMessageSyncId, token) ||
            !await HasMarkerAsync(lc, "session", s.DeleteSessionSyncId, token))
            throw new InvalidDataException("Deletion without a marker cannot be applied.");
        // All records are known synthetic IDs from the same copied baseline. Never apply to arbitrary data.
        var oldMessage = await original.Messages.AsNoTracking().SingleAsync(m => m.Id == s.DeleteMessage, token);
        var rightMessage = await right.Messages.AsNoTracking().SingleAsync(m => m.Id == s.DeleteMessage, token);
        var oldConflict = await original.Messages.AsNoTracking().SingleAsync(m => m.Id == s.ConflictMessage, token);
        var rightConflict = await right.Messages.AsNoTracking().SingleAsync(m => m.Id == s.ConflictMessage, token);
        if (oldMessage.Content != rightMessage.Content || oldConflict.Content == rightConflict.Content ||
            !await right.Sessions.AnyAsync(x => x.Id == s.DeleteSession, token))
            throw new InvalidDataException("Unexpected branch state in deletion rehearsal.");
        await using var tx = await output.Database.BeginTransactionAsync(token);
        var oc = (SqliteConnection)output.Database.GetDbConnection();
        int changed = 0;
        if (!await HasMarkerAsync(oc, "message", s.DeleteMessageSyncId, token, (SqliteTransaction)output.Database.CurrentTransaction!.GetDbTransaction()))
        {
            if (await output.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Messages WHERE Id = {s.DeleteMessage}", token) != 1)
                throw new InvalidDataException("Expected synthetic message was missing without a marker.");
            changed++;
        }
        if (!await HasMarkerAsync(oc, "session", s.DeleteSessionSyncId, token, (SqliteTransaction)output.Database.CurrentTransaction!.GetDbTransaction()))
        {
            if (await output.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Media WHERE ChatMessageId = {s.ChildMessage}", token) != 1 ||
                await output.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Messages WHERE Id = {s.ChildMessage}", token) != 1)
                throw new InvalidDataException("Expected synthetic child and media reference were missing.");
            if (await output.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Sessions WHERE Id = {s.DeleteSession}", token) != 1)
                throw new InvalidDataException("Expected synthetic session was missing without a marker.");
            changed++;
        }
        if (await HasMarkerAsync(oc, "message", s.ConflictMessageSyncId, token, (SqliteTransaction)output.Database.CurrentTransaction!.GetDbTransaction()) ||
            !await output.Messages.AnyAsync(m => m.Id == s.ConflictMessage && m.Content == oldConflict.Content, token))
            throw new InvalidDataException("The conflicting message must remain unresolved in the result.");
        await tx.CommitAsync(token);
        return (changed, 1);
    }

    // Controlled replay of exactly one stale synthetic session. This is not a general import routine.
    internal static async Task VerifyStaleReplayAsync(string stalePath, string resultPath, string resultDirectory, Scenario s, CancellationToken token)
    {
        await using var stale = Context(stalePath);
        await using var result = Context(resultPath);
        var oldSession = await stale.Sessions.AsNoTracking().SingleAsync(x => x.Id == s.DeleteSession, token);
        if (!await stale.Messages.AnyAsync(x => x.Id == s.ChildMessage, token) ||
            !await stale.Media.AnyAsync(x => x.ChatMessageId == s.ChildMessage && x.LocalPath == s.SharedMediaPath, token))
            throw new InvalidDataException("The offline copy does not contain the expected stale session and media reference.");
        if (await result.Sessions.AnyAsync(x => x.Id == s.DeleteSession, token) ||
            await result.Messages.AnyAsync(x => x.Id == s.ChildMessage, token) ||
            await result.Media.AnyAsync(x => x.ChatMessageId == s.ChildMessage, token) ||
            await result.Media.CountAsync(x => x.ChatMessageId == s.KeeperMessage && x.LocalPath == s.SharedMediaPath, token) != 1)
            throw new InvalidDataException("The deleted records or shared-media reference are in an unexpected state.");
        string file = Path.Combine(resultDirectory, "files", Path.GetFileName(s.SharedMediaPath));
        if (!File.Exists(file) || await File.ReadAllTextAsync(file, token) != "Synthetic shared media for deletion rehearsal.")
            throw new InvalidDataException("The shared synthetic media was lost or changed.");

        await using var tx = await result.Database.BeginTransactionAsync(token);
        var c = (SqliteConnection)result.Database.GetDbConnection();
        var sqlTx = (SqliteTransaction)tx.GetDbTransaction();
        if (!await HasMarkerAsync(c, "message", s.ChildMessageSyncId, token, sqlTx))
            throw new InvalidDataException("The deleted child message marker is missing.");
        await using var cmd = c.CreateCommand();
        cmd.Transaction = sqlTx;
        // Actually attempt to insert the stale session. The marker gates insertion; rollback
        // even on an unexpected insert so this diagnostic can never resurrect the record.
        cmd.CommandText = """
            INSERT INTO Sessions (Id, Endpoint, Type, Title, CreatedAt, LastUsedAt)
            SELECT $id, $endpoint, $type, $title, $created, $used
            WHERE NOT EXISTS (SELECT 1 FROM ExperimentalRehearsalTombstone WHERE Kind='session' AND SyncId=$syncId)
            """;
        cmd.Parameters.AddWithValue("$id", oldSession.Id);
        cmd.Parameters.AddWithValue("$endpoint", (int)oldSession.Endpoint);
        cmd.Parameters.AddWithValue("$type", oldSession.Type);
        cmd.Parameters.AddWithValue("$title", oldSession.Title);
        cmd.Parameters.AddWithValue("$created", oldSession.CreatedAt);
        cmd.Parameters.AddWithValue("$used", oldSession.LastUsedAt);
        cmd.Parameters.AddWithValue("$syncId", s.DeleteSessionSyncId);
        int inserted = await cmd.ExecuteNonQueryAsync(token);
        await tx.RollbackAsync(token);
        if (inserted != 0) throw new InvalidDataException("Stale session replay was not blocked by its deletion marker.");
    }

    private static async Task<string> IdentityAsync(SqliteConnection c, string table, string key, int id, CancellationToken token)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT SyncId FROM {table} WHERE {key} = $id";
        cmd.Parameters.AddWithValue("$id", id);
        string? value = Convert.ToString(await cmd.ExecuteScalarAsync(token));
        return Guid.TryParse(value, out _) ? value! : throw new InvalidDataException("Missing synthetic identity.");
    }

    private static async Task<bool> HasMarkerAsync(SqliteConnection c, string kind, string id, CancellationToken token, SqliteTransaction? tx = null)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM ExperimentalRehearsalTombstone WHERE Kind=$kind AND SyncId=$id";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(token)) == 1;
    }
}

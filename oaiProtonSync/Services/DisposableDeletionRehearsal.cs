using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using oaiResponsesAvalonia.Data;
using oaiResponsesAvalonia.Services;

namespace oaiProtonSync.Services;

/// <summary>
/// Controlled deletion experiment on synthetic records in rehearsal database copies only.
/// Not a general sync implementation: timestamps, real data, media, cascading deletes,
/// missing markers, and conflict resolution are deliberately outside its scope.
/// </summary>
internal static class DisposableDeletionRehearsal
{
    internal sealed record Scenario(int DeleteMessage, int ConflictMessage, int DeleteSession, string DeleteMessageSyncId, string ConflictMessageSyncId, string DeleteSessionSyncId);

    private static AppDbContext Context(string path) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()).Options);

    internal static async Task<Scenario> SetupAsync(string path, CancellationToken token)
    {
        await using var db = Context(path);
        var messageSession = new ChatSession(EndpointType.Responses, "Responses", "[MERGE REHEARSAL] Message deletion");
        var conflictSession = new ChatSession(EndpointType.Responses, "Responses", "[MERGE REHEARSAL] Delete versus edit");
        var emptySession = new ChatSession(EndpointType.Responses, "Responses", "[MERGE REHEARSAL] Session deletion");
        var message = new ChatMessage { Role = "user", Content = "Rehearsal: delete this message" };
        var conflict = new ChatMessage { Role = "user", Content = "Rehearsal: original delete-versus-edit message" };
        messageSession.Messages.Add(message);
        conflictSession.Messages.Add(conflict);
        db.Sessions.AddRange(messageSession, conflictSession, emptySession);
        await db.SaveChangesAsync(token);
        await db.Database.OpenConnectionAsync(token);
        var c = (SqliteConnection)db.Database.GetDbConnection();
        string messageId = await IdentityAsync(c, "ExperimentalMessageIdentity", "MessageId", message.Id, token);
        string conflictId = await IdentityAsync(c, "ExperimentalMessageIdentity", "MessageId", conflict.Id, token);
        string sessionId = await IdentityAsync(c, "ExperimentalSessionIdentity", "SessionId", emptySession.Id, token);
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
            INSERT INTO ExperimentalRehearsalAllowedDeletion VALUES ('message', $message), ('message', $conflict), ('session', $session);
            """;
        cmd.Parameters.AddWithValue("$message", message.Id);
        cmd.Parameters.AddWithValue("$conflict", conflict.Id);
        cmd.Parameters.AddWithValue("$session", emptySession.Id);
        await cmd.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
        return new(message.Id, conflict.Id, emptySession.Id, messageId, conflictId, sessionId);
    }

    internal static async Task ChangeBranchesAsync(string a, string b, Scenario s, CancellationToken token)
    {
        await using (var db = Context(a))
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Messages WHERE Id = {s.DeleteMessage}", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Messages WHERE Id = {s.ConflictMessage}", token);
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

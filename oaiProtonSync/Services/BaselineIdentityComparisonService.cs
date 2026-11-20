using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace oaiProtonSync.Services;

public sealed record IdentityDifference(Guid SyncId, string Detail);
public sealed record IdentityComparisonResult(
    int SharedSessions, int SharedMessages,
    IReadOnlyList<IdentityDifference> SessionsOnlyA, IReadOnlyList<IdentityDifference> SessionsOnlyB,
    IReadOnlyList<IdentityDifference> MessagesOnlyA, IReadOnlyList<IdentityDifference> MessagesOnlyB,
    IReadOnlyList<IdentityDifference> DifferentSessions, IReadOnlyList<IdentityDifference> DifferentMessages);

/// <summary>Compares two closed disposable baseline copies. It never merges or interprets missing records as deletions.</summary>
public sealed class BaselineIdentityComparisonService
{
    private sealed record Item(Guid Id, string Values, Guid? Parent);
    private sealed record Data(Dictionary<Guid, Item> Sessions, Dictionary<Guid, Item> Messages);

    public async Task<IdentityComparisonResult> CompareAsync(string folderA, string folderB, CancellationToken token = default)
    {
        var a = await ReadAsync(folderA, token);
        var b = await ReadAsync(folderB, token);
        token.ThrowIfCancellationRequested();
        var sharedSessions = a.Sessions.Keys.Intersect(b.Sessions.Keys).ToArray();
        var sharedMessages = a.Messages.Keys.Intersect(b.Messages.Keys).ToArray();
        static IReadOnlyList<IdentityDifference> Only(Dictionary<Guid, Item> left, Dictionary<Guid, Item> right, bool messages) =>
            left.Values.Where(x => !right.ContainsKey(x.Id)).OrderBy(x => x.Id)
                .Select(x => new IdentityDifference(x.Id, messages ? $"session {x.Parent}" : "session")).ToArray();
        var changedSessions = sharedSessions.Where(id => a.Sessions[id].Values != b.Sessions[id].Values)
            .OrderBy(id => id).Select(id => new IdentityDifference(id, "session fields differ")).ToArray();
        var changedMessages = sharedMessages.Where(id => a.Messages[id].Values != b.Messages[id].Values || a.Messages[id].Parent != b.Messages[id].Parent)
            .OrderBy(id => id).Select(id => new IdentityDifference(id,
                a.Messages[id].Parent != b.Messages[id].Parent ? "parent session differs" : "message fields differ")).ToArray();
        return new IdentityComparisonResult(sharedSessions.Length, sharedMessages.Length,
            Only(a.Sessions, b.Sessions, false), Only(b.Sessions, a.Sessions, false),
            Only(a.Messages, b.Messages, true), Only(b.Messages, a.Messages, true), changedSessions, changedMessages);
    }

    private static async Task<Data> ReadAsync(string folder, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(folder) || !File.Exists(Path.Combine(folder, "experimental-baseline.json")))
            throw new InvalidDataException("Select a disposable baseline copy containing experimental-baseline.json. Normal app data and snapshot ZIPs are not accepted.");
        string path = Path.Combine(folder, "localhistory.db");
        if (!File.Exists(path)) throw new FileNotFoundException("The baseline copy has no localhistory.db.", path);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        // A deferred read transaction keeps each database internally consistent during its read.
        using var transaction = connection.BeginTransaction(deferred: true);
        var sessions = await ReadItemsAsync(connection, transaction,
            "SELECT s.*, i.SyncId AS __sync FROM Sessions s LEFT JOIN ExperimentalSessionIdentity i ON i.SessionId=s.Id", false, token);
        var messages = await ReadItemsAsync(connection, transaction,
            "SELECT m.*, i.SyncId AS __sync, si.SyncId AS __parent FROM Messages m LEFT JOIN ExperimentalMessageIdentity i ON i.MessageId=m.Id LEFT JOIN ExperimentalSessionIdentity si ON si.SessionId=m.ChatSessionId", true, token);
        foreach (var message in messages.Values)
            if (message.Parent is null || !sessions.ContainsKey(message.Parent.Value))
                throw new InvalidDataException("A message references an unknown session identity.");
        foreach (var (table, expected) in new[] { ("ExperimentalSessionIdentity", sessions.Count), ("ExperimentalMessageIdentity", messages.Count) })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != expected)
                throw new InvalidDataException($"{table} contains extra or missing mappings.");
        }
        return new Data(sessions, messages);
    }

    private static async Task<Dictionary<Guid, Item>> ReadItemsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, bool messages, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new Dictionary<Guid, Item>();
        while (await reader.ReadAsync(token))
        {
            token.ThrowIfCancellationRequested();
            int syncOrdinal = reader.GetOrdinal("__sync");
            if (reader.IsDBNull(syncOrdinal) || !Guid.TryParse(reader.GetString(syncOrdinal), out var id) || id == Guid.Empty)
                throw new InvalidDataException("Missing or invalid experimental sync identity; comparison stopped.");
            Guid? parent = null;
            if (messages)
            {
                int parentOrdinal = reader.GetOrdinal("__parent");
                if (reader.IsDBNull(parentOrdinal) || !Guid.TryParse(reader.GetString(parentOrdinal), out var parentId) || parentId == Guid.Empty)
                    throw new InvalidDataException("Missing or invalid parent session identity; comparison stopped.");
                parent = parentId;
            }
            // Compare all stored scalar fields except local keys. Do not expose contents in the activity log.
            var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                if (name is "Id" or "ChatSessionId" or "__sync" or "__parent") continue;
                fields.Add(name, reader.IsDBNull(i) ? null : reader.GetValue(i));
            }
            if (!result.TryAdd(id, new Item(id, JsonSerializer.Serialize(fields), parent)))
                throw new InvalidDataException("Duplicate experimental sync identity; comparison stopped.");
        }
        return result;
    }
}

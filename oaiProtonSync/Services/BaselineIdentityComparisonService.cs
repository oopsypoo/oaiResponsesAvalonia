using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace oaiProtonSync.Services;

public sealed record IdentityDifference(Guid SyncId, string Detail);
public sealed record RehearsalDeletionFinding(string Kind, Guid SyncId, string Classification);
public sealed record RehearsalDeletionComparison(IReadOnlyList<RehearsalDeletionFinding> Findings);
public sealed record ThreeWayPreview(IReadOnlyList<RehearsalDeletionFinding> Findings);
public sealed record IdentityComparisonResult(
    int SharedSessions, int SharedMessages,
    IReadOnlyList<IdentityDifference> SessionsOnlyA, IReadOnlyList<IdentityDifference> SessionsOnlyB,
    IReadOnlyList<IdentityDifference> MessagesOnlyA, IReadOnlyList<IdentityDifference> MessagesOnlyB,
    IReadOnlyList<IdentityDifference> DifferentSessions, IReadOnlyList<IdentityDifference> DifferentMessages);

/// <summary>Compares two closed disposable baseline copies. It never merges or interprets missing records as deletions.</summary>
public sealed class BaselineIdentityComparisonService
{
    private sealed record Item(Guid Id, string Values, Guid? Parent);
    private sealed record BaselineStamp(Guid Id, string Digest);
    private sealed record Data(Dictionary<Guid, Item> Sessions, Dictionary<Guid, Item> Messages,
        HashSet<(string Kind, Guid Id)>? Tombstones, BaselineStamp? Stamp);

    // This covers only the fields used by the preview, not media or arbitrary database tables.
    private static string Fingerprint(Data data)
    {
        if (data.Tombstones is null)
            throw new InvalidDataException("The shared base is missing rehearsal deletion tracking.");
        var payload = new
        {
            Version = 1,
            Sessions = data.Sessions.Values.OrderBy(x => x.Id).ToArray(),
            Messages = data.Messages.Values.OrderBy(x => x.Id).ToArray(),
            Tombstones = data.Tombstones.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Id)
                .Select(x => new { x.Kind, x.Id }).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
    }

    // Only call on an exclusively owned, disposable starting copy, before making branches.
    public async Task StampRehearsalBaseAsync(string folder, CancellationToken token = default)
    {
        var data = await ReadAsync(folder, token);
        if (data.Stamp is not null)
            throw new InvalidDataException("This disposable base already has a rehearsal stamp; create a new rehearsal.");
        string digest = Fingerprint(data);
        string path = Path.Combine(folder, "localhistory.db");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE ExperimentalRehearsalBase (Id TEXT NOT NULL, Digest TEXT NOT NULL); INSERT INTO ExperimentalRehearsalBase (Id, Digest) VALUES ($id, $digest);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$digest", digest);
        await command.ExecuteNonQueryAsync(token);
    }

    private static void ValidateRehearsalBase(Data original, Data a, Data b)
    {
        if (original.Stamp is null || a.Stamp is null || b.Stamp is null)
            throw new InvalidDataException("Rehearsal baseline metadata is missing. Create a new disposable merge rehearsal; older workspaces cannot be verified.");
        if (original.Stamp != a.Stamp || original.Stamp != b.Stamp)
            throw new InvalidDataException("The selected copies have different rehearsal baseline metadata. Select the shared-base, branch-A and branch-B from the same rehearsal.");
        if (!string.Equals(Fingerprint(original), original.Stamp.Digest, StringComparison.Ordinal))
            throw new InvalidDataException("The selected shared starting copy has changed since the rehearsal branches were created. Select its original shared-base folder.");
    }

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

    /// <summary>Read-only comparison for the controlled rehearsal; the caller supplies the actual shared starting copy.</summary>
    public async Task<RehearsalDeletionComparison> CompareRehearsalDeletionsAsync(
        string baselineFolder, string folderA, string folderB, CancellationToken token = default)
    {
        var original = await ReadAsync(baselineFolder, token);
        var a = await ReadAsync(folderA, token);
        var b = await ReadAsync(folderB, token);
        return CompareDeletions(original, a, b, token);
    }

    private static RehearsalDeletionComparison CompareDeletions(Data original, Data a, Data b, CancellationToken token)
    {
        if (original.Tombstones is null || a.Tombstones is null || b.Tombstones is null)
            throw new InvalidDataException("All three copies must contain the experimental rehearsal deletion table; missing tracking is not interpreted as no deletions.");

        var findings = new List<RehearsalDeletionFinding>();
        void CompareKind(string kind, Dictionary<Guid, Item> originals, Dictionary<Guid, Item> left, Dictionary<Guid, Item> right)
        {
            var ids = originals.Keys.Concat(left.Keys).Concat(right.Keys)
                .Concat(a.Tombstones.Where(x => x.Kind == kind).Select(x => x.Id))
                .Concat(b.Tombstones.Where(x => x.Kind == kind).Select(x => x.Id)).Distinct();
            foreach (var id in ids.OrderBy(x => x))
            {
                token.ThrowIfCancellationRequested();
                bool deletedA = a.Tombstones.Contains((kind, id));
                bool deletedB = b.Tombstones.Contains((kind, id));
                originals.TryGetValue(id, out var initial);
                left.TryGetValue(id, out var itemA);
                right.TryGetValue(id, out var itemB);
                if (!deletedA && !deletedB)
                {
                    if (initial is not null && (itemA is null || itemB is null))
                        findings.Add(new(kind, id, "Missing without deletion marker; unknown"));
                    continue;
                }
                if (initial is null)
                {
                    findings.Add(new(kind, id, "Deletion marker not present in shared baseline; unknown"));
                    continue;
                }
                if (deletedA && deletedB)
                {
                    findings.Add(new(kind, id, "Deleted on both"));
                    continue;
                }
                var other = deletedA ? itemB : itemA;
                var otherData = deletedA ? b : a;
                if (other is null)
                {
                    findings.Add(new(kind, id, "Other branch missing without deletion marker; unknown"));
                    continue;
                }
                bool changed = initial.Values != other.Values || initial.Parent != other.Parent;
                if (kind == "session")
                {
                    // A session deletion must not hide edits, additions, removals or moves of child messages.
                    var oldChildren = original.Messages.Values.Where(m => m.Parent == id).ToDictionary(m => m.Id);
                    var newChildren = otherData.Messages.Values.Where(m => m.Parent == id).ToDictionary(m => m.Id);
                    changed |= oldChildren.Count != newChildren.Count || oldChildren.Any(pair =>
                        !newChildren.TryGetValue(pair.Key, out var child) || pair.Value.Values != child.Values || pair.Value.Parent != child.Parent);
                }
                findings.Add(new(kind, id, changed ? "Delete-versus-edit conflict" : deletedA ? "Deleted on A; B unchanged" : "Deleted on B; A unchanged"));
            }
        }
        CompareKind("session", original.Sessions, a.Sessions, b.Sessions);
        CompareKind("message", original.Messages, a.Messages, b.Messages);
        return new RehearsalDeletionComparison(findings);
    }

    /// <summary>
    /// Experimental read-only preview. Checks rehearsal stamps and the selected base's
    /// compared fields, but does not prove ancestry or compare media.
    /// </summary>
    public async Task<ThreeWayPreview> PreviewThreeWayAsync(
        string baselineFolder, string folderA, string folderB, CancellationToken token = default)
    {
        var original = await ReadAsync(baselineFolder, token);
        var a = await ReadAsync(folderA, token);
        var b = await ReadAsync(folderB, token);
        ValidateRehearsalBase(original, a, b);
        // Refuse to infer deletions if any copy lacks the experimental tracking table.
        var deletions = CompareDeletions(original, a, b, token);
        var findings = new List<RehearsalDeletionFinding>(deletions.Findings);
        static bool Equal(Item left, Item right) => left.Values == right.Values && left.Parent == right.Parent;

        void CompareKind(string kind, Dictionary<Guid, Item> initial, Dictionary<Guid, Item> left, Dictionary<Guid, Item> right)
        {
            foreach (var id in initial.Keys.Concat(left.Keys).Concat(right.Keys).Distinct().OrderBy(x => x))
            {
                token.ThrowIfCancellationRequested();
                if (a.Tombstones!.Contains((kind, id)) || b.Tombstones!.Contains((kind, id))) continue;
                initial.TryGetValue(id, out var before);
                left.TryGetValue(id, out var x);
                right.TryGetValue(id, out var y);
                if (before is null)
                {
                    if (x is null && y is null) continue;
                    // A baseline tombstone must never be treated as a new addition.
                    if (original.Tombstones!.Contains((kind, id)))
                        findings.Add(new(kind, id, "Present after baseline deletion marker; unknown"));
                    else if (x is null) findings.Add(new(kind, id, "Added on B"));
                    else if (y is null) findings.Add(new(kind, id, "Added on A"));
                    else findings.Add(new(kind, id, Equal(x, y) ? "Same addition on both" : "Same new identity with different values; conflict"));
                    continue;
                }
                // Missing items without markers are already reported by CompareDeletions.
                if (x is null || y is null) continue;
                bool changedA = !Equal(before, x), changedB = !Equal(before, y);
                if (!changedA && !changedB) continue;
                string status = changedA && changedB
                    ? Equal(x, y) ? "Same edit on both" : "Different edits; conflict"
                    : changedA ? "Edited on A; B unchanged" : "Edited on B; A unchanged";
                findings.Add(new(kind, id, status));
            }
        }
        CompareKind("session", original.Sessions, a.Sessions, b.Sessions);
        CompareKind("message", original.Messages, a.Messages, b.Messages);
        return new ThreeWayPreview(findings.OrderBy(f => f.Kind).ThenBy(f => f.SyncId).ToArray());
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
        HashSet<(string Kind, Guid Id)>? tombstones = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ExperimentalRehearsalTombstone'";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 1)
            {
                tombstones = new();
                command.CommandText = "SELECT Kind, SyncId FROM ExperimentalRehearsalTombstone";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1)) throw new InvalidDataException("Invalid rehearsal deletion marker.");
                    var kind = reader.GetString(0);
                    if ((kind != "session" && kind != "message") || !Guid.TryParse(reader.GetString(1), out var id) || id == Guid.Empty || !tombstones.Add((kind, id)))
                        throw new InvalidDataException("Invalid or duplicate rehearsal deletion marker.");
                    if ((kind == "session" ? sessions : messages).ContainsKey(id))
                        throw new InvalidDataException("An identity is both present and marked deleted.");
                }
            }
        }
        BaselineStamp? stamp = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ExperimentalRehearsalBase'";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 1)
            {
                command.CommandText = "SELECT Id, Digest FROM ExperimentalRehearsalBase";
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token) || reader.IsDBNull(0) || reader.IsDBNull(1) ||
                    !Guid.TryParse(reader.GetString(0), out var id) || id == Guid.Empty)
                    throw new InvalidDataException("Invalid rehearsal baseline metadata.");
                string digest = reader.GetString(1);
                if (digest.Length != 64 || digest.Any(c => !((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))) ||
                    await reader.ReadAsync(token))
                    throw new InvalidDataException("Invalid rehearsal baseline metadata.");
                stamp = new BaselineStamp(id, digest);
            }
        }
        return new Data(sessions, messages, tombstones, stamp);
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

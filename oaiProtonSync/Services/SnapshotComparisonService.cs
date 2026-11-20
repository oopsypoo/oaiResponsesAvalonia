using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace oaiProtonSync.Services;

public sealed record ComparisonSession(long Id, string Title, int MessageCount, IReadOnlySet<string> MessageEvidence);
public sealed record SnapshotCandidate(ComparisonSession First, ComparisonSession Second, int SharedEvidence, bool SameTitle, bool Ambiguous);
public sealed record SnapshotComparisonResult(string FirstSnapshotId, string SecondSnapshotId,
    IReadOnlyList<ComparisonSession> FirstSessions, IReadOnlyList<ComparisonSession> SecondSessions,
    IReadOnlyList<SnapshotCandidate> Candidates);

/// <summary>Compares evidence in two verified snapshots. It never modifies the archives or live data.</summary>
public sealed class SnapshotComparisonService
{
    public async Task<SnapshotComparisonResult> CompareAsync(string firstArchive, string secondArchive,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstArchive);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondArchive);
        string work = Directory.CreateTempSubdirectory("oai-snapshot-compare-").FullName;
        try
        {
            var first = await LoadArchiveAsync(firstArchive, Path.Combine(work, "first"), progress, cancellationToken);
            var second = await LoadArchiveAsync(secondArchive, Path.Combine(work, "second"), progress, cancellationToken);
            progress?.Invoke("Comparing session titles and exact message evidence…");
            return CompareSessions(first.Id, first.Sessions, second.Id, second.Sessions, cancellationToken);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) { progress?.Invoke($"Could not remove temporary comparison data at {work}: {ex.Message}"); }
        }
    }

    public static SnapshotComparisonResult CompareSessions(string firstId, IReadOnlyList<ComparisonSession> first,
        string secondId, IReadOnlyList<ComparisonSession> second, CancellationToken cancellationToken = default)
    {
        var pairs = new List<(ComparisonSession First, ComparisonSession Second, int Shared, bool Title)>();
        foreach (var a in first)
        foreach (var b in second)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int shared = a.MessageEvidence.Count(b.MessageEvidence.Contains);
            bool title = !string.IsNullOrWhiteSpace(a.Title) && !string.IsNullOrWhiteSpace(b.Title)
                && string.Equals(a.Title.Trim(), b.Title.Trim(), StringComparison.OrdinalIgnoreCase);
            if (shared > 0 || title)
                pairs.Add((a, b, shared, title));
        }
        var firstCounts = pairs.GroupBy(p => p.First.Id).ToDictionary(g => g.Key, g => g.Count());
        var secondCounts = pairs.GroupBy(p => p.Second.Id).ToDictionary(g => g.Key, g => g.Count());
        var candidates = pairs.Select(p => new SnapshotCandidate(p.First, p.Second, p.Shared, p.Title,
                firstCounts[p.First.Id] > 1 || secondCounts[p.Second.Id] > 1))
            .OrderByDescending(p => p.SharedEvidence).ThenBy(p => p.First.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.First.Id).ThenBy(p => p.Second.Id).ToList();
        return new SnapshotComparisonResult(firstId, secondId, first, second, candidates);
    }

    private static async Task<(string Id, IReadOnlyList<ComparisonSession> Sessions)> LoadArchiveAsync(
        string source, string directory, Action<string>? progress, CancellationToken ct)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Snapshot archive not found.", source);
        Directory.CreateDirectory(directory);
        // A private copy prevents a changing input from being substituted after verification.
        string copy = Path.Combine(directory, "input.zip");
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        await using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await input.CopyToAsync(output, ct);
        progress?.Invoke($"Verifying snapshot: {source}");
        var verified = await new SnapshotVerificationService().VerifyAsync(copy, directory, Array.Empty<string>(), ct);
        string database = Path.Combine(directory, "comparison.db");
        using (var archive = ZipFile.OpenRead(copy))
        {
            var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Snapshot manifest is missing.");
            await using var manifestStream = manifestEntry.Open();
            using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);
            string? databaseEntryName = manifest.RootElement.GetProperty("DatabaseFile").GetString();
            var entry = archive.GetEntry(databaseEntryName ?? "") ?? throw new InvalidDataException("Snapshot database is missing.");
            await using var stream = entry.Open();
            await using var output = new FileStream(database, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await stream.CopyToAsync(output, ct);
        }
        return (verified.SnapshotId, await ReadSessionsAsync(database, ct));
    }

    private static async Task<IReadOnlyList<ComparisonSession>> ReadSessionsAsync(string database, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync(ct);
        var sessions = new Dictionary<long, (string Title, int Count, HashSet<string> Evidence)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, Title FROM Sessions ORDER BY Id";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                sessions.Add(reader.GetInt64(0), (reader.IsDBNull(1) ? "" : reader.GetString(1), 0, new HashSet<string>(StringComparer.Ordinal)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ChatSessionId, Role, Content FROM Messages ORDER BY Id";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                long id = reader.GetInt64(0);
                if (!sessions.TryGetValue(id, out var session))
                    throw new InvalidDataException("A message references a missing session.");
                string role = reader.IsDBNull(1) ? "" : reader.GetString(1);
                string content = reader.IsDBNull(2) ? "" : reader.GetString(2);
                // Short boilerplate messages and empty content are not reliable matching evidence.
                if (!string.IsNullOrWhiteSpace(role) && content.Trim().Length >= 40)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { role, content }));
                    session.Evidence.Add(Convert.ToHexString(SHA256.HashData(bytes)));
                }
                sessions[id] = (session.Title, session.Count + 1, session.Evidence);
            }
        }
        return sessions.Select(p => new ComparisonSession(p.Key, p.Value.Title, p.Value.Count, p.Value.Evidence)).ToList();
    }
}

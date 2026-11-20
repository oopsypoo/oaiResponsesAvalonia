using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using oaiProtonSync.Services;

namespace ImageInspector.Tests;

public sealed class SnapshotComparisonTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    private const string LongMessage = "This is an intentionally long message with sufficient content for comparison evidence.";

    [Fact]
    public async Task Comparison_UsesMessageContentNotLocalIdsAndKeepsInputsUnchanged()
    {
        string first = CreateArchive("first", 1, "Linux session", LongMessage);
        string second = CreateArchive("second", 20, "Windows session", LongMessage);
        byte[] beforeFirst = File.ReadAllBytes(first), beforeSecond = File.ReadAllBytes(second);
        var result = await new SnapshotComparisonService().CompareAsync(first, second);
        Assert.Equal("first", result.FirstSnapshotId);
        Assert.Equal("second", result.SecondSnapshotId);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(1, candidate.SharedEvidence);
        Assert.False(candidate.SameTitle);
        Assert.Equal(1, candidate.First.Id);
        Assert.Equal(20, candidate.Second.Id);
        Assert.Equal(beforeFirst, File.ReadAllBytes(first));
        Assert.Equal(beforeSecond, File.ReadAllBytes(second));
    }

    [Fact]
    public async Task EqualIdsAndShortMessagesDoNotProveAMatch()
    {
        string first = CreateArchive("first", 1, "First", "Hello");
        string second = CreateArchive("second", 1, "Second", "Hello");
        var result = await new SnapshotComparisonService().CompareAsync(first, second);
        Assert.Empty(result.Candidates);
        Assert.Single(result.FirstSessions);
        Assert.Single(result.SecondSessions);
    }

    [Fact]
    public void SameTitleAndMultipleCandidatesRemainAmbiguous()
    {
        var a = new ComparisonSession(1, "Same", 0, new HashSet<string>());
        var b = new ComparisonSession(4, "same", 0, new HashSet<string>());
        var c = new ComparisonSession(5, "Same", 0, new HashSet<string>());
        var result = SnapshotComparisonService.CompareSessions("A", new[] { a }, "B", new[] { b, c });
        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, item => { Assert.True(item.SameTitle); Assert.True(item.Ambiguous); Assert.Equal(0, item.SharedEvidence); });
    }

    [Fact]
    public async Task ComparisonRejectsInvalidArchive()
    {
        string first = CreateArchive("first", 1, "First", LongMessage);
        string invalid = Path.Combine(_root, "invalid.zip");
        File.WriteAllText(invalid, "not a snapshot");
        await Assert.ThrowsAnyAsync<Exception>(() => new SnapshotComparisonService().CompareAsync(first, invalid));
    }

    private string CreateArchive(string name, int sessionId, string title, string message)
    {
        string database = Path.Combine(_root, name + ".db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Sessions(Id INTEGER PRIMARY KEY, Title TEXT); CREATE TABLE Messages(Id INTEGER PRIMARY KEY, ChatSessionId INTEGER, Role TEXT, Content TEXT);";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO Sessions VALUES ($id, $title); INSERT INTO Messages VALUES (1, $id, 'user', $message);";
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$message", message);
            command.ExecuteNonQuery();
        }
        byte[] data = File.ReadAllBytes(database);
        var manifest = new { ManifestVersion = 1, SnapshotId = name, DatabaseFile = "localhistory.db", DatabaseSha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), MediaFiles = Array.Empty<object>() };
        string archivePath = Path.Combine(_root, name + ".zip");
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using (var stream = zip.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(stream, manifest);
            using (var stream = zip.CreateEntry("localhistory.db").Open()) stream.Write(data);
        }
        return archivePath;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using oaiProtonSync.Services;

namespace ImageInspector.Tests;

public sealed class IsolatedSnapshotRestoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;

    [Theory]
    [InlineData(60, "20261119T151850+0100")]
    [InlineData(120, "20261119T151850+0200")]
    [InlineData(-330, "20261119T151850-0530")]
    [InlineData(0, "20261119T151850+0000")]
    public void SnapshotId_UsesLocalTimeAndOffset(int offsetMinutes, string expectedTimestamp)
    {
        var localTime = new DateTimeOffset(2026, 11, 19, 15, 18, 50, TimeSpan.FromMinutes(offsetMinutes));
        var id = Guid.Parse("03f554b2-5492-460e-a308-b22b7def840d");

        string snapshotId = ConversationSnapshotService.CreateSnapshotId(localTime, id);

        Assert.Equal($"{expectedTimestamp}_03f554b25492460ea308b22b7def840d", snapshotId);
    }

    [Fact]
    public async Task Restore_ReportsUnmappedLegacyDeduplicatedReference()
    {
        string archive = CreateSnapshot(["media/first.txt", "media/second.txt"]);
        var service = new IsolatedSnapshotRestoreService(Path.Combine(_root, "restores"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.RestoreAsync(archive, _ => { }));

        Assert.Contains("media/second.txt", error.Message);
        Assert.Contains("cannot be mapped", error.Message);
        Assert.Contains("Create a new snapshot", error.Message);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "restores")));
    }

    [Fact]
    public async Task NewSnapshot_RestoresSharedPackagedPathForDeduplicatedMedia()
    {
        byte[] content = Encoding.UTF8.GetBytes("shared content");
        string dataDirectory = Path.Combine(_root, "data");
        string filesDirectory = Path.Combine(dataDirectory, "files");
        Directory.CreateDirectory(filesDirectory);
        File.WriteAllBytes(Path.Combine(filesDirectory, "first.txt"), content);
        File.WriteAllBytes(Path.Combine(filesDirectory, "second.txt"), content);
        CreateDatabase(Path.Combine(dataDirectory, "localhistory.db"), ["media/first.txt", "media/second.txt"]);
        using var liveConnection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "localhistory.db")}");
        liveConnection.Open();
        using (var journalCommand = liveConnection.CreateCommand())
        {
            journalCommand.CommandText = "PRAGMA journal_mode=WAL;";
            Assert.Equal("wal", journalCommand.ExecuteScalar());
        }

        string packagedPath = $"media/{Hash(content)}_first.txt";
        var snapshotService = new ConversationSnapshotService(dataDirectory);
        ConversationSnapshotResult snapshot = await snapshotService.CreateAsync(_ => { });
        using (var archive = ZipFile.OpenRead(snapshot.ArchivePath))
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("-wal") || entry.FullName.EndsWith("-shm"));
        var service = new IsolatedSnapshotRestoreService(Path.Combine(_root, "restores"));

        IsolatedSnapshotRestoreResult result = await service.RestoreAsync(snapshot.ArchivePath, _ => { });

        Assert.Equal(1, snapshot.MediaFileCount);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(result.FilesDirectory, Path.GetFileName(packagedPath))));
        using var connection = new SqliteConnection($"Data Source={result.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT LocalPath FROM Media ORDER BY Id;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(packagedPath, reader.GetString(0));
        Assert.True(reader.Read());
        Assert.Equal(packagedPath, reader.GetString(0));
        Assert.False(reader.Read());
    }

    private string CreateSnapshot(string[] references)
    {
        byte[] content = Encoding.UTF8.GetBytes("shared content");
        string mediaPath = $"media/{Hash(content)}_first.txt";
        string databasePath = Path.Combine(_root, "source.db");
        CreateDatabase(databasePath, references);

        byte[] database = File.ReadAllBytes(databasePath);
        var manifest = new
        {
            ManifestVersion = 1,
            SnapshotId = "test",
            DatabaseFile = "localhistory.db",
            DatabaseSha256 = Hash(database),
            MediaFiles = new[] { new { ArchivePath = mediaPath, Sha256 = Hash(content), Size = content.Length } }
        };
        string archivePath = Path.Combine(_root, "snapshot.zip");
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        WriteEntry(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
        WriteEntry(zip, "localhistory.db", database);
        WriteEntry(zip, mediaPath, content);
        return archivePath;
    }

    private static void CreateDatabase(string databasePath, string[] references)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Media (Id INTEGER PRIMARY KEY, LocalPath TEXT, MediaType TEXT);";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO Media (LocalPath, MediaType) VALUES ($path, 'text/plain');";
        var path = command.Parameters.Add("$path", SqliteType.Text);
        foreach (string reference in references)
        {
            path.Value = reference;
            command.ExecuteNonQuery();
        }
    }

    private static void WriteEntry(ZipArchive zip, string path, byte[] bytes)
    {
        using var stream = zip.CreateEntry(path).Open();
        stream.Write(bytes);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

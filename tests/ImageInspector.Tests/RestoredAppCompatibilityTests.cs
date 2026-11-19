using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using oaiProtonSync.Services;
using oaiResponsesAvalonia.Data;

namespace ImageInspector.Tests;

public sealed class RestoredAppCompatibilityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public async Task SnapshotAndRestore_CanBeReadUsingTheMainApplicationModelAndMediaResolver()
    {
        var (restore, liveDb) = await CreateRestoredSnapshotAsync();
        var result = await new RestoredAppCompatibilityService().CheckAsync(restore);

        Assert.Equal(1, result.Sessions);
        Assert.Equal(2, result.Messages);
        Assert.Equal(3, result.MediaReferences);
        Assert.Equal(2, result.UniqueMediaFiles);
        await using var restored = CreateContext(restore.DatabasePath, readOnly: true);
        var sessions = await restored.Sessions.Include(s => s.Messages).ThenInclude(m => m.MediaFiles).ToListAsync();
        Assert.Equal("A test conversation", Assert.Single(sessions).Title);
        Assert.Contains(sessions[0].Messages, m => m.Content == "Message with attachments");
        Assert.Equal(2, sessions[0].Messages.Single(m => m.Role == "user").MediaFiles.Select(m => m.LocalPath).Distinct().Count());

        // The source data still has original references, not the hash-prefixed paths in the restore.
        await using var live = CreateContext(liveDb, readOnly: true);
        var sourcePaths = await live.Media.OrderBy(m => m.Id).Select(m => m.LocalPath).ToListAsync();
        Assert.Equal(new[] { "media/first.txt", "media/second.txt", "media/picture.png" }, sourcePaths);
    }

    [Fact]
    public async Task RestorePreview_ReadsCurrentCountsWithoutChangingEitherDatabase()
    {
        var (restore, livePath) = await CreateRestoredSnapshotAsync();
        await using (var live = CreateContext(livePath))
        {
            var session = await live.Sessions.Include(s => s.Messages).SingleAsync();
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Newer live message" });
            await live.SaveChangesAsync();
        }

        var preflight = new RestorePreflightService();
        var liveCounts = await preflight.ReadCountsAsync(livePath);
        var snapshotCounts = await preflight.ReadCountsAsync(restore.DatabasePath);
        Assert.Equal(new ConversationCounts(1, 3, 3), liveCounts);
        Assert.Equal(new ConversationCounts(1, 2, 3), snapshotCounts);
        await using var check = CreateContext(livePath, readOnly: true);
        Assert.Equal(3, await check.Messages.CountAsync());
    }

    [Fact]
    public async Task RestorePreview_MissingDatabaseIsNotCreated()
    {
        string path = Path.Combine(_root, "missing.db");
        await Assert.ThrowsAsync<FileNotFoundException>(() => new RestorePreflightService().ReadCountsAsync(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CompatibilityCheck_FailsWhenRestoredMediaIsMissing()
    {
        var (restore, _) = await CreateRestoredSnapshotAsync();
        File.Delete(Assert.Single(Directory.GetFiles(restore.FilesDirectory)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new RestoredAppCompatibilityService().CheckAsync(restore));
        Assert.Contains("could not find", error.Message);
    }

    [Fact]
    public async Task CompatibilityCheck_RejectsNonPortableReferencesInsteadOfReadingLiveFiles()
    {
        var (restore, _) = await CreateRestoredSnapshotAsync();
        await using (var db = CreateContext(restore.DatabasePath))
        {
            var media = await db.Media.FirstAsync();
            media.LocalPath = Path.Combine(_root, "outside.txt");
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new RestoredAppCompatibilityService().CheckAsync(restore));
        Assert.Contains("not a portable media path", error.Message);
    }

    private async Task<(IsolatedSnapshotRestoreResult Restore, string LiveDb)> CreateRestoredSnapshotAsync()
    {
        string data = Path.Combine(_root, "source");
        string files = Path.Combine(data, "files");
        string images = Path.Combine(data, "images");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(images);
        await File.WriteAllTextAsync(Path.Combine(files, "first.txt"), "same file content");
        await File.WriteAllTextAsync(Path.Combine(files, "second.txt"), "same file content");
        await File.WriteAllBytesAsync(Path.Combine(images, "picture.png"), [1, 2, 3, 4]);
        string dbPath = Path.Combine(data, "localhistory.db");
        await using (var db = CreateContext(dbPath))
        {
            await db.Database.EnsureCreatedAsync();
            var session = new ChatSession(EndpointType.Responses, "Responses", "A test conversation");
            var user = new ChatMessage { Role = "user", Content = "Message with attachments" };
            user.MediaFiles.Add(new MediaFile { LocalPath = "media/first.txt", MediaType = "text/plain" });
            user.MediaFiles.Add(new MediaFile { LocalPath = "media/second.txt", MediaType = "text/plain" });
            user.MediaFiles.Add(new MediaFile { LocalPath = "media/picture.png", MediaType = "image/png" });
            session.Messages.Add(user);
            session.Messages.Add(new ChatMessage { Role = "assistant", Content = "A reply" });
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
        }
        var snapshot = await new ConversationSnapshotService(data).CreateAsync(_ => { });
        var restore = await new IsolatedSnapshotRestoreService(Path.Combine(_root, "restores")).RestoreAsync(snapshot.ArchivePath, _ => { });
        return (restore, dbPath);
    }

    private static AppDbContext CreateContext(string path, bool readOnly = false)
    {
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection.ToString()).Options);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

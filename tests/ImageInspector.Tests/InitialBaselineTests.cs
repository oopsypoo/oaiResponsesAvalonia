using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using oaiProtonSync.Services;
using oaiResponsesAvalonia.Data;
using oaiResponsesAvalonia.Services;

namespace ImageInspector.Tests;

public sealed class InitialBaselineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public async Task IndependentSnapshotsCreateDisposableBaselineWithIdentitiesAndMedia()
    {
        var first = await CreateSnapshotAsync("first", "Linux history", "Linux message", "one");
        var second = await CreateSnapshotAsync("second", "Windows history", "Windows message", "two");
        byte[] beforeFirst = await File.ReadAllBytesAsync(first);
        byte[] beforeSecond = await File.ReadAllBytesAsync(second);
        var result = await new InitialBaselineService(Path.Combine(_root, "BaselineTests"))
            .CreateAsync(first, second, _ => { });
        Assert.Equal(2, result.Sessions);
        Assert.Equal(2, result.Messages);
        Assert.Equal(2, result.MediaReferences);
        Assert.Equal(beforeFirst, await File.ReadAllBytesAsync(first));
        Assert.Equal(beforeSecond, await File.ReadAllBytesAsync(second));
        Assert.False(File.Exists(Path.Combine(result.Directory, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "experimental-baseline.json")));
        string visualCopy = IsolatedVisualTestService.CreateCopy(result.Directory,
            Path.Combine(_root, "RestoreTests"), Path.Combine(_root, "VisualTests"));
        Assert.True(File.Exists(Path.Combine(visualCopy, "localhistory.db")));
        string database = Path.Combine(result.Directory, "localhistory.db");
        await using var db = Context(database, true);
        var sessions = await db.Sessions.Include(s => s.Messages).ThenInclude(m => m.MediaFiles).ToListAsync();
        Assert.Equal(new[] { "Linux history", "Windows history" }, sessions.Select(s => s.Title).Order().ToArray());
        Assert.All(sessions, s => Assert.Single(Assert.Single(s.Messages).MediaFiles));
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COUNT(DISTINCT SyncId) FROM ExperimentalSessionIdentity";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal(2, reader.GetInt32(1));
        }
        command.CommandText = "SELECT COUNT(*), COUNT(DISTINCT SyncId) FROM ExperimentalMessageIdentity";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal(2, reader.GetInt32(1));
        }
    }

    [Fact]
    public async Task BaselineCopiesPreserveIdsAndAssignDistinctIdsToNewRecords()
    {
        string first = await CreateSnapshotAsync("first", "Linux history", "Linux message", "one");
        string second = await CreateSnapshotAsync("second", "Windows history", "Windows message", "two");
        var baseline = await new InitialBaselineService(Path.Combine(_root, "baselines"))
            .CreateAsync(first, second, _ => { });
        string original = Path.Combine(baseline.Directory, "localhistory.db");
        string a = Path.Combine(_root, "copy-a.db");
        string b = Path.Combine(_root, "copy-b.db");
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = original, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            source.Open();
            foreach (string path in new[] { a, b })
            {
                using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = path, Pooling = false }.ToString());
                destination.Open();
                source.BackupDatabase(destination);
            }
        }

        var existingA = ReadIdentities(a, "ExperimentalMessageIdentity", "MessageId");
        var existingB = ReadIdentities(b, "ExperimentalMessageIdentity", "MessageId");
        Assert.Equal(existingA.OrderBy(x => x.Key), existingB.OrderBy(x => x.Key));
        Assert.All(existingA.Values, id => Assert.True(Guid.TryParse(id, out _)));

        async Task<(int SessionId, int MessageId)> AddAsync(string path, string content)
        {
            await using var db = Context(path);
            var session = new ChatSession(EndpointType.Responses, "Responses", content);
            var message = new ChatMessage { Role = "user", Content = content };
            session.Messages.Add(message);
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
            return (session.Id, message.Id);
        }
        var addedA = await AddAsync(a, "Added on A");
        var addedB = await AddAsync(b, "Added on B");
        string sessionA = ReadIdentities(a, "ExperimentalSessionIdentity", "SessionId")[addedA.SessionId];
        string sessionB = ReadIdentities(b, "ExperimentalSessionIdentity", "SessionId")[addedB.SessionId];
        string messageA = ReadIdentities(a, "ExperimentalMessageIdentity", "MessageId")[addedA.MessageId];
        string messageB = ReadIdentities(b, "ExperimentalMessageIdentity", "MessageId")[addedB.MessageId];
        Assert.NotEqual(sessionA, sessionB);
        Assert.NotEqual(messageA, messageB);
        Assert.True(Guid.TryParse(sessionA, out _));
        Assert.True(Guid.TryParse(messageA, out _));
        Assert.Equal(existingA, ReadIdentities(original, "ExperimentalMessageIdentity", "MessageId"));

        await using var dbA = Context(a);
        var sessionToDelete = await dbA.Sessions.FindAsync(addedA.SessionId);
        dbA.Sessions.Remove(sessionToDelete!);
        await Assert.ThrowsAsync<DbUpdateException>(() => dbA.SaveChangesAsync());
    }

    [Fact]
    public async Task IdentityComparisonFindsIndependentAdditionsAndChangesWithoutWritingInputs()
    {
        string first = await CreateSnapshotAsync("first", "Linux history", "Linux message", "one");
        string second = await CreateSnapshotAsync("second", "Windows history", "Windows message", "two");
        var baseline = await new InitialBaselineService(Path.Combine(_root, "baselines"))
            .CreateAsync(first, second, _ => { });
        string aDir = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        string bDir = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;
        string a = Path.Combine(aDir, "localhistory.db"), b = Path.Combine(bDir, "localhistory.db");
        File.Copy(Path.Combine(baseline.Directory, "experimental-baseline.json"), Path.Combine(aDir, "experimental-baseline.json"));
        File.Copy(Path.Combine(baseline.Directory, "experimental-baseline.json"), Path.Combine(bDir, "experimental-baseline.json"));
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = Path.Combine(baseline.Directory, "localhistory.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            source.Open();
            foreach (var path in new[] { a, b })
            {
                using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                destination.Open();
                source.BackupDatabase(destination);
            }
        }
        var service = new BaselineIdentityComparisonService();
        var identical = await service.CompareAsync(aDir, bDir);
        Assert.Equal(2, identical.SharedSessions);
        Assert.Equal(2, identical.SharedMessages);
        Assert.Empty(identical.MessagesOnlyA);
        Assert.Empty(identical.DifferentMessages);
        foreach (var (path, text) in new[] { (a, "added A"), (b, "added B") })
        {
            await using var db = Context(path);
            var existing = await db.Sessions.OrderBy(s => s.Id).FirstAsync();
            db.Messages.Add(new ChatMessage { ChatSessionId = existing.Id, Role = "user", Content = text });
            if (path == b) (await db.Messages.OrderBy(m => m.Id).FirstAsync()).Content = "changed B";
            await db.SaveChangesAsync();
        }
        var beforeA = await File.ReadAllBytesAsync(a);
        var beforeB = await File.ReadAllBytesAsync(b);
        var result = await service.CompareAsync(aDir, bDir);
        Assert.Equal(2, result.SharedSessions);
        Assert.Equal(2, result.SharedMessages);
        Assert.Single(result.MessagesOnlyA);
        Assert.Single(result.MessagesOnlyB);
        Assert.NotEqual(result.MessagesOnlyA[0].SyncId, result.MessagesOnlyB[0].SyncId);
        Assert.Single(result.DifferentMessages);
        Assert.Equal(beforeA, await File.ReadAllBytesAsync(a));
        Assert.Equal(beforeB, await File.ReadAllBytesAsync(b));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CompareAsync(_root, bDir));
        using (var corrupt = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = b, Pooling = false }.ToString()))
        {
            corrupt.Open();
            using var command = corrupt.CreateCommand();
            command.CommandText = "DELETE FROM ExperimentalMessageIdentity WHERE MessageId = (SELECT MIN(MessageId) FROM ExperimentalMessageIdentity)";
            command.ExecuteNonQuery();
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.CompareAsync(aDir, bDir));
        Assert.Contains("identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangeDemonstrationDetectsChangesAndLeavesBaselineUntouched()
    {
        string first = await CreateSnapshotAsync("demo-first", "Linux history", "Linux message", "one");
        string second = await CreateSnapshotAsync("demo-second", "Windows history", "Windows message", "two");
        var baseline = await new InitialBaselineService(Path.Combine(_root, "demo-baselines"))
            .CreateAsync(first, second, _ => { });
        string database = Path.Combine(baseline.Directory, "localhistory.db");
        byte[] before = await File.ReadAllBytesAsync(database);
        var log = new List<string>();
        var result = await new BaselineChangeDemonstrationService().RunAsync(baseline.Directory, log.Add);
        Assert.Equal(2, result.SharedSessions);
        Assert.Equal(2, result.SharedMessages);
        Assert.Single(result.MessagesOnlyA);
        Assert.Single(result.MessagesOnlyB);
        Assert.Single(result.DifferentMessages);
        Assert.Equal(before, await File.ReadAllBytesAsync(database));
        Assert.Contains(log, line => line.Contains("Temporary demonstration copies removed"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new BaselineChangeDemonstrationService().RunAsync(_root, _ => { }));
    }

    [Fact]
    public async Task ThreeWayRehearsalRetainsConflictsAndIsRepeatableWithoutChangingSource()
    {
        string first = await CreateSnapshotAsync("merge-first", "Linux history", "Linux message", "one");
        string second = await CreateSnapshotAsync("merge-second", "Windows history", "Windows message", "two");
        var baseline = await new InitialBaselineService(Path.Combine(_root, "baselines"))
            .CreateAsync(first, second, _ => { });
        string original = Path.Combine(baseline.Directory, "localhistory.db");
        byte[] before = await File.ReadAllBytesAsync(original);
        var result = await new DisposableThreeWayMergeDemoService(Path.Combine(_root, "BaselineTests"))
            .RunAsync(baseline.Directory, _ => { });
        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Edited);
        Assert.Equal(1, result.Conflicts);
        Assert.Equal(0, result.RepeatedChanges);
        Assert.Equal(6, result.Sessions);
        Assert.Equal(6, result.Messages);
        Assert.Equal(before, await File.ReadAllBytesAsync(original));
        string visual = IsolatedVisualTestService.CreateCopy(result.ResultDirectory,
            Path.Combine(_root, "RestoreTests"), Path.Combine(_root, "VisualTests"));
        Assert.True(File.Exists(Path.Combine(visual, "localhistory.db")));
        await using var output = Context(Path.Combine(result.ResultDirectory, "localhistory.db"), true);
        var contents = await output.Messages.Select(m => m.Content).ToListAsync();
        Assert.Contains("Rehearsal: new message from A", contents);
        Assert.Contains("Rehearsal: new message from B", contents);
        Assert.Contains("Rehearsal: edited only on A", contents);
        Assert.Contains("Rehearsal: original conflicting edit", contents);
        Assert.DoesNotContain("Rehearsal: conflicting edit on A", contents);
        Assert.DoesNotContain("Rehearsal: conflicting edit on B", contents);
        await using var branchA = Context(Path.Combine(result.Workspace, "branch-A", "localhistory.db"), true);
        await using var branchB = Context(Path.Combine(result.Workspace, "branch-B", "localhistory.db"), true);
        Assert.True(await branchA.Messages.AnyAsync(m => m.Content == "Rehearsal: conflicting edit on A"));
        Assert.True(await branchB.Messages.AnyAsync(m => m.Content == "Rehearsal: conflicting edit on B"));
    }

    private static Dictionary<int, string> ReadIdentities(string path, string table, string idColumn)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {idColumn}, SyncId FROM {table}";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<int, string>();
        while (reader.Read()) result.Add(reader.GetInt32(0), reader.GetString(1));
        return result;
    }

    [Fact]
    public async Task MatchingCandidatesAreRejectedAndIncompleteWorkspaceIsRemoved()
    {
        var first = await CreateSnapshotAsync("first", "Same title", "first message", "one");
        var second = await CreateSnapshotAsync("second", "Same title", "second message", "two");
        string output = Path.Combine(_root, "baselines");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new InitialBaselineService(output).CreateAsync(first, second, _ => { }));
        Assert.Contains("Possible shared sessions", error.Message);
        Assert.Empty(Directory.GetDirectories(output));
    }

    [Fact]
    public async Task SameSnapshotIsRejected()
    {
        var first = await CreateSnapshotAsync("first", "History", "message", "one");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new InitialBaselineService(Path.Combine(_root, "baselines")).CreateAsync(first, first, _ => { }));
    }

    private async Task<string> CreateSnapshotAsync(string name, string title, string text, string fileContent)
    {
        string data = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(data, "files"));
        Directory.CreateDirectory(Path.Combine(data, "images"));
        await File.WriteAllTextAsync(Path.Combine(data, "files", "attachment.txt"), fileContent);
        await using (var db = Context(Path.Combine(data, "localhistory.db")))
        {
            await db.Database.EnsureCreatedAsync();
            var session = new ChatSession(EndpointType.Responses, "Responses", title);
            var message = new ChatMessage { Role = "user", Content = text };
            message.MediaFiles.Add(new MediaFile { LocalPath = "media/attachment.txt", MediaType = "text/plain" });
            session.Messages.Add(message);
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
        }
        return (await new ConversationSnapshotService(data).CreateAsync(_ => { })).ArchivePath;
    }

    private static AppDbContext Context(string path, bool readOnly = false) => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()).Options);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

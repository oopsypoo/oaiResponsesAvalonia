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
    public async Task RealHistoryWorkflowsAssignIdsAndBlockedDeletesPreserveMedia()
    {
        string first = await CreateSnapshotAsync("workflow-first", "Linux history", "Linux message", "one");
        string second = await CreateSnapshotAsync("workflow-second", "Windows history", "Windows message", "two");
        var baseline = await new InitialBaselineService(Path.Combine(_root, "workflow-baselines"))
            .CreateAsync(first, second, _ => { });
        string source = Path.Combine(baseline.Directory, "localhistory.db");
        string aDir = Directory.CreateDirectory(Path.Combine(_root, "workflow-a")).FullName;
        string bDir = Directory.CreateDirectory(Path.Combine(_root, "workflow-b")).FullName;
        using (var input = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            input.Open();
            foreach (string dir in new[] { aDir, bDir })
            {
                using var output = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = Path.Combine(dir, "localhistory.db"), Pooling = false }.ToString());
                output.Open();
                input.BackupDatabase(output);
                File.Copy(Path.Combine(baseline.Directory, "experimental-baseline.json"),
                    Path.Combine(dir, "experimental-baseline.json"));
            }
        }

        async Task<(int Session, int Message, string File, SessionCleanupService Cleanup)> ChangeAsync(string dir)
        {
            string database = Path.Combine(dir, "localhistory.db");
            string images = Directory.CreateDirectory(Path.Combine(dir, "images")).FullName;
            string files = Directory.CreateDirectory(Path.Combine(dir, "files")).FullName;
            var media = new MediaStorageService();
            media.SetStorageFolders(images, files);
            var history = new HistoryService(media, () => Context(database));
            int session = await history.StartNewSessionAsync("Disposable workflow", EndpointType.Responses);
            int message = await history.AddMessageAsync(session, "user", "Disposable addition");
            string path = Path.Combine(files, "workflow.txt");
            await File.WriteAllTextAsync(path, "keep this file if deletion fails");
            Assert.Equal(path, media.ResolveMediaPath("media/workflow.txt"));
            await history.LinkMediaAsync(message, "media/workflow.txt", "text/plain");
            return (session, message, path, new SessionCleanupService(history, media));
        }

        var a = await ChangeAsync(aDir);
        var b = await ChangeAsync(bDir);
        string aDb = Path.Combine(aDir, "localhistory.db");
        string bDb = Path.Combine(bDir, "localhistory.db");
        Assert.NotEqual(ReadIdentities(aDb, "ExperimentalSessionIdentity", "SessionId")[a.Session],
            ReadIdentities(bDb, "ExperimentalSessionIdentity", "SessionId")[b.Session]);
        Assert.NotEqual(ReadIdentities(aDb, "ExperimentalMessageIdentity", "MessageId")[a.Message],
            ReadIdentities(bDb, "ExperimentalMessageIdentity", "MessageId")[b.Message]);
        var comparison = await new BaselineIdentityComparisonService().CompareAsync(aDir, bDir);
        Assert.Single(comparison.SessionsOnlyA);
        Assert.Single(comparison.SessionsOnlyB);
        Assert.Single(comparison.MessagesOnlyA);
        Assert.Single(comparison.MessagesOnlyB);

        // Existing baseline triggers reject deletion; the real cleanup workflow must
        // not delete attachments before the database has accepted that deletion.
        await Assert.ThrowsAsync<DbUpdateException>(() => a.Cleanup.DeleteSessionAsync(a.Session));
        await Assert.ThrowsAsync<DbUpdateException>(() => b.Cleanup.DeleteTurnAsync(b.Message));
        Assert.True(File.Exists(a.File));
        Assert.True(File.Exists(b.File));
        await using var aContext = Context(aDb, true);
        await using var bContext = Context(bDb, true);
        Assert.True(await aContext.Messages.AnyAsync(m => m.Id == a.Message));
        Assert.True(await bContext.Messages.AnyAsync(m => m.Id == b.Message));
    }

    [Fact]
    public async Task ActualCleanupOnDisposableRehearsalRecordsCreatesMarkersAndPreservesSharedMedia()
    {
        string first = await CreateSnapshotAsync("cleanup-first", "Linux history", "Linux message", "one");
        string second = await CreateSnapshotAsync("cleanup-second", "Windows history", "Windows message", "two");
        var baseline = await new InitialBaselineService(Path.Combine(_root, "cleanup-baselines"))
            .CreateAsync(first, second, _ => { });
        string original = Path.Combine(baseline.Directory, "localhistory.db");
        byte[] before = await File.ReadAllBytesAsync(original);
        string work = Directory.CreateDirectory(Path.Combine(_root, "cleanup-disposable")).FullName;
        string database = Path.Combine(work, "localhistory.db");
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = original, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = database, Pooling = false }.ToString()))
        {
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }

        // Setup permits only its synthetic IDs to be deleted; every other delete remains blocked.
        var scenario = await DisposableDeletionRehearsal.SetupAsync(database, CancellationToken.None);
        string images = Directory.CreateDirectory(Path.Combine(work, "images")).FullName;
        string files = Directory.CreateDirectory(Path.Combine(work, "files")).FullName;
        string sharedFile = Path.Combine(files, Path.GetFileName(scenario.SharedMediaPath));
        await File.WriteAllTextAsync(sharedFile, "shared test attachment");
        var media = new MediaStorageService();
        media.SetStorageFolders(images, files);
        var history = new HistoryService(media, () => Context(database));
        var cleanup = new SessionCleanupService(history, media);

        await cleanup.DeleteSessionAsync(scenario.DeleteSession);
        await using (var check = Context(database, true))
        {
            Assert.False(await check.Sessions.AnyAsync(s => s.Id == scenario.DeleteSession));
            Assert.False(await check.Messages.AnyAsync(m => m.Id == scenario.ChildMessage));
            Assert.True(await check.Media.AnyAsync(m => m.ChatMessageId == scenario.KeeperMessage && m.LocalPath == scenario.SharedMediaPath));
        }
        Assert.Equal(2L, await CountTombstonesAsync(database));
        Assert.True(await HasTombstoneAsync(database, "session", scenario.DeleteSessionSyncId));
        Assert.True(await HasTombstoneAsync(database, "message", scenario.ChildMessageSyncId));
        Assert.Equal("shared test attachment", await File.ReadAllTextAsync(sharedFile));

        // Keep the message's parent session non-empty so DeleteTurnAsync does not
        // attempt to delete a non-allowlisted synthetic session.
        int messageSession;
        await using (var check = Context(database, true))
            messageSession = (await check.Messages.SingleAsync(m => m.Id == scenario.DeleteMessage)).ChatSessionId;
        await history.AddMessageAsync(messageSession, "user", "Keep this rehearsal session");
        await history.LinkMediaAsync(scenario.DeleteMessage, scenario.SharedMediaPath, "text/plain");
        Assert.False(await cleanup.DeleteTurnAsync(scenario.DeleteMessage));
        Assert.Equal(3L, await CountTombstonesAsync(database));
        Assert.True(await HasTombstoneAsync(database, "message", scenario.DeleteMessageSyncId));
        Assert.True(File.Exists(sharedFile));
        await using (var check = Context(database, true))
        {
            Assert.False(await check.Messages.AnyAsync(m => m.Id == scenario.DeleteMessage));
            Assert.True(await check.Media.AnyAsync(m => m.ChatMessageId == scenario.KeeperMessage && m.LocalPath == scenario.SharedMediaPath));
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(original));
    }

    [Fact]
    public async Task RealWorkflowsOnTwoCopiesExposeAdditionsAndSyntheticDeletionMarkers()
    {
        string first = await CreateSnapshotAsync("two-copy-first", "Linux history", "Linux message", "one");
        string second = await CreateSnapshotAsync("two-copy-second", "Windows history", "Windows message", "two");
        var baseline = await new InitialBaselineService(Path.Combine(_root, "two-copy-baselines"))
            .CreateAsync(first, second, _ => { });
        string original = Path.Combine(baseline.Directory, "localhistory.db");
        byte[] originalBytes = await File.ReadAllBytesAsync(original);
        string shared = Path.Combine(_root, "two-copy-shared.db");
        string aDir = Directory.CreateDirectory(Path.Combine(_root, "two-copy-a")).FullName;
        string bDir = Directory.CreateDirectory(Path.Combine(_root, "two-copy-b")).FullName;
        string aDb = Path.Combine(aDir, "localhistory.db");
        string bDb = Path.Combine(bDir, "localhistory.db");

        static void Backup(string from, string to)
        {
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = from, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = to, Pooling = false }.ToString());
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }

        Backup(original, shared);
        // Only the explicitly allowlisted synthetic records may be deleted in these copies.
        var scenario = await DisposableDeletionRehearsal.SetupAsync(shared, CancellationToken.None);
        string sharedDir = Directory.CreateDirectory(Path.Combine(_root, "two-copy-common-base")).FullName;
        Backup(shared, Path.Combine(sharedDir, "localhistory.db"));
        File.Copy(Path.Combine(baseline.Directory, "experimental-baseline.json"), Path.Combine(sharedDir, "experimental-baseline.json"));
        await new BaselineIdentityComparisonService().StampRehearsalBaseAsync(sharedDir);
        foreach (string dir in new[] { aDir, bDir })
        {
            Backup(Path.Combine(sharedDir, "localhistory.db"), Path.Combine(dir, "localhistory.db"));
            File.Copy(Path.Combine(baseline.Directory, "experimental-baseline.json"),
                Path.Combine(dir, "experimental-baseline.json"));
            Directory.CreateDirectory(Path.Combine(dir, "images"));
            string files = Directory.CreateDirectory(Path.Combine(dir, "files")).FullName;
            await File.WriteAllTextAsync(Path.Combine(files, Path.GetFileName(scenario.SharedMediaPath)),
                "shared synthetic attachment");
        }

        static (HistoryService History, SessionCleanupService Cleanup) Services(string dir)
        {
            string db = Path.Combine(dir, "localhistory.db");
            var media = new MediaStorageService();
            media.SetStorageFolders(Path.Combine(dir, "images"), Path.Combine(dir, "files"));
            var history = new HistoryService(media, () => Context(db));
            return (history, new SessionCleanupService(history, media));
        }

        var a = Services(aDir);
        var b = Services(bDir);
        int sessionA = await a.History.StartNewSessionAsync("Added on A", EndpointType.Responses);
        int messageA = await a.History.AddMessageAsync(sessionA, "user", "A's new message");
        int sessionB = await b.History.StartNewSessionAsync("Added on B", EndpointType.Responses);
        int messageB = await b.History.AddMessageAsync(sessionB, "user", "B's new message");
        // A deletes a synthetic session; B edits its child independently.
        await a.Cleanup.DeleteSessionAsync(scenario.DeleteSession);
        await b.History.UpdateMessageContentAndRawJsonAsync(scenario.ChildMessage, "Edited on B", "");

        Assert.True(await HasTombstoneAsync(aDb, "session", scenario.DeleteSessionSyncId));
        Assert.True(await HasTombstoneAsync(aDb, "message", scenario.ChildMessageSyncId));
        Assert.Equal(2L, await CountTombstonesAsync(aDb));
        Assert.Equal(0L, await CountTombstonesAsync(bDb));
        Assert.True(File.Exists(Path.Combine(aDir, "files", Path.GetFileName(scenario.SharedMediaPath))));
        await using (var check = Context(aDb, true))
            Assert.True(await check.Media.AnyAsync(m => m.ChatMessageId == scenario.KeeperMessage && m.LocalPath == scenario.SharedMediaPath));
        await using (var check = Context(bDb, true))
            Assert.Equal("Edited on B", (await check.Messages.SingleAsync(m => m.Id == scenario.ChildMessage)).Content);

        var comparison = await new BaselineIdentityComparisonService().CompareAsync(aDir, bDir);
        var sessionIdA = Guid.Parse(ReadIdentities(aDb, "ExperimentalSessionIdentity", "SessionId")[sessionA]);
        var sessionIdB = Guid.Parse(ReadIdentities(bDb, "ExperimentalSessionIdentity", "SessionId")[sessionB]);
        var messageIdA = Guid.Parse(ReadIdentities(aDb, "ExperimentalMessageIdentity", "MessageId")[messageA]);
        var messageIdB = Guid.Parse(ReadIdentities(bDb, "ExperimentalMessageIdentity", "MessageId")[messageB]);
        Assert.NotEqual(sessionIdA, sessionIdB);
        Assert.NotEqual(messageIdA, messageIdB);
        Assert.Equal(new[] { sessionIdA }, comparison.SessionsOnlyA.Select(x => x.SyncId));
        Assert.Equal(new[] { messageIdA }, comparison.MessagesOnlyA.Select(x => x.SyncId));
        Assert.Equal(new[] { sessionIdB, Guid.Parse(scenario.DeleteSessionSyncId) }.Order(), comparison.SessionsOnlyB.Select(x => x.SyncId).Order());
        Assert.Equal(new[] { messageIdB, Guid.Parse(scenario.ChildMessageSyncId) }.Order(), comparison.MessagesOnlyB.Select(x => x.SyncId).Order());
        var comparer = new BaselineIdentityComparisonService();
        var threeWay = await comparer.PreviewThreeWayAsync(sharedDir, aDir, bDir);
        Assert.Contains(threeWay.Findings, x => x.Kind == "session" && x.SyncId == sessionIdA && x.Classification == "Added on A");
        Assert.Contains(threeWay.Findings, x => x.Kind == "session" && x.SyncId == sessionIdB && x.Classification == "Added on B");
        Assert.Contains(threeWay.Findings, x => x.Kind == "message" && x.SyncId == messageIdA && x.Classification == "Added on A");
        Assert.Contains(threeWay.Findings, x => x.Kind == "message" && x.SyncId == messageIdB && x.Classification == "Added on B");
        Assert.Contains(threeWay.Findings, x => x.Kind == "message" && x.SyncId == Guid.Parse(scenario.ChildMessageSyncId) && x.Classification == "Delete-versus-edit conflict");
        await Assert.ThrowsAsync<InvalidDataException>(() => comparer.PreviewThreeWayAsync(baseline.Directory, aDir, bDir));
        var preview = await comparer.CompareRehearsalDeletionsAsync(sharedDir, aDir, bDir);
        Assert.Contains(preview.Findings, x => x.Kind == "message" && x.SyncId == Guid.Parse(scenario.ChildMessageSyncId) && x.Classification == "Delete-versus-edit conflict");
        Assert.Contains(preview.Findings, x => x.Kind == "session" && x.SyncId == Guid.Parse(scenario.DeleteSessionSyncId) && x.Classification == "Delete-versus-edit conflict");
        await Assert.ThrowsAsync<InvalidDataException>(() => comparer.CompareRehearsalDeletionsAsync(baseline.Directory, aDir, bDir));
        // Missing markers are unknown, not evidence that a deletion should be applied.
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = aDb, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ExperimentalRehearsalTombstone WHERE Kind='message' AND SyncId=$id";
            command.Parameters.AddWithValue("$id", scenario.ChildMessageSyncId);
            await command.ExecuteNonQueryAsync();
        }
        var missing = await comparer.CompareRehearsalDeletionsAsync(sharedDir, aDir, bDir);
        Assert.Contains(missing.Findings, x => x.Kind == "message" && x.SyncId == Guid.Parse(scenario.ChildMessageSyncId) && x.Classification == "Missing without deletion marker; unknown");
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(original));
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
        Assert.Equal(9, result.Sessions);
        Assert.Equal(8, result.Messages);
        Assert.Equal(2, result.Deletions);
        Assert.Equal(1, result.DeleteConflicts);
        Assert.Equal(0, result.RepeatedDeletions);
        var threeWay = await new BaselineIdentityComparisonService().PreviewThreeWayAsync(
            Path.Combine(result.Workspace, "shared-base"), Path.Combine(result.Workspace, "branch-A"), Path.Combine(result.Workspace, "branch-B"));
        Assert.Equal(1, threeWay.Findings.Count(f => f.Kind == "message" && f.Classification == "Added on A"));
        Assert.Equal(1, threeWay.Findings.Count(f => f.Kind == "message" && f.Classification == "Added on B"));
        Assert.Equal(1, threeWay.Findings.Count(f => f.Kind == "message" && f.Classification == "Edited on A; B unchanged"));
        Assert.Equal(1, threeWay.Findings.Count(f => f.Kind == "message" && f.Classification == "Different edits; conflict"));
        Assert.Contains(threeWay.Findings, f => f.Classification == "Delete-versus-edit conflict");

        var comparer = new BaselineIdentityComparisonService();
        string baseDir = Path.Combine(result.Workspace, "shared-base");
        string branchADir = Path.Combine(result.Workspace, "branch-A");
        string branchBDir = Path.Combine(result.Workspace, "branch-B");
        // Branch A has the same metadata but is not the unchanged starting copy.
        var changedBase = await Assert.ThrowsAsync<InvalidDataException>(() => comparer.PreviewThreeWayAsync(branchADir, branchADir, branchBDir));
        Assert.Contains("shared starting copy has changed", changedBase.Message);
        var legacy = await Assert.ThrowsAsync<InvalidDataException>(() => comparer.PreviewThreeWayAsync(baseline.Directory, branchADir, branchBDir));
        Assert.Contains("metadata is missing", legacy.Message);
        var other = await new DisposableThreeWayMergeDemoService(Path.Combine(_root, "BaselineTests"))
            .RunAsync(baseline.Directory, _ => { });
        var mismatch = await Assert.ThrowsAsync<InvalidDataException>(() => comparer.PreviewThreeWayAsync(
            baseDir, branchADir, Path.Combine(other.Workspace, "branch-B")));
        Assert.Contains("different rehearsal baseline metadata", mismatch.Message);
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
        Assert.DoesNotContain("Rehearsal: delete this message", contents);
        Assert.Contains("Rehearsal: original delete-versus-edit message", contents);
        Assert.False(await output.Sessions.AnyAsync(s => s.Title == "[MERGE REHEARSAL] Session deletion"));
        Assert.Equal(3L, await CountTombstonesAsync(Path.Combine(result.ResultDirectory, "localhistory.db")));
        Assert.DoesNotContain("Rehearsal: child of deleted session", contents);
        var keeper = await output.Messages.SingleAsync(m => m.Content == "Rehearsal: still references shared media");
        var reference = await output.Media.SingleAsync(m => m.ChatMessageId == keeper.Id);
        Assert.Equal("text/plain", reference.MediaType);
        string sharedFile = Path.Combine(result.ResultDirectory, "files", Path.GetFileName(reference.LocalPath));
        Assert.Equal("Synthetic shared media for deletion rehearsal.", await File.ReadAllTextAsync(sharedFile));
        await using var branchA = Context(Path.Combine(result.Workspace, "branch-A", "localhistory.db"), true);
        await using var branchB = Context(Path.Combine(result.Workspace, "branch-B", "localhistory.db"), true);
        Assert.True(await branchA.Messages.AnyAsync(m => m.Content == "Rehearsal: conflicting edit on A"));
        Assert.True(await branchB.Messages.AnyAsync(m => m.Content == "Rehearsal: conflicting edit on B"));
        Assert.False(await branchA.Messages.AnyAsync(m => m.Content == "Rehearsal: original delete-versus-edit message"));
        Assert.True(await branchB.Messages.AnyAsync(m => m.Content == "Rehearsal: edited on B while A deleted"));
        Assert.True(await branchB.Messages.AnyAsync(m => m.Content == "Rehearsal: child of deleted session"));
        Assert.True(await branchB.Media.AnyAsync(m => m.LocalPath == reference.LocalPath));
        Assert.Equal(4L, await CountTombstonesAsync(Path.Combine(result.Workspace, "branch-A", "localhistory.db")));
        Assert.Equal(0L, await CountTombstonesAsync(Path.Combine(result.Workspace, "branch-B", "localhistory.db")));
    }

    private static async Task<long> CountTombstonesAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ExperimentalRehearsalTombstone";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> HasTombstoneAsync(string path, string kind, string syncId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ExperimentalRehearsalTombstone WHERE Kind = $kind AND SyncId = $id";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", syncId);
        return (long)(await command.ExecuteScalarAsync())! == 1;
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

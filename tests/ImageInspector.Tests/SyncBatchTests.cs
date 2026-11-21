using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using oaiProtonSync.Services;
using oaiResponsesAvalonia.Data;

namespace ImageInspector.Tests;

public sealed class SyncBatchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sync-batch-test-").FullName;

    [Fact]
    public async Task InvalidSnapshotIsReportedAndInputIsNotChanged()
    {
        string input = Directory.CreateDirectory(Path.Combine(_root, "input")).FullName;
        string output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        string archive = Path.Combine(input, "bad.zip");
        await File.WriteAllTextAsync(archive, "not a snapshot");

        Assert.Equal(1, await SyncBatch.RunAsync([input, output]));
        Assert.Equal("not a snapshot", await File.ReadAllTextAsync(archive));
        string run = Assert.Single(Directory.GetDirectories(output));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "report.json")));
        var findings = report.RootElement.GetProperty("Findings").EnumerateArray().ToArray();
        Assert.Contains(findings, f => f.GetProperty("Name").GetString() == "Snapshot: bad.zip" &&
            f.GetProperty("Status").GetString() == "FAIL");
        Assert.Contains(findings, f => f.GetProperty("Name").GetString() == "Synthetic initial baseline and merge rehearsal" &&
            f.GetProperty("Status").GetString() == "PASS");
    }

    [Fact]
    public async Task NestedOutputIsRejectedWithoutWriting()
    {
        string input = Directory.CreateDirectory(Path.Combine(_root, "input")).FullName;
        string output = Directory.CreateDirectory(Path.Combine(input, "output")).FullName;
        Assert.Equal(2, await SyncBatch.RunAsync([input, output]));
        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public async Task MatchingSnapshotsAreMarkedForReview()
    {
        string input = Directory.CreateDirectory(Path.Combine(_root, "input")).FullName;
        string output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        string data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(data, "localhistory.db")}").Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var session = new ChatSession(EndpointType.Responses, "Responses", "A matching title");
            session.Messages.Add(new ChatMessage { Role = "user", Content = "Example" });
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
        }
        var snapshot = await new ConversationSnapshotService(data).CreateAsync(_ => { });
        File.Copy(snapshot.ArchivePath, Path.Combine(input, "first.zip"));
        File.Copy(snapshot.ArchivePath, Path.Combine(input, "second.zip"));

        Assert.Equal(0, await SyncBatch.RunAsync([input, output]));
        string run = Assert.Single(Directory.GetDirectories(output));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "report.json")));
        Assert.Contains(report.RootElement.GetProperty("Findings").EnumerateArray(),
            f => f.GetProperty("Name").GetString()!.StartsWith("Compare:") &&
                 f.GetProperty("Status").GetString() == "REVIEW");
        using var details = JsonDocument.Parse(await File.ReadAllTextAsync(
            Assert.Single(Directory.GetFiles(Path.Combine(run, "comparisons")))));
        Assert.Single(details.RootElement.GetProperty("Candidates").EnumerateArray());
    }

    [Fact]
    public async Task BaselineBatchRunsOnCopyAndPreservesOriginal()
    {
        string fixtures = Directory.CreateDirectory(Path.Combine(_root, "fixtures")).FullName;
        async Task<string> Snapshot(string name)
        {
            string data = Directory.CreateDirectory(Path.Combine(fixtures, name)).FullName;
            Directory.CreateDirectory(Path.Combine(data, "files"));
            Directory.CreateDirectory(Path.Combine(data, "images"));
            string attachment = Path.Combine(data, "files", $"{name}.txt");
            await File.WriteAllTextAsync(attachment, name);
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(data, "localhistory.db")}").Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var session = new ChatSession(EndpointType.Responses, "Responses", name);
                var message = new ChatMessage { Role = "user", Content = name };
                message.MediaFiles.Add(new MediaFile { LocalPath = attachment, MediaType = "text/plain" });
                session.Messages.Add(message);
                db.Sessions.Add(session);
                await db.SaveChangesAsync();
            }
            return (await new ConversationSnapshotService(data).CreateAsync(_ => { })).ArchivePath;
        }
        string first = await Snapshot("first");
        string second = await Snapshot("second");
        var baseline = await new InitialBaselineService(Path.Combine(fixtures, "baselines"))
            .CreateAsync(first, second, _ => { });
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(baseline.Directory, "localhistory.db"));
        string output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;

        Assert.Equal(0, await SyncBatch.RunAsync(["--baseline", baseline.Directory, output]));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(baseline.Directory, "localhistory.db")));
        string run = Assert.Single(Directory.GetDirectories(output));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "report.json")));
        var findings = report.RootElement.GetProperty("Findings").EnumerateArray().ToArray();
        Assert.Contains(findings, f => f.GetProperty("Name").GetString() == "Controlled merge on candidate copy" &&
            f.GetProperty("Status").GetString() == "PASS");
        Assert.Contains(findings, f => f.GetProperty("Name").GetString() == "Real synchronization" &&
            f.GetProperty("Status").GetString() == "NOT TESTED");
    }

    [Fact]
    public async Task BaselineBatchRejectsAnUnmarkedDirectory()
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        string output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        Assert.Equal(2, await SyncBatch.RunAsync(["--baseline", source, output]));
        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

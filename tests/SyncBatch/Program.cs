using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using oaiProtonSync.Services;
using oaiResponsesAvalonia.Data;

return await SyncBatch.RunAsync(args);

public static class SyncBatch
{
    private sealed record Finding(string Name, string Status, string Detail);
    private sealed record Input(string Original, string Copy, string Hash);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 3 && args[0] == "--baseline")
            return await BaselineBatch.RunAsync(args[1], args[2]);
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: SyncBatch <snapshot-directory> <existing-output-directory>");
            Console.Error.WriteLine("       SyncBatch --baseline <experimental-baseline-directory> <existing-output-directory>");
            return 2;
        }

        string snapshotDir;
        string outputRoot;
        string[] archives;
        try
        {
            snapshotDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[0]));
            outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
            if (!Directory.Exists(snapshotDir) || !Directory.Exists(outputRoot))
                throw new DirectoryNotFoundException("Both input and output directories must already exist.");
            RejectLinks(snapshotDir);
            RejectLinks(outputRoot);
            if (ContainsPath(snapshotDir, outputRoot) || ContainsPath(outputRoot, snapshotDir))
                throw new InvalidDataException("Input and output directories must be separate, not nested.");
            archives = Directory.GetFiles(snapshotDir, "*.zip").OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (archives.Length == 0) throw new InvalidDataException("The input directory contains no ZIP snapshots.");
            foreach (string archive in archives) RejectLinks(archive);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Cannot start batch: {ex.Message}");
            return 2;
        }

        string run = Path.Combine(outputRoot, $"sync-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(run);
        var findings = new List<Finding>();
        var inputs = new List<Input>();
        var valid = new List<Input>();

        async Task Check(string name, Func<Task<string>> action)
        {
            Console.WriteLine(name);
            try { findings.Add(new(name, "PASS", await action())); }
            catch (Exception ex)
            {
                findings.Add(new(name, "FAIL", $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        foreach (string archive in archives)
        {
            string name = Path.GetFileName(archive);
            await Check($"Snapshot: {name}", async () =>
            {
                string copies = Path.Combine(run, "inputs");
                Directory.CreateDirectory(copies);
                string copy = Path.Combine(copies, name);
                string hash = await HashAsync(archive);
                File.Copy(archive, copy);
                if (hash != await HashAsync(copy) || hash != await HashAsync(archive))
                    throw new IOException("Snapshot changed while being copied.");
                var input = new Input(archive, copy, hash);
                inputs.Add(input);
                var verified = await new SnapshotVerificationService().VerifyAsync(copy,
                    Path.Combine(run, "verification", Guid.NewGuid().ToString("N")), Array.Empty<string>());
                var restore = await new IsolatedSnapshotRestoreService(Path.Combine(run, "restores"))
                    .RestoreAsync(copy, _ => { });
                var compatibility = await new RestoredAppCompatibilityService().CheckAsync(restore);
                valid.Add(input);
                return $"Verified {verified.SnapshotId}; sessions={compatibility.Sessions}, messages={compatibility.Messages}, media references={compatibility.MediaReferences}.";
            });
        }

        for (int i = 0; i < valid.Count; i++)
        for (int j = i + 1; j < valid.Count; j++)
        {
            var first = valid[i];
            var second = valid[j];
            bool needsReview = false;
            await Check($"Compare: {Path.GetFileName(first.Original)} / {Path.GetFileName(second.Original)}", async () =>
            {
                var result = await new SnapshotComparisonService().CompareAsync(first.Copy, second.Copy);
                int ambiguous = result.Candidates.Count(c => c.Ambiguous);
                needsReview = result.Candidates.Count > 0;
                string comparisons = Path.Combine(run, "comparisons");
                Directory.CreateDirectory(comparisons);
                string detailPath = Path.Combine(comparisons, $"{i:D3}-{j:D3}.json");
                await File.WriteAllTextAsync(detailPath, JsonSerializer.Serialize(new
                {
                    First = Path.GetFileName(first.Original),
                    Second = Path.GetFileName(second.Original),
                    Candidates = result.Candidates.Select(c => new
                    {
                        FirstSessionId = c.First.Id,
                        SecondSessionId = c.Second.Id,
                        c.SharedEvidence,
                        c.SameTitle,
                        c.Ambiguous
                    })
                }, new JsonSerializerOptions { WriteIndented = true }));
                return $"{result.Candidates.Count} possible session matches ({ambiguous} ambiguous); no candidates does not prove independence. Details: {detailPath}";
            });
            if (needsReview && findings[^1].Status == "PASS")
                findings[^1] = findings[^1] with { Status = "REVIEW" };
        }

        await Check("Synthetic initial baseline and merge rehearsal", async () =>
        {
            string fixtures = Path.Combine(run, "synthetic");
            string first = await CreateFixtureAsync(fixtures, "first");
            string second = await CreateFixtureAsync(fixtures, "second");
            var baseline = await new InitialBaselineService(Path.Combine(fixtures, "baselines"))
                .CreateAsync(first, second, _ => { });
            string baselineDb = Path.Combine(baseline.Directory, "localhistory.db");
            string originalHash = await HashAsync(baselineDb);
            var result = await new DisposableThreeWayMergeDemoService(Path.Combine(fixtures, "rehearsals"))
                .RunAsync(baseline.Directory, _ => { });
            if (result.Added != 2 || result.Edited != 1 || result.Conflicts != 1 ||
                result.RepeatedChanges != 0 || result.Deletions != 2 ||
                result.DeleteConflicts != 1 || result.RepeatedDeletions != 0 ||
                originalHash != await HashAsync(baselineDb))
                throw new InvalidDataException("Unexpected rehearsal outcome or changed source baseline.");
            var preview = await new BaselineIdentityComparisonService().PreviewThreeWayAsync(
                Path.Combine(result.Workspace, "shared-base"),
                Path.Combine(result.Workspace, "branch-A"), Path.Combine(result.Workspace, "branch-B"));
            if (!preview.Findings.Any(f => f.Classification == "Different edits; conflict") ||
                !preview.Findings.Any(f => f.Classification == "Delete-versus-edit conflict"))
                throw new InvalidDataException("Expected conflicts were not reported.");
            return $"Two additions, one edit, two deletions, edit and delete conflicts, repeated application, stale replay and shared media checked. Result: {result.ResultDirectory}";
        });

        foreach (var input in inputs)
            await Check($"Input unchanged: {Path.GetFileName(input.Original)}", async () =>
                input.Hash == await HashAsync(input.Original)
                    ? "SHA-256 unchanged."
                    : throw new IOException("Input snapshot changed during the batch."));

        findings.Add(new("Real synchronization", "NOT TESTED",
            "General merge, real deletion history, divergent conversations, live application, recovery, Proton transport and visual inspection are not implemented or exercised by this batch."));
        string reportPath = Path.Combine(run, "report.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            Platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Findings = findings
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var finding in findings)
            Console.WriteLine($"{finding.Status}: {finding.Name} - {finding.Detail}");
        Console.WriteLine($"Report: {reportPath}");
        return findings.Any(f => f.Status == "FAIL") ? 1 : 0;
    }

    private static bool ContainsPath(string parent, string path) =>
        string.Equals(parent, path, PathComparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, PathComparison);

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Symbolic links are not supported: {current}");
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task<string> CreateFixtureAsync(string root, string name)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(directory, "files"));
        Directory.CreateDirectory(Path.Combine(directory, "images"));
        string attachment = Path.Combine(directory, "files", $"{name}.txt");
        await File.WriteAllTextAsync(attachment, $"Synthetic attachment {name}");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, "localhistory.db"), Pooling = false }.ToString()).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var session = new ChatSession(EndpointType.Responses, "Responses", $"Synthetic {name}");
            var message = new ChatMessage { Role = "user", Content = $"Synthetic message {name}" };
            message.MediaFiles.Add(new MediaFile { LocalPath = attachment, MediaType = "text/plain" });
            session.Messages.Add(message);
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
        }
        return (await new ConversationSnapshotService(directory).CreateAsync(_ => { })).ArchivePath;
    }
}

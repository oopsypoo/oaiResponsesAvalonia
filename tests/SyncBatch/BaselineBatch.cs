using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using oaiProtonSync.Services;

internal static class BaselineBatch
{
    private sealed record Finding(string Name, string Status, string Detail);
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static async Task<int> RunAsync(string baselineArgument, string outputArgument)
    {
        string source;
        string output;
        try
        {
            source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baselineArgument));
            output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputArgument));
            if (!Directory.Exists(source) || !Directory.Exists(output))
                throw new DirectoryNotFoundException("The baseline and output directories must exist.");
            RejectLinks(source);
            RejectLinks(output);
            if (Contains(source, output) || Contains(output, source))
                throw new InvalidDataException("Baseline and output must be separate, non-nested directories.");
            ValidateTree(source);
            using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(source, "experimental-baseline.json")));
            if (marker.RootElement.GetProperty("kind").GetString() != "disposable-initial-baseline" ||
                marker.RootElement.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("Expected a disposable initial-baseline marker.");
            foreach (string name in new[] { "localhistory.db", "images", "files" })
                if (!File.Exists(Path.Combine(source, name)) && !Directory.Exists(Path.Combine(source, name)))
                    throw new InvalidDataException($"The baseline is missing {name}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"Cannot start baseline batch: {ex.Message}");
            return 2;
        }

        string run = Path.Combine(output, $"baseline-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(run);
        var findings = new List<Finding>();
        Dictionary<string, string>? originalHashes = null;
        try
        {
            originalHashes = await HashTreeAsync(source);
            string copy = Path.Combine(run, "input");
            Directory.CreateDirectory(copy);
            File.Copy(Path.Combine(source, "experimental-baseline.json"), Path.Combine(copy, "experimental-baseline.json"));
            CopyDatabase(Path.Combine(source, "localhistory.db"), Path.Combine(copy, "localhistory.db"));
            foreach (string folder in new[] { "images", "files" })
                CopyTree(Path.Combine(source, folder), Path.Combine(copy, folder));
            if (!Same(originalHashes, await HashTreeAsync(source)))
                throw new IOException("The source baseline changed while it was being copied.");

            await CheckDatabaseAsync(Path.Combine(copy, "localhistory.db"));
            var identities = await new BaselineIdentityComparisonService().CompareAsync(copy, copy);
            var restored = new IsolatedSnapshotRestoreResult(copy, Path.Combine(copy, "localhistory.db"),
                Path.Combine(copy, "images"), Path.Combine(copy, "files"), "baseline", 0, "unchecked");
            var compatibility = await new RestoredAppCompatibilityService().CheckAsync(restored);
            if (identities.SharedSessions != compatibility.Sessions || identities.SharedMessages != compatibility.Messages)
                throw new InvalidDataException("Not every session and message has a valid identity.");
            findings.Add(new("Candidate baseline", "PASS",
                $"Application can read {compatibility.Sessions} sessions, {compatibility.Messages} messages and {compatibility.MediaReferences} media references; identities are complete. This does not prove ancestry or visual review."));

            var rehearsal = await new DisposableThreeWayMergeDemoService(Path.Combine(run, "rehearsals"))
                .RunAsync(copy, _ => { });
            if (rehearsal.Added != 2 || rehearsal.Edited != 1 || rehearsal.Conflicts != 1 ||
                rehearsal.RepeatedChanges != 0 || rehearsal.Deletions != 2 ||
                rehearsal.DeleteConflicts != 1 || rehearsal.RepeatedDeletions != 0)
                throw new InvalidDataException("Unexpected controlled rehearsal outcome.");
            var preserved = await new BaselineIdentityComparisonService().CompareAsync(copy, rehearsal.ResultDirectory);
            if (preserved.SessionsOnlyA.Count != 0 || preserved.MessagesOnlyA.Count != 0 ||
                preserved.DifferentSessions.Count != 0 || preserved.DifferentMessages.Count != 0)
                throw new InvalidDataException("A pre-existing session or message was altered in the result.");
            var beforeMedia = await ReadMediaAsync(Path.Combine(copy, "localhistory.db"));
            var afterMedia = await ReadMediaAsync(Path.Combine(rehearsal.ResultDirectory, "localhistory.db"));
            foreach (var (message, references) in beforeMedia)
                if (!afterMedia.TryGetValue(message, out var actual) || !references.SequenceEqual(actual))
                    throw new InvalidDataException("A pre-existing message's media relationships changed.");
            foreach (string folder in new[] { "images", "files" })
            {
                var before = await HashTreeAsync(Path.Combine(copy, folder));
                var after = await HashTreeAsync(Path.Combine(rehearsal.ResultDirectory, folder));
                if (before.Any(pair => !after.TryGetValue(pair.Key, out string? hash) || hash != pair.Value))
                    throw new InvalidDataException("A pre-existing media file is missing or changed in the result.");
            }
            findings.Add(new("Controlled merge on candidate copy", "PASS",
                $"Synthetic additions, edits, deletions, conflicts, repetition, stale replay and shared media passed; pre-existing session/message fields, media relationships and media bytes preserved. Result: {rehearsal.ResultDirectory}"));
            findings.Add(new("Real synchronization", "NOT TESTED",
                "The experiment does not merge arbitrary real changes, compare media relationships, handle divergent conversation branches, or prove live-application recovery. Synthetic edits do not authorize editing submitted history."));
        }
        catch (Exception ex)
        {
            findings.Add(new("Baseline rehearsal", "FAIL", $"{ex.GetType().Name}: {ex.Message}"));
        }

        if (originalHashes is not null)
        {
            try
            {
                bool unchanged = Same(originalHashes, await HashTreeAsync(source));
                findings.Add(new("Source baseline", unchanged ? "PASS" : "FAIL",
                    unchanged ? "All source files remain unchanged." : "Source files changed during the batch."));
            }
            catch (Exception ex)
            {
                findings.Add(new("Source baseline", "FAIL", $"Could not verify source: {ex.Message}"));
            }
        }
        string reportPath = Path.Combine(run, "report.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            Platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Source = source,
            Findings = findings
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var finding in findings)
            Console.WriteLine($"{finding.Status}: {finding.Name} - {finding.Detail}");
        Console.WriteLine($"Report: {reportPath}");
        return findings.Any(f => f.Status == "FAIL") ? 1 : 0;
    }

    private static bool Contains(string parent, string path) =>
        string.Equals(parent, path, Comparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, Comparison);

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Symbolic links are not supported: {current}");
    }

    private static void CopyDatabase(string source, string destination)
    {
        using var input = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var output = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        input.Open();
        output.Open();
        input.BackupDatabase(output);
    }

    private static void ValidateTree(string root)
    {
        RejectLinks(root);
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            RejectLinks(entry);
            if (Directory.Exists(entry)) ValidateTree(entry);
        }
    }

    private static async Task CheckDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(Convert.ToString(await command.ExecuteScalarAsync()), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Baseline SQLite integrity check failed.");
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync()) throw new InvalidDataException("Baseline has invalid foreign keys.");
    }

    private static async Task<Dictionary<string, List<(string Path, string Type)>>> ReadMediaAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.SyncId, media.LocalPath, media.MediaType
            FROM ExperimentalMessageIdentity i
            LEFT JOIN Media media ON i.MessageId = media.ChatMessageId
            ORDER BY i.SyncId, media.LocalPath, media.MediaType
            """;
        var result = new Dictionary<string, List<(string Path, string Type)>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string id = reader.GetString(0);
            if (!result.TryGetValue(id, out var references))
                result.Add(id, references = []);
            if (!reader.IsDBNull(1))
                references.Add((reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            RejectLinks(entry);
            string target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyTree(entry, target);
            else File.Copy(entry, target);
        }
    }

    private static async Task<Dictionary<string, string>> HashTreeAsync(string root)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task Visit(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectLinks(path);
                if (Directory.Exists(path))
                    await Visit(path);
                else
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    hashes.Add(Path.GetRelativePath(root, path), Convert.ToHexString(await SHA256.HashDataAsync(stream)));
                }
            }
        }
        await Visit(root);
        return hashes;
    }

    private static bool Same(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
}

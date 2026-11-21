using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using oaiResponsesAvalonia.Data;

namespace oaiProtonSync.Services;

/// <summary>Demonstrates identity comparison using temporary database copies only.</summary>
public sealed class BaselineChangeDemonstrationService
{
    public async Task<IdentityComparisonResult> RunAsync(string baselineFolder, Action<string> report, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(baselineFolder))
            throw new InvalidDataException("Choose an experimental baseline folder first.");
        string marker = Path.Combine(baselineFolder, "experimental-baseline.json");
        string sourcePath = Path.Combine(baselineFolder, "localhistory.db");
        if (!File.Exists(marker) || !File.Exists(sourcePath))
            throw new InvalidDataException("The selected folder must contain experimental-baseline.json and localhistory.db.");

        string workspace = Directory.CreateTempSubdirectory("oai-identity-demo-").FullName;
        try
        {
            string a = Directory.CreateDirectory(Path.Combine(workspace, "A")).FullName;
            string b = Directory.CreateDirectory(Path.Combine(workspace, "B")).FullName;
            report("Creating two temporary database copies from the selected baseline; no media or live data will be changed…");
            File.Copy(marker, Path.Combine(a, "experimental-baseline.json"));
            File.Copy(marker, Path.Combine(b, "experimental-baseline.json"));
            string dbA = Path.Combine(a, "localhistory.db");
            string dbB = Path.Combine(b, "localhistory.db");
            CopyDatabase(sourcePath, dbA);
            token.ThrowIfCancellationRequested();
            // Copy A, not the source again: both demonstrations must start from exactly the same state.
            CopyDatabase(dbA, dbB);
            var comparer = new BaselineIdentityComparisonService();
            var before = await comparer.CompareAsync(a, b, token);
            if (before.SharedSessions == 0 || before.SharedMessages == 0 ||
                before.SessionsOnlyA.Count != 0 || before.SessionsOnlyB.Count != 0 ||
                before.MessagesOnlyA.Count != 0 || before.MessagesOnlyB.Count != 0 ||
                before.DifferentSessions.Count != 0 || before.DifferentMessages.Count != 0)
                throw new InvalidDataException("The baseline must contain sessions and messages with valid identities, and both initial copies must match.");

            report($"Starting point: {before.SharedSessions} shared sessions and {before.SharedMessages} shared messages.");
            report("Simulating one new message in A and one different new message in B, plus an edit to an existing message in B.");
            await ChangeCopyAsync(dbA, "Temporary simulated addition on device A", false, token);
            await ChangeCopyAsync(dbB, "Temporary simulated addition on device B", true, token);
            var result = await comparer.CompareAsync(a, b, token);
            if (result.SharedSessions != before.SharedSessions || result.SharedMessages != before.SharedMessages ||
                result.SessionsOnlyA.Count != 0 || result.SessionsOnlyB.Count != 0 ||
                result.MessagesOnlyA.Count != 1 || result.MessagesOnlyB.Count != 1 ||
                result.DifferentSessions.Count != 0 || result.DifferentMessages.Count != 1 ||
                result.MessagesOnlyA[0].SyncId == result.MessagesOnlyB[0].SyncId)
                throw new InvalidDataException("The comparison did not detect the expected simulated changes. No data was merged.");
            return result;
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
                report("Temporary demonstration copies removed.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report($"Could not remove temporary demonstration copies at {workspace}: {ex.Message}");
            }
        }
    }

    private static void CopyDatabase(string sourcePath, string destinationPath)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static async Task ChangeCopyAsync(string path, string content, bool editExisting, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()).Options;
        await using var db = new AppDbContext(options);
        var existing = await db.Messages.OrderBy(m => m.Id).FirstAsync(token);
        db.Messages.Add(new ChatMessage { ChatSessionId = existing.ChatSessionId, Role = "user", Content = content });
        if (editExisting)
            existing.Content = (existing.Content ?? string.Empty) + " [temporary simulated edit]";
        await db.SaveChangesAsync(token);
    }
}

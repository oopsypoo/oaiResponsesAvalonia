using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using oaiResponsesAvalonia.Services;

namespace oaiProtonSync.Services;

public sealed record SnapshotVerificationResult(
    string ArchivePath,
    string ArchiveSha256,
    bool? MatchesLocalArchive,
    string? ComparedLocalArchivePath,
    int LocalArchivesCompared,
    string SnapshotId,
    int MediaFileCount,
    long ArchiveSize,
    string DatabaseIntegrity,
    IReadOnlyList<string> VerifiedEntries);

public sealed class SnapshotVerificationService
{
    private const long MaxManifestBytes = 1024 * 1024;

    public async Task<SnapshotVerificationResult> VerifyAsync(
        string archivePath,
        string verificationDirectory,
        IReadOnlyCollection<string> localArchivePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationDirectory);

        if (!File.Exists(archivePath))
            throw new FileNotFoundException("The downloaded snapshot archive was not found.", archivePath);

        Directory.CreateDirectory(verificationDirectory);
        string extractedDatabasePath = Path.Combine(verificationDirectory, "verified-localhistory.db");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archiveInfo = new FileInfo(archivePath);
            string archiveHash = await ComputeSha256Async(archivePath, cancellationToken);
            var verifiedEntries = new List<string>();
            string snapshotId;
            string databaseEntryPath;
            string expectedDatabaseHash;
            IReadOnlyList<ManifestMediaFile> mediaFiles;

            await using (var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                var manifestEntry = archive.GetEntry("manifest.json")
                    ?? throw new InvalidDataException("The archive does not contain manifest.json.");
                if (manifestEntry.Length > MaxManifestBytes)
                    throw new InvalidDataException("The snapshot manifest is unexpectedly large.");

                await using var manifestStream = manifestEntry.Open();
                var manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(
                    manifestStream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken)
                    ?? throw new InvalidDataException("The snapshot manifest is empty or invalid.");

                if (manifest.ManifestVersion != 1)
                    throw new InvalidDataException($"Unsupported snapshot manifest version: {manifest.ManifestVersion}.");
                if (string.IsNullOrWhiteSpace(manifest.SnapshotId))
                    throw new InvalidDataException("The snapshot manifest has no snapshot ID.");
                if (!IsSafeArchivePath(manifest.DatabaseFile))
                    throw new InvalidDataException("The database path in the manifest is invalid.");

                snapshotId = manifest.SnapshotId;
                databaseEntryPath = manifest.DatabaseFile;
                expectedDatabaseHash = NormalizeHash(manifest.DatabaseSha256, "database");
                mediaFiles = manifest.MediaFiles ?? [];

                var databaseEntry = archive.GetEntry(databaseEntryPath)
                    ?? throw new InvalidDataException("The database listed in the manifest is missing from the archive.");
                string actualDatabaseHash = await ComputeEntrySha256Async(databaseEntry, cancellationToken);
                if (!HashesEqual(expectedDatabaseHash, actualDatabaseHash))
                    throw new InvalidDataException("The database SHA-256 does not match the snapshot manifest.");

                await using (var databaseInput = databaseEntry.Open())
                await using (var databaseOutput = new FileStream(extractedDatabasePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await databaseInput.CopyToAsync(databaseOutput, cancellationToken);
                verifiedEntries.Add($"{databaseEntryPath} (SHA-256 verified)");

                var seenPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mediaFile in mediaFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsSafeArchivePath(mediaFile.ArchivePath) || !seenPaths.Add(mediaFile.ArchivePath))
                        throw new InvalidDataException("The manifest contains an invalid or duplicate media path.");

                    var mediaEntry = archive.GetEntry(mediaFile.ArchivePath)
                        ?? throw new InvalidDataException($"A media file listed in the manifest is missing: {mediaFile.ArchivePath}");
                    if (mediaEntry.Length != mediaFile.Size)
                        throw new InvalidDataException($"Media file size does not match the manifest: {mediaFile.ArchivePath}");

                    string expectedHash = NormalizeHash(mediaFile.Sha256, mediaFile.ArchivePath);
                    string actualHash = await ComputeEntrySha256Async(mediaEntry, cancellationToken);
                    if (!HashesEqual(expectedHash, actualHash))
                        throw new InvalidDataException($"Media SHA-256 does not match the manifest: {mediaFile.ArchivePath}");
                    verifiedEntries.Add($"{mediaFile.ArchivePath} (SHA-256 verified)");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            string databaseIntegrity = CheckSqliteIntegrity(extractedDatabasePath);
            if (!string.Equals(databaseIntegrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SQLite integrity check failed: {databaseIntegrity}");
            verifiedEntries.Add("SQLite integrity_check: ok");

            bool? matchesLocal = null;
            string? comparedLocalArchivePath = null;
            int localArchivesCompared = 0;
            foreach (string localArchivePath in localArchivePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(localArchivePath))
                    continue;

                localArchivesCompared++;
                string localHash = await ComputeSha256Async(localArchivePath, cancellationToken);
                if (!HashesEqual(archiveHash, localHash))
                    continue;

                matchesLocal = true;
                comparedLocalArchivePath = localArchivePath;
                break;
            }

            if (matchesLocal is null && localArchivesCompared > 0)
                matchesLocal = false;

            verifiedEntries.Add(matchesLocal switch
            {
                true => $"Downloaded archive exactly matches local archive: {comparedLocalArchivePath} (SHA-256).",
                false => $"Downloaded archive did not exactly match any of the {localArchivesCompared} local snapshot archive(s) checked; internal manifest entries were verified.",
                null => "No local snapshot archive was available for whole-archive comparison; internal manifest entries were verified."
            });

            return new SnapshotVerificationResult(
                archivePath,
                archiveHash,
                matchesLocal,
                comparedLocalArchivePath,
                localArchivesCompared,
                snapshotId,
                mediaFiles.Count,
                archiveInfo.Length,
                databaseIntegrity,
                verifiedEntries);
        }
        finally
        {
            try
            {
                if (File.Exists(extractedDatabasePath))
                    File.Delete(extractedDatabasePath);

                foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
                {
                    string sidecarPath = extractedDatabasePath + suffix;
                    if (File.Exists(sidecarPath))
                        File.Delete(sidecarPath);
                }
            }
            catch
            {
                // A leftover verification copy is harmless; preserve the verification result or original exception.
            }
        }
    }

    private static string CheckSqliteIntegrity(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return Convert.ToString(command.ExecuteScalar()) ?? "no result";
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static async Task<string> ComputeEntrySha256Async(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string NormalizeHash(string hash, string item)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException($"The SHA-256 value for {item} is invalid.");
        return hash.ToLowerInvariant();
    }

    private static bool HashesEqual(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static bool IsSafeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\'))
            return false;
        return path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private sealed record SnapshotManifest(
        int ManifestVersion,
        string SnapshotId,
        DateTimeOffset CreatedAtUtc,
        string DatabaseFile,
        string DatabaseSha256,
        IReadOnlyList<ManifestMediaFile>? MediaFiles);

    private sealed record ManifestMediaFile(
        string ArchivePath,
        string Sha256,
        long Size,
        IReadOnlyList<string>? MediaTypes);
}

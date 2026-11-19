using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using oaiResponsesAvalonia.Services;

namespace oaiProtonSync.Services;

public sealed record IsolatedSnapshotRestoreResult(
    string RestoreDirectory,
    string DatabasePath,
    string ImagesDirectory,
    string FilesDirectory,
    string SnapshotId,
    int MediaFileCount,
    string DatabaseIntegrity);

/// <summary>
/// Materializes a verified snapshot into a new, isolated directory. It never writes to the live app data directory.
/// </summary>
public sealed class IsolatedSnapshotRestoreService
{
    private const long MaxManifestBytes = 1024 * 1024;
    private readonly string _restoreRoot;

    public IsolatedSnapshotRestoreService()
        : this(Path.Combine(AppPaths.DataDirectory, "ProtonSync", "RestoreTests"))
    {
    }

    internal IsolatedSnapshotRestoreService(string restoreRoot)
    {
        _restoreRoot = restoreRoot;
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".apng", ".arw", ".avif", ".bmp", ".cr2", ".cr3", ".dng", ".gif", ".heic", ".heif",
        ".ico", ".j2k", ".jp2", ".jpe", ".jpeg", ".jfif", ".jpg", ".jxl", ".nef", ".nrw",
        ".orf", ".png", ".psd", ".raw", ".rw2", ".svg", ".tif", ".tiff", ".webp"
    };

    public async Task<IsolatedSnapshotRestoreResult> RestoreAsync(
        string archivePath,
        Action<string> reportProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(reportProgress);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("The downloaded snapshot archive was not found.", archivePath);

        Directory.CreateDirectory(_restoreRoot);
        string restoreDirectory = Path.Combine(_restoreRoot, $"restore_{Guid.NewGuid():N}");
        string imagesDirectory = Path.Combine(restoreDirectory, "images");
        string filesDirectory = Path.Combine(restoreDirectory, "files");
        string databasePath = Path.Combine(restoreDirectory, "localhistory.db");
        bool restoreDirectoryCreated = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(restoreDirectory);
            restoreDirectoryCreated = true;
            Directory.CreateDirectory(imagesDirectory);
            Directory.CreateDirectory(filesDirectory);

            reportProgress("Validating snapshot manifest and preparing an isolated restore directory…");
            await using var archiveStream = new FileStream(
                archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);

            var manifestEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException("The archive does not contain manifest.json.");
            if (manifestEntry.Length > MaxManifestBytes)
                throw new InvalidDataException("The snapshot manifest is unexpectedly large.");

            SnapshotManifest manifest;
            await using (var manifestStream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(
                    manifestStream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken)
                    ?? throw new InvalidDataException("The snapshot manifest is empty or invalid.");
            }

            ValidateManifest(manifest);
            string snapshotId = manifest.SnapshotId;
            var databaseEntry = archive.GetEntry(manifest.DatabaseFile)
                ?? throw new InvalidDataException("The database listed in the manifest is missing from the archive.");

            reportProgress("Restoring the snapshot database into the isolated test directory…");
            await using (var input = databaseEntry.Open())
            await using (var output = new FileStream(databasePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                string actualHash = await CopyAndHashAsync(input, output, cancellationToken);
                if (!HashesEqual(NormalizeHash(manifest.DatabaseSha256, "database"), actualHash))
                    throw new InvalidDataException("The database SHA-256 does not match the snapshot manifest.");
            }

            foreach (var mediaFile in manifest.MediaFiles ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mediaEntry = archive.GetEntry(mediaFile.ArchivePath)
                    ?? throw new InvalidDataException($"A media file listed in the manifest is missing: {mediaFile.ArchivePath}");
                if (mediaEntry.Length != mediaFile.Size)
                    throw new InvalidDataException($"Media file size does not match the manifest: {mediaFile.ArchivePath}");

                string fileName = Path.GetFileName(mediaFile.ArchivePath);
                if (!IsSafeFileName(fileName))
                    throw new InvalidDataException($"The media filename is not safe for local restore: {fileName}");
                string destinationDirectory = IsImage(mediaFile, fileName) ? imagesDirectory : filesDirectory;
                string destinationPath = Path.Combine(destinationDirectory, fileName);
                if (File.Exists(destinationPath))
                    throw new InvalidDataException($"Multiple archive media entries resolve to the same restored filename: {fileName}");

                await using var mediaInput = mediaEntry.Open();
                await using var mediaOutput = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                string actualHash = await CopyAndHashAsync(mediaInput, mediaOutput, cancellationToken);
                if (!HashesEqual(NormalizeHash(mediaFile.Sha256, fileName), actualHash))
                    throw new InvalidDataException($"Media SHA-256 does not match the snapshot manifest: {fileName}");
            }

            await using (var input = manifestEntry.Open())
            await using (var output = new FileStream(Path.Combine(restoreDirectory, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await input.CopyToAsync(output, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            reportProgress("Mapping packaged media into the isolated database and validating the restored copy…");
            var mediaFiles = manifest.MediaFiles ?? [];
            RewriteDatabaseMediaReferences(databasePath, mediaFiles);
            string integrity = CheckSqliteIntegrity(databasePath);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SQLite integrity check failed: {integrity}");
            ValidateDatabaseMediaReferences(databasePath, mediaFiles);
            reportProgress($"Isolated restore prepared at {restoreDirectory}");
            return new IsolatedSnapshotRestoreResult(
                restoreDirectory,
                databasePath,
                imagesDirectory,
                filesDirectory,
                snapshotId,
                manifest.MediaFiles?.Count ?? 0,
                integrity);
        }
        catch
        {
            if (restoreDirectoryCreated)
            {
                try
                {
                    Directory.Delete(restoreDirectory, recursive: true);
                }
                catch
                {
                    // Preserve the primary error if cleanup of a failed isolated restore is not possible.
                }
            }

            throw;
        }
    }

    private static void ValidateManifest(SnapshotManifest manifest)
    {
        if (manifest.ManifestVersion != 1)
            throw new InvalidDataException($"Unsupported snapshot manifest version: {manifest.ManifestVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.SnapshotId))
            throw new InvalidDataException("The snapshot manifest has no snapshot ID.");
        if (!IsSafeArchivePath(manifest.DatabaseFile) || string.Equals(manifest.DatabaseFile, "manifest.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The database path in the manifest is invalid.");
        _ = NormalizeHash(manifest.DatabaseSha256, "database");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manifest.DatabaseFile, "manifest.json" };
        foreach (var mediaFile in manifest.MediaFiles ?? [])
        {
            if (!IsSafeArchivePath(mediaFile.ArchivePath) || !paths.Add(mediaFile.ArchivePath))
                throw new InvalidDataException("The manifest contains an invalid or duplicate archive path.");
            if (mediaFile.Size < 0)
                throw new InvalidDataException($"Media file size is invalid: {mediaFile.ArchivePath}");
            _ = NormalizeHash(mediaFile.Sha256, mediaFile.ArchivePath);
        }
    }

    private static void RewriteDatabaseMediaReferences(string databasePath, IReadOnlyList<ManifestMediaFile> mediaFiles)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        connection.Open();

        var updates = new List<(long Id, string LocalPath)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT Id, LocalPath FROM Media;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                string localPath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                string fileName = GetStoredFileName(localPath);
                ManifestMediaFile entry = ResolveMediaEntry(fileName, localPath, mediaFiles);
                updates.Add((id, $"media/{Path.GetFileName(entry.ArchivePath)}"));
            }
        }

        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE Media SET LocalPath = $localPath WHERE Id = $id;";
        var idParameter = update.CreateParameter();
        idParameter.ParameterName = "$id";
        update.Parameters.Add(idParameter);
        var pathParameter = update.CreateParameter();
        pathParameter.ParameterName = "$localPath";
        update.Parameters.Add(pathParameter);

        foreach (var (id, localPath) in updates)
        {
            idParameter.Value = id;
            pathParameter.Value = localPath;
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidDataException($"Could not update restored media reference {id}.");
        }

        transaction.Commit();
    }

    private static void ValidateDatabaseMediaReferences(string databasePath, IReadOnlyList<ManifestMediaFile> mediaFiles)
    {
        var availableNames = new HashSet<string>(mediaFiles.Select(file => Path.GetFileName(file.ArchivePath)), StringComparer.OrdinalIgnoreCase);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT LocalPath FROM Media;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string localPath = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            string fileName = GetStoredFileName(localPath);
            if (string.IsNullOrWhiteSpace(fileName) || !availableNames.Contains(fileName))
                throw new InvalidDataException($"The restored database references media that is absent from the snapshot: {localPath}");
        }
    }

    private static ManifestMediaFile ResolveMediaEntry(
        string fileName,
        string storedPath,
        IReadOnlyList<ManifestMediaFile> mediaFiles)
    {
        var exactMatches = mediaFiles
            .Where(file => string.Equals(Path.GetFileName(file.ArchivePath), fileName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactMatches.Length == 1)
            return exactMatches[0];

        // Older snapshots may retain the original database filename while packaging
        // media with a SHA-256 prefix: <hash>_<original filename>.
        var legacyMatches = mediaFiles
            .Where(file => Path.GetFileName(file.ArchivePath).EndsWith($"_{fileName}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (legacyMatches.Length == 1)
            return legacyMatches[0];
        if (legacyMatches.Length > 1 || exactMatches.Length > 1)
            throw new InvalidDataException(
                $"The snapshot has multiple packaged files matching database media reference '{storedPath}'. " +
                "The reference cannot be mapped unambiguously.");

        throw new InvalidDataException(
            $"The snapshot database media reference '{storedPath}' cannot be mapped to a packaged file. " +
            "Some snapshots may not retain all mappings, including when SQLite changes were left in a WAL file. " +
            "Create a new snapshot from the original database and media, then upload and download it before retrying.");
    }

    private static string GetStoredFileName(string localPath) =>
        localPath.Replace('\\', '/').Split('/').LastOrDefault() ?? string.Empty;

    private static bool IsImage(ManifestMediaFile mediaFile, string fileName) =>
        (mediaFile.MediaTypes ?? []).Any(type => type is not null && type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) ||
        ImageExtensions.Contains(Path.GetExtension(fileName));

    private static bool IsSafeFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName is not "." and not ".." &&
        !Path.IsPathRooted(fileName) &&
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !fileName.Contains('/') &&
        !fileName.Contains('\\') &&
        !fileName.Contains(':') &&
        !fileName.Any(char.IsControl);

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

    private static async Task<string> CopyAndHashAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string NormalizeHash(string? hash, string item)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException($"The SHA-256 value for {item} is invalid.");
        return hash.ToLowerInvariant();
    }

    private static bool HashesEqual(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static bool IsSafeArchivePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith('/') &&
        !path.Contains('\\') &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

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

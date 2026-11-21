using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using oaiResponsesAvalonia.Services;

namespace oaiProtonSync.Services;

public sealed record ConversationSnapshotResult(string ArchivePath, int MediaFileCount, long ArchiveSize);

public sealed class ConversationSnapshotService
{
    private readonly string _dataDirectory;

    public ConversationSnapshotService() : this(AppPaths.DataDirectory)
    {
    }

    internal ConversationSnapshotService(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
    }

    private static readonly string[] ImageExtensions =
    [
        ".apng", ".arw", ".avif", ".bmp", ".cr2", ".cr3", ".dng", ".gif", ".heic", ".heif",
        ".ico", ".j2k", ".jp2", ".jpe", ".jpeg", ".jfif", ".jpg", ".jxl", ".nef", ".nrw",
        ".orf", ".png", ".psd", ".raw", ".rw2", ".svg", ".tif", ".tiff", ".webp"
    ];

    public Task<ConversationSnapshotResult> CreateAsync(
        Action<string> reportProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reportProgress);
        return Task.Run(() => Create(_dataDirectory, reportProgress, cancellationToken), cancellationToken);
    }

    internal static string CreateSnapshotId(DateTimeOffset localTime, Guid id)
    {
        // Include the offset so local times remain unambiguous across time zones and daylight saving changes.
        string timestamp = localTime.ToString("yyyyMMdd'T'HHmmsszzz", CultureInfo.InvariantCulture).Replace(":", "");
        return $"{timestamp}_{id:N}";
    }

    internal static string CreateSnapshotFileName(string hostname, string snapshotId)
    {
        string safeHostname = new string(hostname.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray()).Trim('-', '.');
        if (safeHostname.Length == 0)
            throw new InvalidOperationException("The machine hostname cannot be represented in a snapshot filename.");
        if (safeHostname.Length > 63)
            safeHostname = safeHostname[..63].TrimEnd('-', '.');
        return $"snapshot_{safeHostname}_{snapshotId}.zip";
    }

    internal static bool IsSnapshotFileName(string name) =>
        !name.Contains('/') && !name.Contains('\\') &&
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
        (name.StartsWith("snapshot_", StringComparison.OrdinalIgnoreCase) ||
         name.StartsWith("oaiResponsesAvalonia_snapshot_", StringComparison.OrdinalIgnoreCase));

    private static ConversationSnapshotResult Create(string dataDirectory, Action<string> reportProgress, CancellationToken cancellationToken)
    {
        string databasePath = Path.Combine(dataDirectory, "localhistory.db");
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("The application conversation database was not found.", databasePath);

        string snapshotsDirectory = Path.Combine(dataDirectory, "ProtonSync", "Snapshots");
        Directory.CreateDirectory(snapshotsDirectory);

        string snapshotId = CreateSnapshotId(DateTimeOffset.Now, Guid.NewGuid());
        string workingDirectory = Path.Combine(snapshotsDirectory, $".working_{snapshotId}");
        string archivePath = Path.Combine(snapshotsDirectory, CreateSnapshotFileName(Environment.MachineName, snapshotId));
        string temporaryArchivePath = archivePath + ".tmp";
        string snapshotDatabasePath = Path.Combine(workingDirectory, "localhistory.db");
        string mediaDirectory = Path.Combine(workingDirectory, "media");
        Directory.CreateDirectory(mediaDirectory);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            reportProgress("Creating a consistent SQLite database snapshot. SQLite may briefly block other database writes…");
            CreateDatabaseBackup(databasePath, snapshotDatabasePath);
            cancellationToken.ThrowIfCancellationRequested();

            var storageFolders = ReadStorageFolders(dataDirectory);
            var mediaRecords = ReadMediaRecords(snapshotDatabasePath);
            reportProgress($"Found {mediaRecords.Count} database media reference(s). Resolving their local files…");

            var byContentHash = new Dictionary<string, MediaArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            var rewrittenPaths = new List<(long Id, string LocalPath)>();

            foreach (var mediaRecord in mediaRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(mediaRecord.LocalPath))
                    throw new InvalidDataException($"Media record {mediaRecord.Id} has an empty local path.");

                string sourcePath = ResolveMediaFile(mediaRecord.LocalPath, storageFolders, mediaRecord.Id);
                string sourceFullPath = Path.GetFullPath(sourcePath);
                var before = new FileInfo(sourceFullPath);
                long originalLength = before.Length;
                DateTime originalWriteTime = before.LastWriteTimeUtc;

                using var source = new FileStream(sourceFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                string contentHash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
                source.Position = 0;

                if (!byContentHash.TryGetValue(contentHash, out var archiveEntry))
                {
                    string archiveFileName = $"{contentHash}_{MakeSafeFileName(Path.GetFileName(sourceFullPath))}";
                    string stagedFilePath = Path.Combine(mediaDirectory, archiveFileName);
                    using (var destination = new FileStream(stagedFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        source.CopyTo(destination);
                    }

                    var after = new FileInfo(sourceFullPath);
                    if (after.Length != originalLength || after.LastWriteTimeUtc != originalWriteTime)
                    {
                        throw new IOException($"Media file changed while the snapshot was being created: {Path.GetFileName(sourceFullPath)}");
                    }

                    string stagedHash = ComputeSha256(stagedFilePath);
                    if (!string.Equals(stagedHash, contentHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Media file changed while it was being copied: {Path.GetFileName(sourceFullPath)}");

                    var stagedInfo = new FileInfo(stagedFilePath);
                    archiveEntry = new MediaArchiveEntry(
                        $"media/{archiveFileName}", contentHash, stagedInfo.Length, new HashSet<string>(StringComparer.Ordinal));
                    byContentHash.Add(contentHash, archiveEntry);
                }

                if (!string.IsNullOrWhiteSpace(mediaRecord.MediaType))
                    archiveEntry.MediaTypes.Add(mediaRecord.MediaType);

                rewrittenPaths.Add((mediaRecord.Id, archiveEntry.ArchivePath));
            }

            cancellationToken.ThrowIfCancellationRequested();
            RewriteSnapshotMediaPaths(snapshotDatabasePath, rewrittenPaths);

            string databaseHash = ComputeSha256(snapshotDatabasePath);
            var manifest = new SnapshotManifest(
                ManifestVersion: 1,
                SnapshotId: snapshotId,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                DatabaseFile: "localhistory.db",
                DatabaseSha256: databaseHash,
                MediaFiles: byContentHash.Values
                    .OrderBy(entry => entry.ArchivePath, StringComparer.Ordinal)
                    .Select(entry => new SnapshotMediaFile(
                        entry.ArchivePath,
                        entry.Sha256,
                        entry.Size,
                        entry.MediaTypes.OrderBy(type => type, StringComparer.Ordinal).ToArray()))
                    .ToArray());

            File.WriteAllText(
                Path.Combine(workingDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            cancellationToken.ThrowIfCancellationRequested();
            reportProgress($"Packaging the database and {byContentHash.Count} unique media file(s)…");
            ZipFile.CreateFromDirectory(workingDirectory, temporaryArchivePath, CompressionLevel.Fastest, includeBaseDirectory: false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryArchivePath, archivePath);

            long archiveSize = new FileInfo(archivePath).Length;
            reportProgress($"Local snapshot created: {archivePath}");
            return new ConversationSnapshotResult(archivePath, byContentHash.Count, archiveSize);
        }
        catch
        {
            TryDeleteFile(temporaryArchivePath);
            TryDeleteFile(archivePath);
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workingDirectory))
                    Directory.Delete(workingDirectory, recursive: true);
            }
            catch
            {
                // Keep a failed cleanup from hiding the snapshot result or primary failure.
            }
        }
    }

    private static void CreateDatabaseBackup(string sourcePath, string destinationPath)
    {
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };

        using var source = new SqliteConnection(sourceBuilder.ToString());
        using var destination = new SqliteConnection(destinationBuilder.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        using var command = destination.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        if (!string.Equals(Convert.ToString(command.ExecuteScalar()), "delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Could not make the snapshot database self-contained.");
    }

    private static List<MediaRecord> ReadMediaRecords(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, LocalPath, MediaType FROM Media ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var records = new List<MediaRecord>();
        while (reader.Read())
        {
            records.Add(new MediaRecord(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
        }

        return records;
    }

    private static void RewriteSnapshotMediaPaths(string databasePath, IEnumerable<(long Id, string LocalPath)> paths)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Media SET LocalPath = $localPath WHERE Id = $id;";
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "$id";
        command.Parameters.Add(idParameter);
        var pathParameter = command.CreateParameter();
        pathParameter.ParameterName = "$localPath";
        command.Parameters.Add(pathParameter);

        foreach (var (id, localPath) in paths)
        {
            idParameter.Value = id;
            pathParameter.Value = localPath;
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidDataException($"Could not update media reference {id} in the snapshot database.");
        }

        transaction.Commit();
    }

    private static StorageFolders ReadStorageFolders(string dataDirectory)
    {
        string appRoot = dataDirectory;
        string imagesName = "images";
        string filesName = "files";
        string settingsPath = Path.Combine(dataDirectory, "settings.json");

        if (File.Exists(settingsPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = document.RootElement;
            appRoot = ReadString(root, "AppRoot") ?? appRoot;
            imagesName = ReadString(root, "ImagesFolder") ?? imagesName;
            filesName = ReadString(root, "FilesFolder") ?? filesName;
        }

        appRoot = Path.GetFullPath(appRoot);
        string imagesDirectory = ResolveConfiguredPath(appRoot, imagesName);
        string filesDirectory = ResolveConfiguredPath(appRoot, filesName);
        return new StorageFolders(imagesDirectory, filesDirectory);
    }

    private static string ResolveConfiguredPath(string appRoot, string path) =>
        Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(appRoot, path));

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }

        return null;
    }

    private static string ResolveMediaFile(string storedPath, StorageFolders folders, long mediaId)
    {
        if (Path.IsPathFullyQualified(storedPath) || IsWindowsAbsolutePath(storedPath))
        {
            string absolutePath = Path.GetFullPath(storedPath);
            if (File.Exists(absolutePath))
                return absolutePath;
        }

        string fileName = storedPath.Replace('\\', '/').Split('/').LastOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
            throw new FileNotFoundException($"Media record {mediaId} has an invalid stored path: {storedPath}");

        bool isImage = ImageExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
        string[] searchFolders = isImage
            ? [folders.ImagesDirectory, folders.FilesDirectory]
            : [folders.FilesDirectory, folders.ImagesDirectory];

        foreach (string folder in searchFolders)
        {
            string candidate = Path.Combine(folder, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Could not locate media file '{fileName}' referenced by database record {mediaId}. No snapshot was uploaded.");
    }

    private static bool IsWindowsAbsolutePath(string path) =>
        path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    private static string MakeSafeFileName(string fileName)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');

        foreach (char invalid in new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
            fileName = fileName.Replace(invalid, '_');

        string trimmed = fileName.Trim().TrimEnd('.');
        if (trimmed.Length > 120)
            trimmed = trimmed[..120];
        return string.IsNullOrWhiteSpace(trimmed) ? "attachment.bin" : trimmed;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the original error if cleanup fails.
        }
    }

    private sealed record MediaRecord(long Id, string LocalPath, string MediaType);
    private sealed record StorageFolders(string ImagesDirectory, string FilesDirectory);
    private sealed record SnapshotManifest(
        int ManifestVersion,
        string SnapshotId,
        DateTimeOffset CreatedAtUtc,
        string DatabaseFile,
        string DatabaseSha256,
        IReadOnlyList<SnapshotMediaFile> MediaFiles);
    private sealed record SnapshotMediaFile(
        string ArchivePath,
        string Sha256,
        long Size,
        IReadOnlyList<string> MediaTypes);

    private sealed record MediaArchiveEntry(
        string ArchivePath,
        string Sha256,
        long Size,
        HashSet<string> MediaTypes);
}

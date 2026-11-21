using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using oaiResponsesAvalonia.Data;
using oaiResponsesAvalonia.Services;

namespace oaiProtonSync.Services;

public sealed record RestoredAppCompatibilityResult(int Sessions, int Messages, int MediaReferences, int UniqueMediaFiles);

/// <summary>Checks an isolated restore with the main application's actual data model and media resolver.</summary>
public sealed class RestoredAppCompatibilityService
{
    public async Task<RestoredAppCompatibilityResult> CheckAsync(
        IsolatedSnapshotRestoreResult restore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restore);
        if (!File.Exists(restore.DatabasePath) || !Directory.Exists(restore.ImagesDirectory) || !Directory.Exists(restore.FilesDirectory))
            throw new InvalidDataException("The isolated database or its media directories are missing.");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = restore.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        await using var db = new AppDbContext(options);

        // Match the main app's history/logs query while never using its global database path.
        var sessions = await db.Sessions.AsNoTracking()
            .Include(session => session.Messages).ThenInclude(message => message.MediaFiles)
            .AsSplitQuery().ToListAsync(cancellationToken);
        int messages = sessions.Sum(session => session.Messages.Count);
        int references = sessions.Sum(session => session.Messages.Sum(message => message.MediaFiles.Count));
        if (messages != await db.Messages.CountAsync(cancellationToken) ||
            references != await db.Media.CountAsync(cancellationToken))
            throw new InvalidDataException("Some messages or media references are not associated with a session and cannot be read through the application's history view.");

        var storage = new MediaStorageService();
        storage.SetStorageFolders(restore.ImagesDirectory, restore.FilesDirectory);
        var checkedFiles = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        string images = Path.GetFullPath(restore.ImagesDirectory);
        string files = Path.GetFullPath(restore.FilesDirectory);
        foreach (var media in sessions.SelectMany(session => session.Messages).SelectMany(message => message.MediaFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = media.LocalPath ?? string.Empty;
            if (!path.StartsWith("media/", StringComparison.Ordinal) || path.Length == 6 ||
                path[6..].Contains('/') || path[6..].Contains('\\'))
                throw new InvalidDataException($"A restored media reference is not a portable media path: {path}");

            string? resolved = storage.ResolveMediaPath(path);
            if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
                throw new InvalidDataException($"The application's media resolver could not find: {path}");
            string fullPath = Path.GetFullPath(resolved);
            string? parent = Path.GetDirectoryName(fullPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(parent, images, comparison) && !string.Equals(parent, files, comparison))
                throw new InvalidDataException($"A media reference resolved outside the isolated restore: {path}");
            if (checkedFiles.Add(fullPath))
            {
                await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                _ = await SHA256.HashDataAsync(stream, cancellationToken);
            }
        }

        return new RestoredAppCompatibilityResult(sessions.Count, messages, references, checkedFiles.Count);
    }
}

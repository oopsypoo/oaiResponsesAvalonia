using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services;

/// <summary>Pushes and pulls numbered, verified SQLite snapshots through the Proton Drive CLI.</summary>
public sealed class ProtonDatabaseSyncService
{
    private static readonly SemaphoreSlim SyncLock = new(1, 1);
    private static readonly Regex SnapshotName = new(@"^db-(\d{6,})-(.+)\.db$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly AppSettings _settings;
    private readonly IRemoteStore _remote;
    private readonly string _databasePath;
    private readonly string _appRoot;

    public ProtonDatabaseSyncService(AppSettings settings, IRemoteStore remote, string? databasePath = null)
    {
        _settings = settings;
        _remote = remote;
        _databasePath = Path.GetFullPath(databasePath ?? oaiResponsesAvalonia.Data.AppDbContext.DatabaseFilePath);
        _appRoot = Path.GetFullPath(settings.AppRoot);
    }

    public Task<ProtonFileSyncResult> PullAsync(Action<string> log, CancellationToken cancellationToken = default) =>
        RunAsync(push: false, log, cancellationToken);

    public Task<ProtonFileSyncResult> PushAsync(Action<string> log, CancellationToken cancellationToken = default) =>
        RunAsync(push: true, log, cancellationToken);

    private async Task<ProtonFileSyncResult> RunAsync(bool push, Action<string> log, CancellationToken cancellationToken)
    {
        if (!_settings.ProtonDriveSyncEnabled)
            return new ProtonFileSyncResult(ProtonFileSyncStatus.Failed, "Proton Sync is disabled in settings.");
        if (!await SyncLock.WaitAsync(0, cancellationToken))
            return new ProtonFileSyncResult(ProtonFileSyncStatus.Failed, "Another Proton Sync operation is already running.");

        string workRoot = Path.Combine(_appRoot, "tmp", "proton-db-sync");
        string manifestPath = Path.Combine(_appRoot, "sync-state.json");
        try
        {
            Directory.CreateDirectory(workRoot);
            Directory.CreateDirectory(_appRoot);
            ProtonSyncManifest manifest = await ProtonSyncManifest.LoadAsync(manifestPath);
            await manifest.SaveAsync(manifestPath);
            log($"Database: {_databasePath}{Environment.NewLine}Local sync state: {manifestPath}{Environment.NewLine}");

            string remoteRoot = _settings.ProtonDriveRemoteRoot.TrimEnd('/');
            await _remote.EnsureFolderAsync(remoteRoot, "db", log, cancellationToken);
            string remoteDbFolder = remoteRoot + "/db";
            return push
                ? await PushCoreAsync(remoteDbFolder, manifest, manifestPath, workRoot, log, cancellationToken)
                : await PullCoreAsync(remoteDbFolder, manifest, manifestPath, workRoot, log, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new ProtonFileSyncResult(ProtonFileSyncStatus.Failed, "Database sync was cancelled.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log($"Database sync failed: {ex}{Environment.NewLine}");
            return new ProtonFileSyncResult(ProtonFileSyncStatus.Failed, ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true); }
            catch (IOException ex) { log($"Could not clean database sync temporary files: {ex.Message}{Environment.NewLine}"); }
            catch (UnauthorizedAccessException ex) { log($"Could not clean database sync temporary files: {ex.Message}{Environment.NewLine}"); }
            SyncLock.Release();
        }
    }

    private async Task<ProtonFileSyncResult> PushCoreAsync(string remoteFolder, ProtonSyncManifest manifest, string manifestPath,
        string workRoot, Action<string> log, CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
            return Failed("The local database does not exist.");

        IReadOnlyList<RemoteFileEntry> before = await _remote.ListTreeAsync(remoteFolder, log, cancellationToken);
        (int Number, RemoteFileEntry Entry)[] numbered = GetSnapshots(before);
        int highest = numbered.Select(item => item.Number).DefaultIfEmpty(0).Max();
        if (HasDuplicateNumber(numbered, highest))
            return Conflict($"Remote database folder has multiple snapshots numbered {highest}; no snapshot was uploaded.");

        if (manifest.DbSnapshotNumber is null)
        {
            if (highest != 0)
                return Conflict("A remote database snapshot already exists but this device has no synced database baseline. Pull first.");
        }
        else if (highest != manifest.DbSnapshotNumber.Value)
        {
            return Conflict($"Remote database number is {highest}, but this device last synced {manifest.DbSnapshotNumber}. Pull or resolve the remote snapshot before pushing.");
        }

        string snapshotPath = Path.Combine(workRoot, "local-snapshot.db");
        await CreateSnapshotAsync(_databasePath, snapshotPath, cancellationToken);
        string snapshotSha1 = await HashFileAsync(snapshotPath, cancellationToken);
        if (string.Equals(snapshotSha1, manifest.DbSha1, StringComparison.OrdinalIgnoreCase))
            return Ok("Local database is unchanged; no snapshot was uploaded.");

        int nextNumber = checked(highest + 1);
        string device = SanitizeDeviceId(manifest.DeviceId);
        string uploadPath = Path.Combine(workRoot, $"db-{nextNumber:D6}-{device}.db");
        File.Copy(snapshotPath, uploadPath, overwrite: false);
        ProtonCommandResult uploaded = await _remote.UploadAsync([uploadPath], remoteFolder, log, cancellationToken,
            ProtonFileConflictStrategy.Skip);
        EnsureCommandSuccess(uploaded);

        IReadOnlyList<RemoteFileEntry> after = await _remote.ListTreeAsync(remoteFolder, log, cancellationToken);
        (int Number, RemoteFileEntry Entry)[] afterNumbered = GetSnapshots(after);
        (int Number, RemoteFileEntry Entry)[] newSnapshots = afterNumbered.Where(item => item.Number == nextNumber).ToArray();
        if (newSnapshots.Length != 1)
            return Conflict($"Expected exactly one remote snapshot numbered {nextNumber}, found {newSnapshots.Length}. The local sync state was not advanced.");

        RemoteFileEntry confirmed = newSnapshots[0].Entry;
        if (confirmed.Size != new FileInfo(uploadPath).Length || !string.Equals(confirmed.Sha1, snapshotSha1, StringComparison.OrdinalIgnoreCase))
            return Failed("The uploaded database snapshot did not verify against its remote size and SHA-1.");

        manifest.DbSnapshotNumber = nextNumber;
        manifest.DbSha1 = snapshotSha1;
        await manifest.SaveAsync(manifestPath);
        log($"Uploaded and verified database snapshot {Path.GetFileName(uploadPath)}.{Environment.NewLine}");
        return Ok($"Database snapshot {nextNumber} uploaded and verified.");
    }

    private async Task<ProtonFileSyncResult> PullCoreAsync(string remoteFolder, ProtonSyncManifest manifest, string manifestPath,
        string workRoot, Action<string> log, CancellationToken cancellationToken)
    {
        IReadOnlyList<RemoteFileEntry> entries = await _remote.ListTreeAsync(remoteFolder, log, cancellationToken);
        (int Number, RemoteFileEntry Entry)[] numbered = GetSnapshots(entries);
        if (numbered.Length == 0)
            return Ok("No remote database snapshots exist yet.");

        int highest = numbered.Max(item => item.Number);
        if (HasDuplicateNumber(numbered, highest))
            return Conflict($"Remote database folder has multiple snapshots numbered {highest}; choose a snapshot manually before pulling.");
        if (manifest.DbSnapshotNumber == highest)
            return Ok($"Database snapshot {highest} is already current on this device.");
        if (manifest.DbSnapshotNumber is int known && highest < known)
            return Conflict($"Remote database snapshot {highest} is older than the locally recorded snapshot {known}; refusing rollback.");

        if (manifest.DbSnapshotNumber is not null)
        {
            if (!File.Exists(_databasePath))
                return Conflict("The local database recorded in sync state is missing; refusing to replace it automatically.");
            string currentSnapshot = Path.Combine(workRoot, "current-local.db");
            await CreateSnapshotAsync(_databasePath, currentSnapshot, cancellationToken);
            string currentSha1 = await HashFileAsync(currentSnapshot, cancellationToken);
            if (!string.Equals(currentSha1, manifest.DbSha1, StringComparison.OrdinalIgnoreCase))
                return Conflict("The local database changed since its last successful sync. The remote snapshot was not installed.");
        }

        RemoteFileEntry remote = numbered.Single(item => item.Number == highest).Entry;
        if (remote.Size is null || string.IsNullOrWhiteSpace(remote.Sha1))
            return Failed("The selected remote database snapshot is missing size or SHA-1 metadata.");

        string downloadRoot = Path.Combine(workRoot, "download");
        Directory.CreateDirectory(downloadRoot);
        ProtonCommandResult downloaded = await _remote.DownloadAsync([remote.RemotePath], downloadRoot, log, cancellationToken);
        EnsureCommandSuccess(downloaded);
        string downloadedPath = Path.Combine(downloadRoot, Path.GetFileName(remote.RemotePath));
        if (!File.Exists(downloadedPath))
            downloadedPath = Directory.GetFiles(downloadRoot, "*", SearchOption.AllDirectories).SingleOrDefault()
                ?? throw new FileNotFoundException("The downloaded database snapshot was not found in the temporary folder.");

        var downloadedInfo = new FileInfo(downloadedPath);
        string downloadedSha1 = await HashFileAsync(downloadedPath, cancellationToken);
        if (downloadedInfo.Length != remote.Size.Value || !string.Equals(downloadedSha1, remote.Sha1, StringComparison.OrdinalIgnoreCase))
            return Failed("The downloaded database snapshot failed size or SHA-1 verification; the current database was not changed.");

        string? databaseDirectory = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrWhiteSpace(databaseDirectory))
            return Failed("The local database path has no parent directory.");
        Directory.CreateDirectory(databaseDirectory);

        string backupDirectory = Path.Combine(_appRoot, "backups", "proton-sync", "database");
        Directory.CreateDirectory(backupDirectory);
        if (File.Exists(_databasePath))
        {
            string backupPath = Path.Combine(backupDirectory, $"localhistory-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.db");
            // A SQLite snapshot includes committed WAL contents and is safer than copying the live main file.
            await CreateSnapshotAsync(_databasePath, backupPath, cancellationToken);
            log($"Backed up current database to {backupPath}.{Environment.NewLine}");
        }

        // EF/SQLite may retain disposed connections in the connection pool. Close those handles
        // before replacing the database file so subsequent contexts cannot keep using the old inode.
        SqliteConnection.ClearAllPools();
        string installPath = Path.Combine(databaseDirectory, $".localhistory-{Guid.NewGuid():N}.db.tmp");
        File.Copy(downloadedPath, installPath, overwrite: false);
        string? rollbackPath = null;
        try
        {
            if (File.Exists(_databasePath))
            {
                rollbackPath = Path.Combine(databaseDirectory, $".localhistory-{Guid.NewGuid():N}.rollback");
                File.Move(_databasePath, rollbackPath);
            }
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                string sidecar = _databasePath + suffix;
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
            File.Move(installPath, _databasePath);
            if (rollbackPath is not null && File.Exists(rollbackPath)) File.Delete(rollbackPath);
        }
        catch
        {
            if (File.Exists(_databasePath)) File.Delete(_databasePath);
            if (rollbackPath is not null && File.Exists(rollbackPath)) File.Move(rollbackPath, _databasePath);
            throw;
        }
        finally
        {
            if (File.Exists(installPath)) File.Delete(installPath);
        }

        manifest.DbSnapshotNumber = highest;
        manifest.DbSha1 = downloadedSha1;
        await manifest.SaveAsync(manifestPath);
        log($"Installed and verified remote database snapshot {Path.GetFileName(remote.RemotePath)}.{Environment.NewLine}");
        return Ok($"Database snapshot {highest} pulled and installed. Restart the application to ensure all database users reopen the new file.");
    }

    private static async Task CreateSnapshotAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        string escapedDestination = destinationPath.Replace("'", "''", StringComparison.Ordinal);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly };
        await using var connection = new SqliteConnection(connectionString.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"VACUUM INTO '{escapedDestination}';";
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (!File.Exists(destinationPath))
            throw new IOException("SQLite did not create the requested database snapshot.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        byte[] hash = await SHA1.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static (int Number, RemoteFileEntry Entry)[] GetSnapshots(System.Collections.Generic.IReadOnlyList<RemoteFileEntry> entries) =>
        entries.Where(entry => !entry.IsDirectory)
            .Select(entry => (Entry: entry, Match: SnapshotName.Match(Path.GetFileName(entry.RelativePath))))
            .Where(item => item.Match.Success && int.TryParse(item.Match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(item => (int.Parse(item.Match.Groups[1].Value, CultureInfo.InvariantCulture), item.Entry))
            .ToArray();

    private static bool HasDuplicateNumber((int Number, RemoteFileEntry Entry)[] snapshots, int number) =>
        snapshots.Count(item => item.Number == number) > 1;

    private static string SanitizeDeviceId(string deviceId)
    {
        string safe = new(deviceId.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '@' ? ch : '-').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "unknown-device" : safe;
    }

    private static void EnsureCommandSuccess(ProtonCommandResult result)
    {
        if (!result.Succeeded)
            throw new IOException($"Command failed: {result.Command}{Environment.NewLine}{result.Output}");
    }

    private static ProtonFileSyncResult Ok(string summary) => new(ProtonFileSyncStatus.Ok, summary);
    private static ProtonFileSyncResult Failed(string summary) => new(ProtonFileSyncStatus.Failed, summary);
    private static ProtonFileSyncResult Conflict(string summary) => new(ProtonFileSyncStatus.Conflict, summary);
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services;

public enum ProtonFileSyncStatus
{
    Ok,
    Conflict,
    Failed
}

public sealed record ProtonFileSyncResult(ProtonFileSyncStatus Status, string Summary);

public enum ProtonFileConflictStrategy
{
    Skip,
    Replace
}

public interface IRemoteStore
{
    Task EnsureFolderAsync(string parentFolder, string name, Action<string> log, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteFileEntry>> ListTreeAsync(string remoteFolder, Action<string> log, CancellationToken cancellationToken = default);
    Task<ProtonCommandResult> UploadAsync(IReadOnlyList<string> localPaths, string remoteFolder, Action<string> log, CancellationToken cancellationToken = default, ProtonFileConflictStrategy fileConflictStrategy = ProtonFileConflictStrategy.Skip);
    Task<ProtonCommandResult> DownloadAsync(IReadOnlyList<string> remotePaths, string localFolder, Action<string> log, CancellationToken cancellationToken = default);
    Task<ProtonCommandResult> TrashAsync(string remotePath, Action<string> log, CancellationToken cancellationToken = default);
}

public sealed record RemoteFileEntry(string RelativePath, string RemotePath, bool IsDirectory, long? Size, string? Sha1, DateTimeOffset? ModifiedUtc);

/// <summary>Runs the official proton-drive CLI using explicit non-interactive strategy options.</summary>
public sealed class ProtonDriveRemoteStore(string executable) : IRemoteStore
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);
    private readonly string _executable = string.IsNullOrWhiteSpace(executable) ? "proton-drive" : executable.Trim();

    public async Task EnsureFolderAsync(string parentFolder, string name, Action<string> log, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("A remote folder name must be a single path component.", nameof(name));

        IReadOnlyList<(string Name, bool IsDirectory)> children = await ListImmediateChildrenAsync(parentFolder, log, cancellationToken);
        (string Name, bool IsDirectory)? existing = children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.Ordinal));
        if (existing is { } node && node.Name is not null)
        {
            if (!node.IsDirectory)
                throw new IOException($"Remote path '{JoinRemote(parentFolder, name)}' already exists and is not a folder.");
            log($"Remote folder already exists: '{JoinRemote(parentFolder, name)}'.{Environment.NewLine}");
            return;
        }

        ProtonCommandResult create = await RunAsync(["filesystem", "create-folder", parentFolder, name], log, cancellationToken);
        if (!create.Succeeded)
        {
            // A concurrent sync may have created it after our list. Accept only if the
            // resulting node now exists as a folder; otherwise preserve the CLI failure.
            children = await ListImmediateChildrenAsync(parentFolder, log, cancellationToken);
            existing = children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.Ordinal));
            if (existing is not { IsDirectory: true })
                EnsureSuccess(create);
        }

        children = await ListImmediateChildrenAsync(parentFolder, log, cancellationToken);
        existing = children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.Ordinal));
        if (existing is not { IsDirectory: true })
            throw new IOException($"Folder creation did not produce the expected remote folder '{JoinRemote(parentFolder, name)}'.");
        log($"Created remote folder '{JoinRemote(parentFolder, name)}'.{Environment.NewLine}");
    }

    private async Task<IReadOnlyList<(string Name, bool IsDirectory)>> ListImmediateChildrenAsync(string remoteFolder, Action<string> log, CancellationToken cancellationToken)
    {
        ProtonCommandResult result = await RunAsync(["filesystem", "list", remoteFolder, "-j"], log, cancellationToken);
        EnsureSuccess(result);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Expected a JSON array from list for '{remoteFolder}'.");

        var children = new List<(string Name, bool IsDirectory)>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string? childName = ReadName(item);
            if (string.IsNullOrWhiteSpace(childName))
            {
                log($"Skipped a remote item in '{remoteFolder}' because name.ok was false or its name was empty.{Environment.NewLine}");
                continue;
            }
            string type = ReadString(item, "type") ?? string.Empty;
            children.Add((childName, type.Equals("folder", StringComparison.OrdinalIgnoreCase) || type.Equals("directory", StringComparison.OrdinalIgnoreCase)));
        }
        return children;
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListTreeAsync(string remoteFolder, Action<string> log, CancellationToken cancellationToken = default)
    {
        var allEntries = new Dictionary<string, RemoteFileEntry>(StringComparer.Ordinal);
        var pending = new Queue<(string Remote, string Relative)>();
        pending.Enqueue((remoteFolder, string.Empty));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, relativeFolder) = pending.Dequeue();
            var result = await RunAsync(["filesystem", "list", folder, "-j"], log, cancellationToken);
            EnsureSuccess(result);

            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Expected a JSON array from list for '{folder}'.");

            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                string? name = ReadName(item);
                if (string.IsNullOrWhiteSpace(name))
                {
                    log($"Skipped a remote item in '{folder}' because name.ok was false or its name was empty.");
                    continue;
                }

                if (name is "." or ".." || name.Contains('/') || name.Contains('\\'))
                {
                    log($"Skipped remote name '{name}' because it cannot be represented safely as a path component.");
                    continue;
                }

                string relativePath = string.IsNullOrEmpty(relativeFolder) ? name : $"{relativeFolder}/{name}";
                string remotePath = JoinRemote(folder, EscapeRemoteComponent(name));
                string type = ReadString(item, "type") ?? string.Empty;
                bool isDirectory = type.Equals("folder", StringComparison.OrdinalIgnoreCase) || type.Equals("directory", StringComparison.OrdinalIgnoreCase);

                if (isDirectory)
                {
                    allEntries[relativePath] = new RemoteFileEntry(relativePath, remotePath, true, null, null, null);
                    pending.Enqueue((remotePath, relativePath));
                    continue;
                }

                JsonElement revision = default;
                if (!item.TryGetProperty("activeRevision", out revision) || revision.ValueKind != JsonValueKind.Object ||
                    !revision.TryGetProperty("claimedSize", out JsonElement sizeElement) || !TryReadLong(sizeElement, out long size) ||
                    !revision.TryGetProperty("claimedModificationTime", out JsonElement timeElement) ||
                    !revision.TryGetProperty("claimedDigests", out JsonElement digests) ||
                    !digests.TryGetProperty("sha1", out JsonElement shaElement) || !TryReadStringValue(shaElement, out string? parsedSha1))
                {
                    log($"Skipped remote file '{remotePath}' because required revision metadata is missing.");
                    continue;
                }

                string? sha1 = parsedSha1;
                if (string.IsNullOrWhiteSpace(sha1) || size < 0 || !TryReadDate(timeElement, out DateTimeOffset modified))
                {
                    log($"Skipped remote file '{remotePath}' because its size, SHA-1, or modification time is invalid.");
                    continue;
                }

                allEntries[relativePath] = new RemoteFileEntry(relativePath, remotePath, false, size, sha1, modified);
            }
        }

        return allEntries.Values.ToArray();
    }

    public Task<ProtonCommandResult> UploadAsync(IReadOnlyList<string> localPaths, string remoteFolder, Action<string> log, CancellationToken cancellationToken = default, ProtonFileConflictStrategy fileConflictStrategy = ProtonFileConflictStrategy.Skip)
    {
        if (localPaths.Count == 0)
            throw new ArgumentException("At least one local path is required.", nameof(localPaths));
        string strategy = fileConflictStrategy == ProtonFileConflictStrategy.Replace ? "replace" : "skip";
        var args = new List<string> { "filesystem", "upload", "-f", strategy, "-d", "merge" };
        args.AddRange(localPaths);
        args.Add(remoteFolder);
        return RunAsync(args, log, cancellationToken);
    }

    public Task<ProtonCommandResult> DownloadAsync(IReadOnlyList<string> remotePaths, string localFolder, Action<string> log, CancellationToken cancellationToken = default)
    {
        if (remotePaths.Count == 0)
            throw new ArgumentException("At least one remote path is required.", nameof(remotePaths));
        var args = new List<string> { "filesystem", "download", "-f", "skip", "-d", "merge" };
        args.AddRange(remotePaths);
        args.Add(localFolder);
        return RunAsync(args, log, cancellationToken);
    }

    public Task<ProtonCommandResult> TrashAsync(string remotePath, Action<string> log, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || !remotePath.StartsWith("/", StringComparison.Ordinal) || remotePath.Contains('\\'))
            throw new ArgumentException("A remote trash path must be an absolute POSIX path.", nameof(remotePath));
        return RunAsync(["filesystem", "trash", remotePath], log, cancellationToken);
    }

    private async Task<ProtonCommandResult> RunAsync(IReadOnlyList<string> args, Action<string> log, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        string command = _executable + " " + string.Join(" ", args.Select(QuoteForLog));
        log($"> {command}{Environment.NewLine}");
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new ProtonCommandResult(command, null, false, "Failed to start process.", string.Empty);
            process.StandardInput.Close();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task exitTask = process.WaitForExitAsync(timeoutCts.Token);
            Task timeoutTask = Task.Delay(CommandTimeout, cancellationToken);
            Task completed = await Task.WhenAny(exitTask, timeoutTask);

            if (completed != exitTask)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                string timedOutText = cancellationToken.IsCancellationRequested ? "Command cancelled; process terminated." : "Command timed out after 10 minutes; process terminated.";
                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                string output = FormatOutput(stdout, stderr, process.ExitCode, timedOutText);
                log(output);
                return new ProtonCommandResult(command, process.ExitCode, !cancellationToken.IsCancellationRequested, output, stdout);
            }

            await exitTask;
            string standardOutput = await stdoutTask;
            string standardError = await stderrTask;
            string formatted = FormatOutput(standardOutput, standardError, process.ExitCode, null);
            log(formatted);
            return new ProtonCommandResult(command, process.ExitCode, false, formatted, standardOutput);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string text = $"Error: {ex.Message}{Environment.NewLine}";
            log(text);
            return new ProtonCommandResult(command, null, false, text, string.Empty);
        }
    }

    private static string FormatOutput(string stdout, string stderr, int exitCode, string? message)
    {
        var output = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(message)) output.AppendLine(message);
        if (!string.IsNullOrWhiteSpace(stdout)) output.AppendLine("[stdout]").AppendLine(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr)) output.AppendLine("[stderr]").AppendLine(stderr.TrimEnd());
        output.AppendLine($"Exit code: {exitCode}");
        return output.ToString();
    }

    private static void EnsureSuccess(ProtonCommandResult result)
    {
        if (!result.Succeeded)
            throw new IOException($"Command failed: {result.Command}{Environment.NewLine}{result.Output}");
    }

    private static string? ReadName(JsonElement item)
    {
        if (!item.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.Object ||
            !name.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.True ||
            !name.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out JsonElement nested) && nested.ValueKind == JsonValueKind.String)
            return nested.GetString();
        return null;
    }

    private static bool TryReadLong(JsonElement value, out long result)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind != JsonValueKind.True)
            {
                result = 0;
                return false;
            }
            if (!value.TryGetProperty("value", out value))
            {
                result = 0;
                return false;
            }
        }
        return value.TryGetInt64(out result);
    }

    private static bool TryReadStringValue(JsonElement value, out string? result)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if ((value.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind != JsonValueKind.True) || !value.TryGetProperty("value", out value))
            {
                result = null;
                return false;
            }
        }
        result = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(result);
    }

    private static bool TryReadDate(JsonElement value, out DateTimeOffset date)
    {
        string? text = null;
        if (value.ValueKind == JsonValueKind.String)
            text = value.GetString();
        else if (value.ValueKind == JsonValueKind.Object &&
                 (!value.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind == JsonValueKind.True) &&
                 value.TryGetProperty("value", out JsonElement nested) && nested.ValueKind == JsonValueKind.String)
            text = nested.GetString();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date))
            return true;
        date = default;
        return false;
    }

    private static string EscapeRemoteComponent(string name) => name.Replace("%", "%25", StringComparison.Ordinal).Replace("/", "%2F", StringComparison.Ordinal);
    private static string JoinRemote(string parent, string name) => $"{parent.TrimEnd('/')}/{name}";
    private static string QuoteForLog(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}

/// <summary>
/// Syncs immutable file areas additively and reconciles mutable user files against a local manifest baseline.
/// </summary>
public sealed class ProtonFileSyncService
{
    private static readonly SemaphoreSlim SyncLock = new(1, 1);
    private readonly AppSettings _settings;
    private readonly IRemoteStore _remote;
    private readonly string _appRoot;

    public ProtonFileSyncService(AppSettings settings, IRemoteStore remote)
    {
        _settings = settings;
        _remote = remote;
        _appRoot = Path.GetFullPath(settings.AppRoot);
    }

    public Task<ProtonFileSyncResult> PullAsync(Action<string> log, CancellationToken cancellationToken = default) => RunAsync(push: false, log, cancellationToken);
    public Task<ProtonFileSyncResult> PushAsync(Action<string> log, CancellationToken cancellationToken = default) => RunAsync(push: true, log, cancellationToken);

    private async Task<ProtonFileSyncResult> RunAsync(bool push, Action<string> log, CancellationToken cancellationToken)
    {
        if (!_settings.ProtonDriveSyncEnabled)
            return new ProtonFileSyncResult(ProtonFileSyncStatus.Failed, "Proton Sync is disabled in settings.");
        if (!await SyncLock.WaitAsync(0, cancellationToken))
            return new ProtonFileSyncResult(ProtonFileSyncStatus.Failed, "Another Proton Sync operation is already running.");

        try
        {
            string imagesPath = Path.GetFullPath(Path.Combine(_appRoot, _settings.ImagesFolder));
            string filesPath = Path.GetFullPath(Path.Combine(_appRoot, _settings.FilesFolder));
            string userPath = _settings.GetSavedImagesDirectory(imagesPath);
            EnsureUnderRoot(imagesPath, _appRoot);
            EnsureUnderRoot(filesPath, _appRoot);
            var areas = new List<SyncArea>
            {
                new("images", imagesPath, userPath),
                new("files", filesPath, null),
                new("user", userPath, null)
            };
            string manifestPath = Path.Combine(_appRoot, "sync-state.json");
            ProtonSyncManifest manifest = await ProtonSyncManifest.LoadAsync(manifestPath);
            await manifest.SaveAsync(manifestPath);
            log($"Local sync manifest: {manifestPath}{Environment.NewLine}");
            int conflicts = 0;
            foreach (SyncArea area in areas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                conflicts += await SyncAreaAsync(area, push, manifest, log, cancellationToken);
            }
            await manifest.SaveAsync(Path.Combine(_appRoot, "sync-state.json"));
            string summary = conflicts == 0
                ? $"{(push ? "Push" : "Pull")} of regular files completed."
                : $"{(push ? "Push" : "Pull")} completed with {conflicts} differing-path conflict(s); neither side was overwritten.";
            return new ProtonFileSyncResult(conflicts == 0 ? ProtonFileSyncStatus.Ok : ProtonFileSyncStatus.Conflict, summary);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log($"Sync failed: {ex.Message}{Environment.NewLine}");
            return new ProtonFileSyncResult(ProtonFileSyncStatus.Failed, ex.Message);
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private async Task<int> SyncAreaAsync(SyncArea area, bool push, ProtonSyncManifest manifest, Action<string> log, CancellationToken cancellationToken)
    {
        string remoteRoot = $"{_settings.ProtonDriveRemoteRoot.TrimEnd('/')}/{area.RemoteName}";
        log($"{(push ? "Push" : "Pull")} area '{area.RemoteName}' from '{area.LocalRoot}'.{Environment.NewLine}");
        Directory.CreateDirectory(area.LocalRoot);
        await _remote.EnsureFolderAsync(_settings.ProtonDriveRemoteRoot.TrimEnd('/'), area.RemoteName, log, cancellationToken);
        IReadOnlyList<RemoteFileEntry> remoteTree = FilterPortableRemoteEntries(
            await _remote.ListTreeAsync(remoteRoot, log, cancellationToken), log);
        LocalScan localTree = ScanLocal(area, log);
        int conflicts = 0;
        if (area.RemoteName == "user")
        {
            conflicts += await ReconcileUserDeletionsAsync(area, localTree, remoteTree, manifest, log, cancellationToken);
            remoteTree = FilterPortableRemoteEntries(await _remote.ListTreeAsync(remoteRoot, log, cancellationToken), log);
            localTree = ScanLocal(area, log);
            conflicts += await ReconcileUserFilesAsync(area, remoteRoot, localTree, remoteTree, push, manifest, log, cancellationToken);
            return conflicts;
        }

        // Pull only absent files. Every transfer uses a fresh empty temporary folder and verification.
        foreach (RemoteFileEntry remoteFile in remoteTree.Where(e => !e.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!localTree.Files.TryGetValue(remoteFile.RelativePath, out string? localPath))
            {
                if (push) continue;
                if (area.RemoteName == "user" && manifest.Files.ContainsKey(remoteFile.RelativePath))
                {
                    log($"Local deletion candidate '{area.RemoteName}/{remoteFile.RelativePath}' retained in the manifest; not re-downloading or deleting anything until safe trash support is available.{Environment.NewLine}");
                    continue;
                }
                await DownloadVerifiedAsync(area, remoteFile, log, cancellationToken);
            }
            else if (!push && !string.Equals(await Sha1Async(localPath), remoteFile.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                conflicts++;
                log($"Conflict/anomaly: '{area.RemoteName}/{remoteFile.RelativePath}' exists on both sides with different SHA-1 values. Neither side was overwritten.{Environment.NewLine}");
            }
        }

        if (!push)
        {
            if (area.RemoteName == "user")
                await RefreshUserManifestAsync(area, manifest, log, cancellationToken);
            return conflicts;
        }

        // Upload only files absent remotely. Existing differing paths are intentionally never replaced.
        localTree = ScanLocal(area, log);
        var remoteFiles = remoteTree.Where(e => !e.IsDirectory).ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var remoteDirectories = remoteTree.Where(e => e.IsDirectory).Select(e => e.RelativePath).ToHashSet(StringComparer.Ordinal);
        var uploadedDirectoryRoots = new HashSet<string>(StringComparer.Ordinal);
        var uploadedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string relativePath, string localPath) in localTree.Files.OrderBy(pair => pair.Key.Count(c => c == '/')))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remoteFiles.TryGetValue(relativePath, out RemoteFileEntry? remoteFile))
            {
                if (!string.Equals(await Sha1Async(localPath), remoteFile.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts++;
                    log($"Conflict/anomaly: '{area.RemoteName}/{relativePath}' exists on both sides with different SHA-1 values. Neither side was overwritten.{Environment.NewLine}");
                }
                continue;
            }

            if (area.RemoteName == "user" && manifest.Files.ContainsKey(relativePath))
            {
                log($"Remote deletion candidate '{area.RemoteName}/{relativePath}' retained in the manifest; not uploading or deleting anything until safe trash support is available.{Environment.NewLine}");
                continue;
            }

            string parentRelative = ParentRelative(relativePath);
            if (parentRelative.Length > 0 && !remoteDirectories.Contains(parentRelative))
            {
                // Upload the topmost missing local directory. The CLI's -d merge strategy is
                // specifically intended to create that directory and carry its contents.
                string topmostMissing = parentRelative;
                while (ParentRelative(topmostMissing).Length > 0 && !remoteDirectories.Contains(ParentRelative(topmostMissing)))
                    topmostMissing = ParentRelative(topmostMissing);
                if (!uploadedDirectoryRoots.Add(topmostMissing))
                    continue;

                string targetParentRelative = ParentRelative(topmostMissing);
                string targetRemoteParent = JoinRemote(remoteRoot, targetParentRelative);
                string localDirectory = ResolveLocal(area.LocalRoot, topmostMissing);
                ProtonCommandResult directoryResult = await _remote.UploadAsync([localDirectory], targetRemoteParent, log, cancellationToken);
                EnsureSucceeded(directoryResult);
                remoteTree = FilterPortableRemoteEntries(await _remote.ListTreeAsync(remoteRoot, log, cancellationToken), log);
                remoteFiles = remoteTree.Where(e => !e.IsDirectory).ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
                remoteDirectories = remoteTree.Where(e => e.IsDirectory).Select(e => e.RelativePath).ToHashSet(StringComparer.Ordinal);
                foreach (string uploaded in localTree.Files.Keys.Where(path => path.StartsWith(topmostMissing + "/", StringComparison.Ordinal)))
                    uploadedFiles.Add(uploaded);
                continue;
            }

            string remoteParent = JoinRemote(remoteRoot, parentRelative);
            ProtonCommandResult result = await _remote.UploadAsync([localPath], remoteParent, log, cancellationToken);
            EnsureSucceeded(result);
            uploadedFiles.Add(relativePath);
        }

        // Confirm uploads via a fresh recursive listing. Missing items indicate a transfer anomaly.
        IReadOnlyList<RemoteFileEntry> confirmed = await _remote.ListTreeAsync(remoteRoot, log, cancellationToken);
        var confirmedFiles = confirmed.Where(e => !e.IsDirectory).ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        foreach (string relativePath in uploadedFiles)
        {
            string localPath = localTree.Files[relativePath];
            if (!confirmedFiles.TryGetValue(relativePath, out RemoteFileEntry? item))
                throw new IOException($"Upload verification failed: remote listing does not contain '{area.RemoteName}/{relativePath}'.");
            if (!string.Equals(await Sha1Async(localPath), item.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                conflicts++;
                log($"Upload verification found differing content at '{area.RemoteName}/{relativePath}'; neither side was overwritten.{Environment.NewLine}");
            }
        }

        if (area.RemoteName == "user")
            await RefreshUserManifestAsync(area, manifest, log, cancellationToken);

        return conflicts;
    }

    private async Task<int> ReconcileUserFilesAsync(
        SyncArea area,
        string remoteRoot,
        LocalScan local,
        IReadOnlyList<RemoteFileEntry> remoteEntries,
        bool push,
        ProtonSyncManifest manifest,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var remoteFiles = remoteEntries.Where(entry => !entry.IsDirectory)
            .ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        var paths = local.Files.Keys.Concat(remoteFiles.Keys).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        int conflicts = 0;

        foreach (string relativePath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasLocal = local.Files.TryGetValue(relativePath, out string? localPath);
            bool hasRemote = remoteFiles.TryGetValue(relativePath, out RemoteFileEntry? remoteFile);
            bool hasBaseline = manifest.Files.TryGetValue(relativePath, out ProtonSyncManifestEntry? baseline);

            // Deletion decisions are handled separately and conservatively above.
            if (!hasLocal || !hasRemote)
            {
                if (hasLocal && push && !hasBaseline)
                {
                    string localHash = await Sha1Async(localPath!);
                    await UploadNewUserFileAsync(area, remoteRoot, relativePath, localPath!, localHash, manifest, log, cancellationToken);
                }
                else if (hasRemote && !push && !hasBaseline)
                {
                    await DownloadVerifiedAsync(area, remoteFile!, log, cancellationToken);
                    string destination = ResolveLocal(area.LocalRoot, relativePath);
                    if (!File.Exists(destination) || !string.Equals(await Sha1Async(destination), remoteFile!.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        conflicts++;
                        log($"Could not establish a verified local copy of user file '{relativePath}'.{Environment.NewLine}");
                        continue;
                    }
                    manifest.Files[relativePath] = NewManifestEntry(remoteFile.Sha1!, remoteFile.Size!.Value, destination);
                }
                continue;
            }

            if (remoteFile!.Sha1 is null || remoteFile.Size is null)
            {
                log($"Skipped user file '{relativePath}' because remote metadata cannot be verified.{Environment.NewLine}");
                continue;
            }

            FileInfo localInfo = new(localPath!);
            if (!localInfo.Exists)
                continue;
            string localSha = await Sha1Async(localPath!);
            string remoteSha = remoteFile.Sha1;

            if (!hasBaseline)
            {
                if (string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase))
                    manifest.Files[relativePath] = NewManifestEntry(remoteSha, localInfo.Length, localPath!);
                else
                {
                    conflicts++;
                    log($"Untracked user-file conflict at '{relativePath}': local and remote content differs; neither copy was changed.{Environment.NewLine}");
                }
                continue;
            }

            bool localChanged = !string.Equals(localSha, baseline!.BaseSha1, StringComparison.OrdinalIgnoreCase);
            bool remoteChanged = !string.Equals(remoteSha, baseline.BaseSha1, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase))
            {
                manifest.Files[relativePath] = NewManifestEntry(remoteSha, localInfo.Length, localPath!);
                continue;
            }

            if (!localChanged && !remoteChanged)
            {
                // Bytes are still the baseline on both sides; refresh local metadata only.
                manifest.Files[relativePath] = NewManifestEntry(baseline.BaseSha1, localInfo.Length, localPath!);
                continue;
            }

            if (localChanged && !remoteChanged)
            {
                IReadOnlyList<RemoteFileEntry> secondListing = await _remote.ListTreeAsync(remoteRoot, log, cancellationToken);
                RemoteFileEntry? currentRemote = secondListing.FirstOrDefault(entry => !entry.IsDirectory && entry.RelativePath == relativePath);
                if (currentRemote is null || !string.Equals(currentRemote.Sha1, baseline.BaseSha1, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts++;
                    log($"Local edit was not uploaded because remote '{relativePath}' changed during reconciliation.{Environment.NewLine}");
                    continue;
                }

                string remoteParent = JoinRemote(remoteRoot, ParentRelative(relativePath));
                ProtonCommandResult upload = await _remote.UploadAsync([localPath!], remoteParent, log, cancellationToken, ProtonFileConflictStrategy.Replace);
                EnsureSucceeded(upload);
                IReadOnlyList<RemoteFileEntry> afterUpload = await _remote.ListTreeAsync(remoteRoot, log, cancellationToken);
                RemoteFileEntry? confirmed = afterUpload.FirstOrDefault(entry => !entry.IsDirectory && entry.RelativePath == relativePath);
                if (confirmed is null || !string.Equals(confirmed.Sha1, localSha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Replacement verification failed for user file '{relativePath}'. The baseline was not advanced.");
                manifest.Files[relativePath] = NewManifestEntry(confirmed.Sha1!, confirmed.Size!.Value, localPath!);
                log($"Replaced remote user file after a local-only edit: '{relativePath}'.{Environment.NewLine}");
                continue;
            }

            if (!localChanged && remoteChanged)
            {
                if (!File.Exists(localPath) || !string.Equals(await Sha1Async(localPath!), baseline.BaseSha1, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts++;
                    log($"Remote edit was not downloaded because local '{relativePath}' changed during reconciliation.{Environment.NewLine}");
                    continue;
                }
                await DownloadVerifiedAsync(area, remoteFile, log, cancellationToken, overwriteExisting: true, expectedLocalSha1: baseline.BaseSha1);
                if (!File.Exists(localPath) || !string.Equals(await Sha1Async(localPath!), remoteSha, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts++;
                    log($"Remote edit was not installed because local '{relativePath}' changed during download.{Environment.NewLine}");
                    continue;
                }
                manifest.Files[relativePath] = NewManifestEntry(remoteSha, remoteFile.Size.Value, localPath!);
                log($"Downloaded remote-only user edit: '{relativePath}'.{Environment.NewLine}");
                continue;
            }

            // Both sides changed to different content. Preserve the local copy under a unique
            // conflict name, install the verified remote copy at the original path, and sync
            // the conflict copy as a separate new file.
            string conflictRelative = MakeConflictRelativePath(relativePath, manifest.DeviceId);
            string conflictLocalPath = ResolveLocal(area.LocalRoot, conflictRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(conflictLocalPath)!);
            File.Move(localPath!, conflictLocalPath);
            await DownloadVerifiedAsync(area, remoteFile, log, cancellationToken);
            string originalLocalPath = ResolveLocal(area.LocalRoot, relativePath);
            if (!File.Exists(originalLocalPath) || !string.Equals(await Sha1Async(originalLocalPath), remoteSha, StringComparison.OrdinalIgnoreCase))
            {
                conflicts++;
                log($"Conflict copy was preserved as '{conflictRelative}', but remote content could not be installed at '{relativePath}'.{Environment.NewLine}");
                continue;
            }

            IReadOnlyList<RemoteFileEntry> beforeConflictUpload = await _remote.ListTreeAsync(remoteRoot, log, cancellationToken);
            if (!beforeConflictUpload.Any(entry => !entry.IsDirectory && entry.RelativePath == conflictRelative))
            {
                await EnsureRemoteParentExistsAsync(remoteRoot, conflictRelative, log, cancellationToken);
                string parent = JoinRemote(remoteRoot, ParentRelative(conflictRelative));
                ProtonCommandResult conflictUpload = await _remote.UploadAsync([conflictLocalPath], parent, log, cancellationToken);
                EnsureSucceeded(conflictUpload);
            }
            IReadOnlyList<RemoteFileEntry> afterConflictUpload = await _remote.ListTreeAsync(remoteRoot, log, cancellationToken);
            RemoteFileEntry? remoteConflict = afterConflictUpload.FirstOrDefault(entry => !entry.IsDirectory && entry.RelativePath == conflictRelative);
            if (remoteConflict is null || !string.Equals(remoteConflict.Sha1, localSha, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Could not verify preserved conflict copy '{conflictRelative}'.");

            manifest.Files[relativePath] = NewManifestEntry(remoteSha, remoteFile.Size.Value, originalLocalPath);
            manifest.Files[conflictRelative] = NewManifestEntry(remoteConflict.Sha1!, remoteConflict.Size!.Value, conflictLocalPath);
            conflicts++;
            log($"Both sides changed '{relativePath}'. Preserved local content as '{conflictRelative}' and kept remote content at the original path.{Environment.NewLine}");
        }

        return conflicts;
    }

    private async Task UploadNewUserFileAsync(
        SyncArea area,
        string remoteRoot,
        string relativePath,
        string localPath,
        string localSha,
        ProtonSyncManifest manifest,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        await EnsureRemoteParentExistsAsync(remoteRoot, relativePath, log, cancellationToken);
        string remoteParent = JoinRemote(remoteRoot, ParentRelative(relativePath));
        ProtonCommandResult upload = await _remote.UploadAsync([localPath], remoteParent, log, cancellationToken);
        EnsureSucceeded(upload);
        IReadOnlyList<RemoteFileEntry> listing = await _remote.ListTreeAsync(remoteRoot, log, cancellationToken);
        RemoteFileEntry? confirmed = listing.FirstOrDefault(entry => !entry.IsDirectory && entry.RelativePath == relativePath);
        if (confirmed is null || !string.Equals(confirmed.Sha1, localSha, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Upload verification failed for user file '{relativePath}'.");
        manifest.Files[relativePath] = NewManifestEntry(confirmed.Sha1!, confirmed.Size!.Value, localPath);
    }

    private async Task EnsureRemoteParentExistsAsync(string remoteRoot, string relativePath, Action<string> log, CancellationToken cancellationToken)
    {
        string[] components = ParentRelative(relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        string current = remoteRoot;
        foreach (string component in components)
        {
            IReadOnlyList<RemoteFileEntry> children = await _remote.ListTreeAsync(current, log, cancellationToken);
            if (!children.Any(entry => entry.IsDirectory && string.Equals(entry.RelativePath, component, StringComparison.Ordinal)))
                await _remote.EnsureFolderAsync(current, component, log, cancellationToken);
            current = JoinRemote(current, component);
        }
    }

    private static ProtonSyncManifestEntry NewManifestEntry(string sha1, long size, string localPath) => new()
    {
        BaseSha1 = sha1,
        Size = size,
        LocalMtimeTicks = File.GetLastWriteTimeUtc(localPath).Ticks
    };

    private static string MakeConflictRelativePath(string relativePath, string deviceId)
    {
        string directory = ParentRelative(relativePath);
        string fileName = Path.GetFileName(relativePath);
        string extension = Path.GetExtension(fileName);
        string stem = fileName[..^extension.Length];
        string safeDevice = string.Concat(deviceId.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
        string suffix = $".conflict-{safeDevice}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}";
        string conflictName = stem + suffix + extension;
        return directory.Length == 0 ? conflictName : directory + "/" + conflictName;
    }

    private async Task<int> ReconcileUserDeletionsAsync(
        SyncArea area,
        LocalScan local,
        IReadOnlyList<RemoteFileEntry> remoteEntries,
        ProtonSyncManifest manifest,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var remoteFiles = remoteEntries.Where(entry => !entry.IsDirectory)
            .ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        int conflicts = 0;

        foreach ((string relativePath, ProtonSyncManifestEntry baseline) in manifest.Files.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string safeLocalPath = ResolveLocal(area.LocalRoot, relativePath);
            bool hasLocal = local.Files.TryGetValue(relativePath, out string? localPath);
            bool hasRemote = remoteFiles.TryGetValue(relativePath, out RemoteFileEntry? remoteFile);
            if (hasLocal && hasRemote)
                continue;

            string remotePath = $"{_settings.ProtonDriveRemoteRoot.TrimEnd('/')}/user/{relativePath}";
            if (!hasLocal && hasRemote)
            {
                if (!string.Equals(remoteFile!.Sha1, baseline.BaseSha1, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts++;
                    log($"Deletion conflict for 'user/{relativePath}': remote content changed since the manifest baseline; retaining it.{Environment.NewLine}");
                    continue;
                }

                // Recheck both sides immediately before trashing. Proton Trash is reversible,
                // but the command must never target a file that appeared or changed meanwhile.
                if (File.Exists(safeLocalPath))
                {
                    conflicts++;
                    log($"Deletion cancelled for 'user/{relativePath}': a local file reappeared.{Environment.NewLine}");
                    continue;
                }
                IReadOnlyList<RemoteFileEntry> secondRemote = await _remote.ListTreeAsync(
                    $"{_settings.ProtonDriveRemoteRoot.TrimEnd('/')}/user", log, cancellationToken);
                RemoteFileEntry? confirmed = secondRemote.FirstOrDefault(entry => !entry.IsDirectory &&
                    string.Equals(entry.RelativePath, relativePath, StringComparison.Ordinal));
                if (confirmed is null || !string.Equals(confirmed.Sha1, baseline.BaseSha1, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts++;
                    log($"Deletion cancelled for 'user/{relativePath}': remote listing changed during confirmation.{Environment.NewLine}");
                    continue;
                }

                ProtonCommandResult trash = await _remote.TrashAsync(confirmed.RemotePath, log, cancellationToken);
                EnsureSucceeded(trash);
                manifest.Files.Remove(relativePath);
                log($"Moved remotely deleted local counterpart to Proton Drive trash: '{remotePath}'.{Environment.NewLine}");
                continue;
            }

            if (hasLocal && !hasRemote)
            {
                string currentHash = await Sha1Async(localPath!);
                if (!string.Equals(currentHash, baseline.BaseSha1, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts++;
                    log($"Deletion conflict for 'user/{relativePath}': local content changed since the manifest baseline; retaining it.{Environment.NewLine}");
                    continue;
                }

                IReadOnlyList<RemoteFileEntry> secondRemote = await _remote.ListTreeAsync(
                    $"{_settings.ProtonDriveRemoteRoot.TrimEnd('/')}/user", log, cancellationToken);
                if (secondRemote.Any(entry => !entry.IsDirectory && string.Equals(entry.RelativePath, relativePath, StringComparison.Ordinal)) ||
                    !File.Exists(localPath) || !string.Equals(await Sha1Async(localPath!), baseline.BaseSha1, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts++;
                    log($"Deletion cancelled for 'user/{relativePath}': one side changed during confirmation.{Environment.NewLine}");
                    continue;
                }

                string backupRoot = Path.Combine(_appRoot, "backups", "proton-sync", "user");
                string backupDirectory = Path.GetFullPath(Path.Combine(backupRoot, Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty));
                EnsureUnderRoot(backupDirectory, backupRoot);
                Directory.CreateDirectory(backupDirectory);
                string backupPath = Path.Combine(backupDirectory, Path.GetFileName(localPath));
                EnsureUnderRoot(backupPath, backupRoot);
                if (File.Exists(backupPath))
                    backupPath += "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + ".bak";
                File.Move(localPath!, backupPath);
                manifest.Files.Remove(relativePath);
                log($"Moved remotely deleted local file to backup: '{backupPath}'.{Environment.NewLine}");
                continue;
            }

            // Both copies are absent. Confirm absence once more before dropping only its metadata.
            IReadOnlyList<RemoteFileEntry> finalRemote = await _remote.ListTreeAsync(
                $"{_settings.ProtonDriveRemoteRoot.TrimEnd('/')}/user", log, cancellationToken);
            if (finalRemote.Any(entry => !entry.IsDirectory && string.Equals(entry.RelativePath, relativePath, StringComparison.Ordinal)) ||
                File.Exists(ResolveLocal(area.LocalRoot, relativePath)))
            {
                conflicts++;
                log($"Could not finalize deletion state for 'user/{relativePath}': a copy reappeared.{Environment.NewLine}");
                continue;
            }
            manifest.Files.Remove(relativePath);
        }

        return conflicts;
    }

    private async Task RefreshUserManifestAsync(SyncArea area, ProtonSyncManifest manifest, Action<string> log, CancellationToken cancellationToken)
    {
        IReadOnlyList<RemoteFileEntry> remoteEntries = await _remote.ListTreeAsync(
            $"{_settings.ProtonDriveRemoteRoot.TrimEnd('/')}/{area.RemoteName}", log, cancellationToken);
        LocalScan local = ScanLocal(area, log);
        var remoteFiles = remoteEntries.Where(entry => !entry.IsDirectory)
            .ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);

        // Keep old records for files missing on either side: they are deletion candidates, but
        // deletion is deliberately not propagated until Proton Trash behavior is implemented.
        foreach ((string path, ProtonSyncManifestEntry oldEntry) in manifest.Files.ToArray())
        {
            if (!local.Files.ContainsKey(path) || !remoteFiles.ContainsKey(path))
                log($"Manifest retains missing path '{area.RemoteName}/{path}' as a pending deletion; no data was deleted.{Environment.NewLine}");
        }

        foreach ((string relativePath, string localPath) in local.Files)
        {
            if (!remoteFiles.TryGetValue(relativePath, out RemoteFileEntry? remoteFile) ||
                remoteFile.Size is null || remoteFile.Sha1 is null)
                continue;

            FileInfo info = new(localPath);
            if (!info.Exists)
                continue;

            string localSha1;
            if (manifest.Files.TryGetValue(relativePath, out ProtonSyncManifestEntry? baseline) &&
                info.Length == baseline.Size &&
                Math.Abs(info.LastWriteTimeUtc.Ticks - baseline.LocalMtimeTicks) <= TimeSpan.FromSeconds(2).Ticks &&
                string.Equals(remoteFile.Sha1, baseline.BaseSha1, StringComparison.OrdinalIgnoreCase))
            {
                // The local metadata and remote hash still match the last baseline, so hashing
                // this file again is unnecessary.
                localSha1 = baseline.BaseSha1;
            }
            else
            {
                localSha1 = await Sha1Async(localPath);
            }

            if (info.Length != remoteFile.Size.Value ||
                !string.Equals(localSha1, remoteFile.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                if (manifest.Files.TryGetValue(relativePath, out ProtonSyncManifestEntry? existing))
                {
                    bool localChanged = !string.Equals(localSha1, existing.BaseSha1, StringComparison.OrdinalIgnoreCase);
                    bool remoteChanged = !string.Equals(remoteFile.Sha1, existing.BaseSha1, StringComparison.OrdinalIgnoreCase);
                    log($"User file differs from its last synced baseline '{area.RemoteName}/{relativePath}' (local changed: {localChanged}, remote changed: {remoteChanged}). Baseline retained; neither side was overwritten.{Environment.NewLine}");
                }
                continue;
            }

            manifest.Files[relativePath] = new ProtonSyncManifestEntry
            {
                BaseSha1 = remoteFile.Sha1,
                Size = info.Length,
                LocalMtimeTicks = info.LastWriteTimeUtc.Ticks
            };
        }
    }

    private async Task DownloadVerifiedAsync(SyncArea area, RemoteFileEntry remote, Action<string> log, CancellationToken cancellationToken, bool overwriteExisting = false, string? expectedLocalSha1 = null)
    {
        if (remote.Size is null || remote.Sha1 is null)
            throw new InvalidDataException($"Remote file '{remote.RemotePath}' has no verifiable metadata.");
        string destination = ResolveLocal(area.LocalRoot, remote.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string tempRoot = Path.Combine(_appRoot, "tmp", "proton-sync", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            ProtonCommandResult result = await _remote.DownloadAsync([remote.RemotePath], tempRoot, log, cancellationToken);
            EnsureSucceeded(result);
            string[] candidates = Directory.GetFiles(tempRoot, "*", SearchOption.AllDirectories);
            string? downloadedPath = null;
            foreach (string candidate in candidates)
            {
                if (new FileInfo(candidate).Length == remote.Size && string.Equals(await Sha1Async(candidate), remote.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    if (downloadedPath is not null)
                        throw new InvalidDataException($"Download of '{remote.RemotePath}' produced multiple matching files.");
                    downloadedPath = candidate;
                }
            }
            if (downloadedPath is null)
                throw new InvalidDataException($"Downloaded file '{remote.RemotePath}' failed size/SHA-1 verification.");
            if (File.Exists(destination))
            {
                if (!overwriteExisting || expectedLocalSha1 is null ||
                    !string.Equals(await Sha1Async(destination), expectedLocalSha1, StringComparison.OrdinalIgnoreCase))
                {
                    log($"Skipped placing '{remote.RelativePath}' because the local destination exists or changed during download.{Environment.NewLine}");
                    return;
                }
            }
            File.Move(downloadedPath, destination, overwrite: overwriteExisting);
            if (area.RemoteName == "user" && remote.ModifiedUtc is { } modifiedUtc)
                File.SetLastWriteTimeUtc(destination, modifiedUtc.UtcDateTime);
            log($"Downloaded and verified '{area.RemoteName}/{remote.RelativePath}'.{Environment.NewLine}");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); }
            catch (IOException ex) { log($"Could not clean temporary download folder '{tempRoot}': {ex.Message}{Environment.NewLine}"); }
            catch (UnauthorizedAccessException ex) { log($"Could not clean temporary download folder '{tempRoot}': {ex.Message}{Environment.NewLine}"); }
        }
    }

    private static IReadOnlyList<RemoteFileEntry> FilterPortableRemoteEntries(IReadOnlyList<RemoteFileEntry> entries, Action<string> log)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (RemoteFileEntry entry in entries)
        {
            if (entry.RelativePath.Length > 260 || entry.RelativePath.Split('/').Any(component => !IsWindowsSafeName(component)))
            {
                blocked.Add(entry.RelativePath);
                log($"Skipped remote path that is not portable to Windows: '{entry.RelativePath}'.{Environment.NewLine}");
                continue;
            }
            if (seen.TryGetValue(entry.RelativePath, out string? previous) && !string.Equals(previous, entry.RelativePath, StringComparison.Ordinal))
            {
                blocked.Add(previous);
                blocked.Add(entry.RelativePath);
                log($"Remote case-insensitive path collision: '{previous}' and '{entry.RelativePath}'. Neither will be synced.{Environment.NewLine}");
                continue;
            }
            seen[entry.RelativePath] = entry.RelativePath;
        }
        return entries.Where(entry => !blocked.Any(path => entry.RelativePath == path || entry.RelativePath.StartsWith(path + "/", StringComparison.Ordinal))).ToArray();
    }

    private LocalScan ScanLocal(SyncArea area, Action<string> log)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        var folded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ScanDirectory(area.LocalRoot, string.Empty);
        foreach (string blockedPath in invalid)
        {
            files.Remove(blockedPath);
            foreach (string nested in files.Keys.Where(path => path.StartsWith(blockedPath + "/", StringComparison.Ordinal)).ToArray())
                files.Remove(nested);
        }
        return new LocalScan(files);

        void ScanDirectory(string directory, string relativeDirectory)
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Could not scan '{directory}': {ex.Message}{Environment.NewLine}");
                return;
            }

            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                string relative = string.IsNullOrEmpty(relativeDirectory) ? name : $"{relativeDirectory}/{name}";
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log($"Skipped unreadable local entry '{entry}': {ex.Message}{Environment.NewLine}");
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    log($"Ignored symlink/reparse point '{entry}'.{Environment.NewLine}");
                    continue;
                }
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (area.ExcludedSubtree is not null && IsSameOrChild(entry, area.ExcludedSubtree))
                    continue;
                if (area.RemoteName == "user" && ShouldIgnoreUserFile(name))
                    continue;
                if (!IsWindowsSafeName(name) || relative.Length > 260)
                {
                    invalid.Add(relative);
                    log($"Skipped path that is not portable to Windows: '{relative}'.{Environment.NewLine}");
                    continue;
                }
                if (folded.TryGetValue(relative, out string? collision) && !string.Equals(relative, collision, StringComparison.Ordinal))
                {
                    invalid.Add(relative);
                    invalid.Add(collision);
                    log($"Case-insensitive path collision: '{collision}' and '{relative}'. Neither will be synced.{Environment.NewLine}");
                    continue;
                }
                folded[relative] = relative;
                if (isDirectory)
                    ScanDirectory(entry, relative);
                else
                    files[relative] = entry;
            }
        }

    }

    private static bool IsWindowsSafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith(' ') || name.EndsWith('.')) return false;
        if (name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0) return false;
        string stem = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool ShouldIgnoreUserFile(string name) => name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~$", StringComparison.Ordinal) ||
        name.StartsWith(".~lock", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrChild(string path, string parent)
    {
        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullParent, PathComparison) || fullPath.StartsWith(fullParent + Path.DirectorySeparatorChar, PathComparison) || fullPath.StartsWith(fullParent + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string ResolveLocal(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        EnsureUnderRoot(path, root);
        return path;
    }

    private static void EnsureUnderRoot(string path, string root)
    {
        string full = Path.GetFullPath(path);
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.Equals(rootFull, PathComparison) && !full.StartsWith(rootFull + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidOperationException($"Configured sync path '{path}' escapes its expected root '{root}'.");
    }

    private static string ParentRelative(string relative) => relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : string.Empty;
    private static string JoinRemote(string root, string relative) => string.IsNullOrEmpty(relative) ? root : root.TrimEnd('/') + "/" + relative;
    private static async Task<string> Sha1Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA1.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void EnsureSucceeded(ProtonCommandResult result)
    {
        if (!result.Succeeded) throw new IOException($"Command failed: {result.Command}{Environment.NewLine}{result.Output}");
    }

    private sealed record SyncArea(string RemoteName, string LocalRoot, string? ExcludedSubtree);
    private sealed record LocalScan(Dictionary<string, string> Files);
}

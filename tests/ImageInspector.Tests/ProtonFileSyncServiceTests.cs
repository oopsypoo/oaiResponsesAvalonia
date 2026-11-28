using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using oaiResponsesAvalonia;
using oaiResponsesAvalonia.Services;

namespace ImageInspector.Tests;

public sealed class ProtonFileSyncServiceTests
{
    [Fact]
    public async Task PushCreatesMissingRemoteAreaFoldersUnderConfiguredRoot()
    {
        string root = CreateTempDirectory();
        try
        {
            var settings = CreateSettings(root);
            var remote = new FakeRemoteStore("/my-files/app");

            ProtonFileSyncResult result = await new ProtonFileSyncService(settings, remote).PushAsync(_ => { });

            Assert.Equal(ProtonFileSyncStatus.Ok, result.Status);
            Assert.True(remote.FolderExists("/my-files/app/images"));
            Assert.True(remote.FolderExists("/my-files/app/files"));
            Assert.True(remote.FolderExists("/my-files/app/user"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PullDownloadsMissingRegularFileIntoImagesArea()
    {
        string root = CreateTempDirectory();
        try
        {
            var settings = CreateSettings(root);
            var remote = new FakeRemoteStore("/my-files/app");
            remote.Seed("/my-files/app/images", "from-device.txt", "hello from remote");

            ProtonFileSyncResult result = await new ProtonFileSyncService(settings, remote).PullAsync(_ => { });

            Assert.Equal(ProtonFileSyncStatus.Ok, result.Status);
            Assert.Equal("hello from remote", await File.ReadAllTextAsync(Path.Combine(root, "images", "from-device.txt")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PushUploadsMissingRegularFileWithoutReplacingRemoteContent()
    {
        string root = CreateTempDirectory();
        try
        {
            var settings = CreateSettings(root);
            Directory.CreateDirectory(Path.Combine(root, "files", "nested"));
            await File.WriteAllTextAsync(Path.Combine(root, "files", "nested", "local.txt"), "local content");
            var remote = new FakeRemoteStore("/my-files/app");

            ProtonFileSyncResult result = await new ProtonFileSyncService(settings, remote).PushAsync(_ => { });

            Assert.Equal(ProtonFileSyncStatus.Ok, result.Status);
            Assert.Equal("local content", remote.Read("/my-files/app/files", "nested/local.txt"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PullReportsDifferentSamePathWithoutOverwritingLocalFile()
    {
        string root = CreateTempDirectory();
        try
        {
            var settings = CreateSettings(root);
            string localFile = Path.Combine(root, "images", "same.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
            await File.WriteAllTextAsync(localFile, "local version");
            var remote = new FakeRemoteStore("/my-files/app");
            remote.Seed("/my-files/app/images", "same.txt", "remote version");

            ProtonFileSyncResult result = await new ProtonFileSyncService(settings, remote).PullAsync(_ => { });

            Assert.Equal(ProtonFileSyncStatus.Conflict, result.Status);
            Assert.Equal("local version", await File.ReadAllTextAsync(localFile));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PushWritesLocalManifestForSuccessfullySyncedUserFile()
    {
        string root = CreateTempDirectory();
        try
        {
            var settings = CreateSettings(root);
            string userDirectory = Path.Combine(root, "images", "user");
            Directory.CreateDirectory(userDirectory);
            await File.WriteAllTextAsync(Path.Combine(userDirectory, "note.txt"), "initial content");
            var remote = new FakeRemoteStore("/my-files/app");

            ProtonFileSyncResult result = await new ProtonFileSyncService(settings, remote).PushAsync(_ => { });

            Assert.Equal(ProtonFileSyncStatus.Ok, result.Status);
            string manifestPath = Path.Combine(root, "sync-state.json");
            Assert.True(File.Exists(manifestPath));
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            JsonElement manifest = document.RootElement;
            Assert.Equal(1, manifest.GetProperty("schema").GetInt32());
            Assert.Equal($"{Environment.UserName}@{Environment.MachineName}", manifest.GetProperty("deviceId").GetString());
            JsonElement entry = manifest.GetProperty("files").GetProperty("note.txt");
            Assert.Equal(Sha(Encoding.UTF8.GetBytes("initial content")), entry.GetProperty("baseSha1").GetString());
            Assert.Equal(new FileInfo(Path.Combine(userDirectory, "note.txt")).Length, entry.GetProperty("size").GetInt64());
            Assert.True(entry.GetProperty("localMtimeTicks").GetInt64() > 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PushCreatesManifestEvenWhenThereAreNoUserFiles()
    {
        string root = CreateTempDirectory();
        try
        {
            var settings = CreateSettings(root);
            var remote = new FakeRemoteStore("/my-files/app");
            var log = new List<string>();

            ProtonFileSyncResult result = await new ProtonFileSyncService(settings, remote).PushAsync(log.Add);

            Assert.Equal(ProtonFileSyncStatus.Ok, result.Status);
            Assert.True(File.Exists(Path.Combine(root, "sync-state.json")));
            Assert.Contains(log, line => line.Contains(Path.Combine(root, "sync-state.json"), StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task DeletingTrackedLocalUserFileMovesRemoteCopyToTrash()
    {
        string root = CreateTempDirectory();
        try
        {
            var settings = CreateSettings(root);
            string userDirectory = Path.Combine(root, "images", "user");
            Directory.CreateDirectory(userDirectory);
            string localPath = Path.Combine(userDirectory, "note.txt");
            await File.WriteAllTextAsync(localPath, "content to trash");
            var remote = new FakeRemoteStore("/my-files/app");
            var service = new ProtonFileSyncService(settings, remote);

            Assert.Equal(ProtonFileSyncStatus.Ok, (await service.PushAsync(_ => { })).Status);
            File.Delete(localPath);
            ProtonFileSyncResult result = await service.PushAsync(_ => { });

            Assert.Equal(ProtonFileSyncStatus.Ok, result.Status);
            Assert.DoesNotContain((await remote.ListTreeAsync("/my-files/app/user", _ => { })).Where(entry => !entry.IsDirectory), entry => entry.RelativePath == "note.txt");
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "sync-state.json")));
            Assert.Empty(document.RootElement.GetProperty("files").EnumerateObject());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task LocalOnlyUserEditReplacesRemoteAndAdvancesManifest()
    {
        string root = CreateTempDirectory();
        try
        {
            var settings = CreateSettings(root);
            string localPath = Path.Combine(root, "images", "user", "note.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllTextAsync(localPath, "baseline");
            var remote = new FakeRemoteStore("/my-files/app");
            var service = new ProtonFileSyncService(settings, remote);

            Assert.Equal(ProtonFileSyncStatus.Ok, (await service.PushAsync(_ => { })).Status);
            await File.WriteAllTextAsync(localPath, "edited locally");

            ProtonFileSyncResult changedResult = await service.PushAsync(_ => { });

            Assert.Equal(ProtonFileSyncStatus.Ok, changedResult.Status);
            Assert.Equal("edited locally", remote.Read("/my-files/app/user", "note.txt"));
            Assert.Contains(ProtonFileConflictStrategy.Replace, remote.UploadStrategies);
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "sync-state.json")));
            string? storedBase = document.RootElement.GetProperty("files").GetProperty("note.txt").GetProperty("baseSha1").GetString();
            Assert.Equal(Sha(Encoding.UTF8.GetBytes("edited locally")), storedBase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task RemoteOnlyUserEditDownloadsAndAdvancesManifest()
    {
        string root = CreateTempDirectory();
        try
        {
            var settings = CreateSettings(root);
            string localPath = Path.Combine(root, "images", "user", "note.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllTextAsync(localPath, "baseline");
            var remote = new FakeRemoteStore("/my-files/app");
            var service = new ProtonFileSyncService(settings, remote);
            Assert.Equal(ProtonFileSyncStatus.Ok, (await service.PushAsync(_ => { })).Status);
            remote.Seed("/my-files/app/user", "note.txt", "edited remotely");

            ProtonFileSyncResult result = await service.PullAsync(_ => { });

            Assert.Equal(ProtonFileSyncStatus.Ok, result.Status);
            Assert.Equal("edited remotely", await File.ReadAllTextAsync(localPath));
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "sync-state.json")));
            Assert.Equal(Sha(Encoding.UTF8.GetBytes("edited remotely")), document.RootElement.GetProperty("files").GetProperty("note.txt").GetProperty("baseSha1").GetString());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task DivergentUserEditsPreserveBothVersionsAsConflictCopy()
    {
        string root = CreateTempDirectory();
        try
        {
            var settings = CreateSettings(root);
            string userDirectory = Path.Combine(root, "images", "user");
            Directory.CreateDirectory(userDirectory);
            string localPath = Path.Combine(userDirectory, "note.txt");
            await File.WriteAllTextAsync(localPath, "baseline");
            var remote = new FakeRemoteStore("/my-files/app");
            var service = new ProtonFileSyncService(settings, remote);
            Assert.Equal(ProtonFileSyncStatus.Ok, (await service.PushAsync(_ => { })).Status);
            await File.WriteAllTextAsync(localPath, "local edit");
            remote.Seed("/my-files/app/user", "note.txt", "remote edit");

            ProtonFileSyncResult result = await service.PushAsync(_ => { });

            Assert.Equal(ProtonFileSyncStatus.Conflict, result.Status);
            Assert.Equal("remote edit", await File.ReadAllTextAsync(localPath));
            string[] conflictFiles = Directory.GetFiles(userDirectory, "*.conflict-*.txt");
            Assert.Single(conflictFiles);
            Assert.Equal("local edit", await File.ReadAllTextAsync(conflictFiles[0]));
            string relativeConflict = Path.GetFileName(conflictFiles[0]);
            Assert.Equal("local edit", remote.Read("/my-files/app/user", relativeConflict));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static AppSettings CreateSettings(string root) => new()
    {
        AppRoot = root,
        ImagesFolder = "images",
        FilesFolder = "files",
        SavedImagesFolder = "user",
        ProtonDriveSyncEnabled = true,
        ProtonDriveRemoteRoot = "/my-files/app"
    };

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "proton-sync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeRemoteStore(string remoteRoot) : IRemoteStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

        public bool FolderExists(string path) => _directories.Contains(path);

        public Task EnsureFolderAsync(string parentFolder, string name, Action<string> log, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(parentFolder, remoteRoot, StringComparison.Ordinal) && !_directories.Contains(parentFolder))
                throw new IOException($"Parent remote folder does not exist: {parentFolder}");
            _directories.Add(parentFolder.TrimEnd('/') + "/" + name);
            return Task.CompletedTask;
        }

        public void Seed(string areaRoot, string relativePath, string content)
        {
            string fullPath = areaRoot.TrimEnd('/') + "/" + relativePath;
            string parent = Parent(fullPath);
            AddParentDirectories(parent);
            _files[fullPath] = Encoding.UTF8.GetBytes(content);
        }

        public string Read(string areaRoot, string relativePath) => Encoding.UTF8.GetString(_files[areaRoot.TrimEnd('/') + "/" + relativePath]);

        public Task<IReadOnlyList<RemoteFileEntry>> ListTreeAsync(string remoteFolder, Action<string> log, CancellationToken cancellationToken = default)
        {
            string prefix = remoteFolder.TrimEnd('/') + "/";
            var entries = new List<RemoteFileEntry>();
            foreach (string directory in _directories.Where(path => path.StartsWith(prefix, StringComparison.Ordinal)))
            {
                string relative = directory[prefix.Length..];
                if (relative.Length > 0)
                    entries.Add(new RemoteFileEntry(relative, directory, true, null, null, null));
            }
            foreach ((string path, byte[] content) in _files.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                string relative = path[prefix.Length..];
                entries.Add(new RemoteFileEntry(relative, path, false, content.Length, Sha(content), DateTimeOffset.UtcNow));
            }
            return Task.FromResult<IReadOnlyList<RemoteFileEntry>>(entries);
        }

        public List<ProtonFileConflictStrategy> UploadStrategies { get; } = new();

        public Task<ProtonCommandResult> UploadAsync(IReadOnlyList<string> localPaths, string remoteFolder, Action<string> log, CancellationToken cancellationToken = default, ProtonFileConflictStrategy fileConflictStrategy = ProtonFileConflictStrategy.Skip)
        {
            UploadStrategies.Add(fileConflictStrategy);
            foreach (string localPath in localPaths)
            {
                if (File.Exists(localPath))
                {
                    string path = remoteFolder.TrimEnd('/') + "/" + Path.GetFileName(localPath);
                    if (fileConflictStrategy == ProtonFileConflictStrategy.Replace)
                        _files[path] = File.ReadAllBytes(localPath);
                    else
                        _files.TryAdd(path, File.ReadAllBytes(localPath));
                    AddParentDirectories(Parent(path));
                }
                else if (Directory.Exists(localPath))
                {
                    string name = Path.GetFileName(localPath);
                    string target = remoteFolder.TrimEnd('/') + "/" + name;
                    _directories.Add(target);
                    foreach (string directory in Directory.GetDirectories(localPath, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(localPath, directory).Replace(Path.DirectorySeparatorChar, '/');
                        _directories.Add(target + "/" + relative);
                    }
                    foreach (string file in Directory.GetFiles(localPath, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(localPath, file).Replace(Path.DirectorySeparatorChar, '/');
                        string path = target + "/" + relative;
                        _files.TryAdd(path, File.ReadAllBytes(file));
                        AddParentDirectories(Parent(path));
                    }
                }
            }
            return Task.FromResult(new ProtonCommandResult("fake upload", 0, false, string.Empty));
        }

        public Task<ProtonCommandResult> DownloadAsync(IReadOnlyList<string> remotePaths, string localFolder, Action<string> log, CancellationToken cancellationToken = default)
        {
            foreach (string path in remotePaths)
                File.WriteAllBytes(Path.Combine(localFolder, Path.GetFileName(path)), _files[path]);
            return Task.FromResult(new ProtonCommandResult("fake download", 0, false, string.Empty));
        }

        public Task<ProtonCommandResult> TrashAsync(string remotePath, Action<string> log, CancellationToken cancellationToken = default)
        {
            if (!_files.Remove(remotePath))
                return Task.FromResult(new ProtonCommandResult("fake trash", 1, false, "File not found"));
            return Task.FromResult(new ProtonCommandResult("fake trash", 0, false, string.Empty));
        }

        private void AddParentDirectories(string path)
        {
            while (!string.IsNullOrEmpty(path) && path != remoteRoot)
            {
                _directories.Add(path);
                path = Parent(path);
            }
        }

        private static string Parent(string path)
        {
            int index = path.LastIndexOf('/');
            return index <= 0 ? string.Empty : path[..index];
        }

        private static string Sha(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using oaiProtonSync.Services;

namespace oaiProtonSync.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ProtonDriveCliService _cliService;
    private readonly ConversationSnapshotService _snapshotService;
    private readonly SnapshotVerificationService _verificationService;
    private readonly IsolatedSnapshotRestoreService _restoreService;
    private CancellationTokenSource? _activeOperationCancellation;
    private string _activeOperationName = "CLI operation";

    [ObservableProperty]
    private string _executable = "proton-drive";

    [ObservableProperty]
    private string _remoteFolder = "/my-files/oaiResponsesAvalonia";

    [ObservableProperty]
    private string _localFilePath = string.Empty;

    [ObservableProperty]
    private string _lastSnapshotPath = string.Empty;

    [ObservableProperty]
    private string _lastDownloadedArchivePath = string.Empty;

    [ObservableProperty]
    private RemoteSnapshotFile? _selectedRemoteSnapshot;

    public ObservableCollection<RemoteSnapshotFile> RemoteSnapshots { get; } = [];

    [ObservableProperty]
    private string _status = "Ready. CLI authentication is managed by Proton Drive.";

    [ObservableProperty]
    private string _authenticationStatus = "Not checked";

    [ObservableProperty]
    private string _activityLog = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public MainWindowViewModel(
        ProtonDriveCliService cliService,
        ConversationSnapshotService snapshotService,
        SnapshotVerificationService verificationService,
        IsolatedSnapshotRestoreService restoreService)
    {
        _cliService = cliService;
        _snapshotService = snapshotService;
        _verificationService = verificationService;
        _restoreService = restoreService;
        AppendActivity("Ready. Create a local snapshot before uploading conversation data.");
    }

    public bool CanStart => !IsBusy;

    public bool HasSnapshot => !string.IsNullOrWhiteSpace(LastSnapshotPath);

    public bool CanUploadSnapshot => !IsBusy && HasSnapshot;

    public bool CanDownloadSelectedSnapshot => !IsBusy && SelectedRemoteSnapshot is not null;

    public bool CanPrepareIsolatedRestore => !IsBusy && !string.IsNullOrWhiteSpace(LastDownloadedArchivePath) && File.Exists(LastDownloadedArchivePath);

    partial void OnLastDownloadedArchivePathChanged(string value) =>
        OnPropertyChanged(nameof(CanPrepareIsolatedRestore));

    partial void OnSelectedRemoteSnapshotChanged(RemoteSnapshotFile? value) =>
        OnPropertyChanged(nameof(CanDownloadSelectedSnapshot));

    partial void OnLastSnapshotPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(CanUploadSnapshot));
        OnPropertyChanged(nameof(CanDownloadSelectedSnapshot));
    }

    public event Action? ActivityWindowRequested;

    public event Action<Uri>? AuthenticationUrlDetected;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanUploadSnapshot));
        OnPropertyChanged(nameof(CanDownloadSelectedSnapshot));
        OnPropertyChanged(nameof(CanPrepareIsolatedRestore));
    }

    public bool CanStop => IsBusy;

    [RelayCommand]
    private void OpenActivityWindow() => ActivityWindowRequested?.Invoke();

    [RelayCommand]
    private Task LoginAsync() => RunCliOperationAsync(
        "Starting Proton Drive login. Waiting for browser authentication…",
        ["auth", "login"],
        openAuthenticationUrl: true,
        onCompleted: (exitCode, _, _) =>
        {
            if (exitCode == 0)
            {
                AuthenticationStatus = "Login command completed. Check status to verify remote access.";
                Status = AuthenticationStatus;
            }
            else
            {
                AuthenticationStatus = "Login did not complete. Check status to verify remote access.";
                Status = $"Login failed with exit code {exitCode}. See activity output.";
            }
            AppendActivity(Status);
        });

    [RelayCommand]
    private Task LogoutAsync() => RunCliOperationAsync(
        "Logging out of Proton Drive…",
        ["auth", "logout"],
        onCompleted: (exitCode, _, _) =>
        {
            if (exitCode == 0)
            {
                AuthenticationStatus = "Signed out (logout command completed).";
                Status = AuthenticationStatus;
            }
            else
            {
                AuthenticationStatus = "Logout did not complete. Check status to verify remote access.";
                Status = $"Logout failed with exit code {exitCode}. See activity output.";
            }
            AppendActivity(Status);
        });

    [RelayCommand]
    private Task CheckAuthenticationStatusAsync()
    {
        return RunCliOperationAsync(
            "Checking login status by listing the Proton Drive root…",
            ["filesystem", "list", "/my-files", "-j"],
            onCompleted: (exitCode, standardOutput, allOutput) =>
            {
                if (exitCode == 0 && IsJsonArray(standardOutput))
                {
                    AuthenticationStatus = "Signed in; Proton Drive root is accessible.";
                    Status = AuthenticationStatus;
                    AppendActivity(AuthenticationStatus);
                }
                else if (ContainsAuthenticationRequiredMessage(allOutput))
                {
                    AuthenticationStatus = "Signed out; Proton Drive requires login.";
                    Status = AuthenticationStatus;
                    AppendActivity(AuthenticationStatus);
                }
                else
                {
                    AuthenticationStatus = "Unable to determine login status. Check CLI output and folder access.";
                    Status = AuthenticationStatus;
                    AppendActivity(AuthenticationStatus);
                }
            });
    }

    [RelayCommand]
    private void StopOperation()
    {
        var cancellation = _activeOperationCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        Status = $"Stopping '{_activeOperationName}'…";
        AppendActivity(Status);
        cancellation.Cancel();
    }

    [RelayCommand]
    private async Task CreateSnapshotAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        _activeOperationCancellation = new CancellationTokenSource();
        _activeOperationName = "conversation snapshot creation";
        LastSnapshotPath = string.Empty;
        Status = "Creating a local conversation snapshot. This may take a while…";
        AppendActivity(Status);
        AppendActivity($"Reading the app database at {oaiResponsesAvalonia.Services.AppPaths.DatabaseFilePath}");

        try
        {
            var result = await _snapshotService.CreateAsync(
                AppendActivity,
                _activeOperationCancellation.Token);
            LastSnapshotPath = result.ArchivePath;
            Status = $"Local snapshot ready ({result.MediaFileCount} unique media file(s), {FormatSize(result.ArchiveSize)}).";
            AppendActivity(Status);
        }
        catch (OperationCanceledException) when (_activeOperationCancellation.IsCancellationRequested)
        {
            Status = "Snapshot creation stopped. No upload was started.";
            AppendActivity(Status);
        }
        catch (Exception ex)
        {
            Status = $"Snapshot creation failed: {ex.Message}";
            AppendActivity(Status);
        }
        finally
        {
            _activeOperationCancellation.Dispose();
            _activeOperationCancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task UploadSnapshotAsync()
    {
        if (string.IsNullOrWhiteSpace(LastSnapshotPath) || !File.Exists(LastSnapshotPath))
        {
            Status = "Create a local snapshot before uploading it.";
            AppendActivity(Status);
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(RemoteFolder))
        {
            Status = "Enter a Proton Drive destination folder.";
            AppendActivity(Status);
            return Task.CompletedTask;
        }

        return RunCliOperationAsync(
            $"Uploading conversation snapshot to {RemoteFolder}. This may take a while…",
            [
                "filesystem", "upload",
                "--file-conflict-strategy", "rename",
                LastSnapshotPath,
                RemoteFolder,
                "-v"
            ]);
    }

    [RelayCommand]
    private Task ListRemoteSnapshotsAsync()
    {
        if (string.IsNullOrWhiteSpace(RemoteFolder))
        {
            Status = "Enter a Proton Drive destination folder.";
            AppendActivity(Status);
            return Task.CompletedTask;
        }

        return RunCliOperationAsync(
            $"Listing snapshots in {RemoteFolder}. Proton Drive may take a while to respond…",
            ["filesystem", "list", RemoteFolder, "-j"],
            onCompleted: (exitCode, standardOutput, allOutput) =>
            {
                if (exitCode != 0)
                {
                    Status = $"Remote listing failed with exit code {exitCode}. See activity output.";
                    AppendActivity(Status);
                    return;
                }

                try
                {
                    var snapshots = ParseRemoteSnapshots(standardOutput, RemoteFolder);
                    RemoteSnapshots.Clear();
                    foreach (var snapshot in snapshots)
                        RemoteSnapshots.Add(snapshot);

                    SelectedRemoteSnapshot = null;
                    Status = snapshots.Count == 0
                        ? "No conversation snapshot archives were found in the remote folder."
                        : $"Found {snapshots.Count} conversation snapshot archive(s). Select one to download and verify.";
                    AppendActivity(Status);
                }
                catch (JsonException ex)
                {
                    Status = $"Could not parse the remote listing: {ex.Message}";
                    AppendActivity(Status);
                    if (!string.IsNullOrWhiteSpace(allOutput))
                        AppendActivity("See the CLI output above for details.");
                }
            });
    }

    [RelayCommand]
    private async Task DownloadAndVerifySnapshotAsync()
    {
        if (IsBusy || SelectedRemoteSnapshot is null)
            return;

        IsBusy = true;
        _activeOperationCancellation = new CancellationTokenSource();
        _activeOperationName = "snapshot download and verification";
        LastDownloadedArchivePath = string.Empty;
        string verificationDirectory = Path.Combine(
            oaiResponsesAvalonia.Services.AppPaths.DataDirectory,
            "ProtonSync",
            "Verification",
            $"download_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(verificationDirectory);
            var selectedSnapshot = SelectedRemoteSnapshot;
            Status = $"Downloading {selectedSnapshot.Name} from Proton Drive. This may take a while…";
            AppendActivity(Status);

            var arguments = new[] { "filesystem", "download", selectedSnapshot.RemotePath, verificationDirectory, "-v" };
            AppendActivity($"> {Executable} {string.Join(" ", arguments.Select(QuoteForDisplay))}");
            int exitCode = await _cliService.RunAsync(
                Executable,
                arguments,
                AppendActivity,
                _activeOperationCancellation.Token);
            if (exitCode != 0)
                throw new InvalidOperationException($"Proton Drive download failed with exit code {exitCode}.");

            _activeOperationCancellation.Token.ThrowIfCancellationRequested();
            string expectedFileName = Path.GetFileName(selectedSnapshot.Name);
            string[] downloadedFiles = Directory.GetFiles(verificationDirectory, "*", SearchOption.TopDirectoryOnly);
            string downloadedArchive = downloadedFiles.FirstOrDefault(path =>
                    string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.OrdinalIgnoreCase))
                ?? (downloadedFiles.Length == 1
                    ? downloadedFiles[0]
                    : throw new FileNotFoundException("Could not identify the downloaded snapshot archive in the temporary download folder."));

            Status = "Download complete. Verifying the archive, database, media hashes, and SQLite integrity…";
            AppendActivity(Status);
            string localSnapshotsDirectory = Path.Combine(
                oaiResponsesAvalonia.Services.AppPaths.DataDirectory,
                "ProtonSync",
                "Snapshots");
            var localArchives = Directory.Exists(localSnapshotsDirectory)
                ? Directory.EnumerateFiles(localSnapshotsDirectory, "oaiResponsesAvalonia_snapshot_*.zip", SearchOption.TopDirectoryOnly).ToList()
                : [];
            if (!string.IsNullOrWhiteSpace(LastSnapshotPath) && File.Exists(LastSnapshotPath) &&
                !localArchives.Contains(LastSnapshotPath, StringComparer.OrdinalIgnoreCase))
            {
                localArchives.Add(LastSnapshotPath);
            }

            AppendActivity($"Checking the downloaded archive against {localArchives.Count} local snapshot archive(s)…");
            var result = await _verificationService.VerifyAsync(
                downloadedArchive,
                verificationDirectory,
                localArchives,
                _activeOperationCancellation.Token);
            LastDownloadedArchivePath = downloadedArchive;

            Status = result.MatchesLocalArchive switch
            {
                true => "Download verified: it exactly matches a local snapshot archive, and all internal checks passed.",
                false => $"Download verified internally, but its archive bytes differ from all {result.LocalArchivesCompared} local snapshot archive(s) checked.",
                null => "Download verified internally. No local snapshot archive was available for whole-archive comparison."
            };
            AppendActivity(Status);
            if (result.ComparedLocalArchivePath is not null)
                AppendActivity($"Matching local snapshot: {result.ComparedLocalArchivePath}");
            else if (result.LocalArchivesCompared > 0)
                AppendActivity($"Compared against {result.LocalArchivesCompared} local snapshot archive(s).");
            AppendActivity($"Snapshot ID: {result.SnapshotId}; media files verified: {result.MediaFileCount}; SQLite integrity_check: {result.DatabaseIntegrity}.");
            AppendActivity($"Downloaded archive: {result.ArchivePath}");
            AppendActivity($"Downloaded archive SHA-256: {result.ArchiveSha256}");
            AppendActivity("The downloaded archive is retained in the local Verification folder for manual inspection; no restore was performed.");
            foreach (string entry in result.VerifiedEntries)
                AppendActivity(entry);
        }
        catch (OperationCanceledException) when (_activeOperationCancellation.IsCancellationRequested)
        {
            Status = "Snapshot download or verification was stopped.";
            AppendActivity(Status);
        }
        catch (Win32Exception ex)
        {
            Status = $"Could not start '{Executable}'. Check that Proton Drive CLI is installed and on PATH.";
            AppendActivity($"{Status} Details: {ex.Message}");
        }
        catch (Exception ex)
        {
            Status = $"Snapshot download or verification failed: {ex.Message}";
            AppendActivity(Status);
        }
        finally
        {
            _activeOperationCancellation.Dispose();
            _activeOperationCancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PrepareIsolatedRestoreAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(LastDownloadedArchivePath) || !File.Exists(LastDownloadedArchivePath))
        {
            Status = "Download and verify a remote snapshot before preparing an isolated restore.";
            AppendActivity(Status);
            return;
        }

        IsBusy = true;
        _activeOperationCancellation = new CancellationTokenSource();
        _activeOperationName = "isolated snapshot restore preparation";
        Status = "Preparing an isolated restore copy. The live application database and media will not be changed…";
        AppendActivity(Status);
        AppendActivity($"Source archive: {LastDownloadedArchivePath}");

        try
        {
            var result = await _restoreService.RestoreAsync(
                LastDownloadedArchivePath,
                AppendActivity,
                _activeOperationCancellation.Token);

            Status = "Isolated restore prepared and verified. No live application data was changed.";
            AppendActivity(Status);
            AppendActivity($"Restore test folder: {result.RestoreDirectory}");
            AppendActivity($"Database: {result.DatabasePath}");
            AppendActivity($"Media files restored: {result.MediaFileCount} (images and files folders).");
            AppendActivity($"Snapshot ID: {result.SnapshotId}; SQLite integrity_check: {result.DatabaseIntegrity}.");
            AppendActivity("Testing the isolated copy with the main application's data model and media resolver…");
            try
            {
                var compatibility = await new RestoredAppCompatibilityService().CheckAsync(result, _activeOperationCancellation.Token);
                Status = "Isolated restore passed the main application data compatibility check.";
                AppendActivity(Status);
                AppendActivity($"Read {compatibility.Sessions} sessions, {compatibility.Messages} messages, and {compatibility.MediaReferences} media references; {compatibility.UniqueMediaFiles} distinct media files were readable.");

                // The comparison is informational only; neither database is replaced or merged.
                try
                {
                    AppendActivity("Reading current local database counts for a restore preview…");
                    var current = await new RestorePreflightService().ReadCountsAsync(
                        oaiResponsesAvalonia.Data.AppDbContext.DatabaseFilePath, _activeOperationCancellation.Token);
                    AppendActivity($"Current local database: {current.Sessions} sessions, {current.Messages} messages, {current.MediaReferences} media references.");
                    AppendActivity($"Snapshot: {compatibility.Sessions} sessions, {compatibility.Messages} messages, {compatibility.MediaReferences} media references.");
                    AppendActivity("Counts are informational only: matching counts do not mean the data is identical, and different counts do not identify which conversations differ. A future restore would replace, not merge, local data.");
                }
                catch (OperationCanceledException) when (_activeOperationCancellation.IsCancellationRequested)
                {
                    AppendActivity("Local database comparison stopped; the isolated restore remains available.");
                }
                catch (Exception ex)
                {
                    AppendActivity($"Could not read current local database counts: {ex.Message}. The isolated restore compatibility check still passed.");
                }
            }
            catch (OperationCanceledException) when (_activeOperationCancellation.IsCancellationRequested)
            {
                Status = "Compatibility check stopped. The isolated restore remains available for inspection.";
                AppendActivity(Status);
            }
            catch (Exception ex)
            {
                Status = $"Main application data compatibility check failed: {ex.Message}";
                AppendActivity(Status);
                AppendActivity("The isolated restore remains available for inspection; live data was not changed.");
            }
            AppendActivity("This checks the main app's data model and media resolver, but does not launch its UI or test sending messages.");
        }
        catch (OperationCanceledException) when (_activeOperationCancellation.IsCancellationRequested)
        {
            Status = "Isolated restore preparation was stopped. Any partial staging directory was removed when possible.";
            AppendActivity(Status);
        }
        catch (Exception ex)
        {
            Status = $"Isolated restore preparation failed: {ex.Message}";
            AppendActivity(Status);
        }
        finally
        {
            _activeOperationCancellation.Dispose();
            _activeOperationCancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RehearseGuardedRestoreAsync()
    {
        if (!CanPrepareIsolatedRestore) return;
        IsBusy = true;
        _activeOperationCancellation = new CancellationTokenSource();
        _activeOperationName = "guarded restore rehearsal";
        Status = "Rehearsing replacement in a disposable workspace; live data will not be replaced…";
        AppendActivity(Status);
        try
        {
            string archive = LastDownloadedArchivePath;
            var token = _activeOperationCancellation.Token;
            var result = await Task.Run(() => new GuardedRestoreRehearsalService().RunAsync(archive, AppendActivity, token));
            Status = "Guarded restore rehearsal passed. Live data was not replaced.";
            AppendActivity(Status);
            AppendActivity($"Rehearsal workspace: {result.Workspace}");
            AppendActivity($"Disposable replacement: {result.ReplacedDirectory}");
            AppendActivity($"Recovery copy of original disposable data: {result.RecoveryDirectory}");
            AppendActivity($"Original: {result.Original.Sessions} sessions, {result.Original.Messages} messages, {result.Original.MediaReferences} media references.");
            AppendActivity($"Replacement: {result.Replacement.Sessions} sessions, {result.Replacement.Messages} messages, {result.Replacement.MediaReferences} media references.");
            AppendActivity("No live restore, automatic crash recovery, or merge is implemented.");
        }
        catch (OperationCanceledException) when (_activeOperationCancellation.IsCancellationRequested)
        {
            Status = "Restore rehearsal stopped. See the activity log for workspace and rollback details.";
            AppendActivity(Status);
        }
        catch (Exception ex)
        {
            Status = $"Restore rehearsal failed: {ex.Message}";
            AppendActivity(Status);
        }
        finally
        {
            _activeOperationCancellation.Dispose();
            _activeOperationCancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UploadTestFileAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(LocalFilePath) || !File.Exists(LocalFilePath))
        {
            Status = "Enter the full path to an existing disposable test file.";
            AppendActivity(Status);
            return;
        }

        if (string.IsNullOrWhiteSpace(RemoteFolder))
        {
            Status = "Enter a Proton Drive destination folder.";
            AppendActivity(Status);
            return;
        }

        await RunCliOperationAsync(
            $"Uploading test file to {RemoteFolder}. This may take a while…",
            [
                "filesystem", "upload",
                "--file-conflict-strategy", "rename",
                LocalFilePath,
                RemoteFolder,
                "-v"
            ]);
    }

    private async Task RunCliOperationAsync(
        string startingStatus,
        IReadOnlyList<string> arguments,
        bool openAuthenticationUrl = false,
        Action<int, string, string>? onCompleted = null)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        _activeOperationCancellation = new CancellationTokenSource();
        _activeOperationName = string.Join(" ", arguments.Take(2));
        Status = startingStatus;
        AppendActivity(startingStatus);
        AppendActivity($"> {Executable} {string.Join(" ", arguments.Select(QuoteForDisplay))}");

        var standardOutput = new StringBuilder();
        var allOutput = new StringBuilder();
        var outputLock = new object();
        var waitingForAuthenticationUrl = 0;
        var browserLaunchRequested = 0;

        void HandleOutput(string line)
        {
            lock (outputLock)
            {
                allOutput.AppendLine(line);
            }

            AppendActivity(line);

            if (!openAuthenticationUrl)
            {
                return;
            }

            if (line.Contains("Open following URL manually", StringComparison.OrdinalIgnoreCase))
            {
                Volatile.Write(ref waitingForAuthenticationUrl, 1);
            }

            if (Volatile.Read(ref waitingForAuthenticationUrl) == 0 ||
                !TryExtractProtonAuthenticationUrl(line, out var authenticationUrl) ||
                Interlocked.Exchange(ref browserLaunchRequested, 1) != 0)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                AuthenticationUrlDetected?.Invoke(authenticationUrl);
            }
            else
            {
                AppendActivity("Authentication URL detected; Proton Drive handles browser launching on this platform.");
            }
        }

        void CaptureStandardOutput(string line)
        {
            lock (outputLock)
            {
                standardOutput.AppendLine(line);
            }
        }

        try
        {
            var exitCode = await _cliService.RunAsync(
                Executable,
                arguments,
                HandleOutput,
                _activeOperationCancellation.Token,
                CaptureStandardOutput);

            string stdoutSnapshot;
            string allOutputSnapshot;
            lock (outputLock)
            {
                stdoutSnapshot = standardOutput.ToString();
                allOutputSnapshot = allOutput.ToString();
            }

            if (onCompleted is not null)
            {
                onCompleted(exitCode, stdoutSnapshot, allOutputSnapshot);
            }
            else if (exitCode == 0)
            {
                Status = "Command completed successfully.";
                AppendActivity($"Completed successfully (exit code {exitCode}).");
            }
            else
            {
                Status = $"Command failed with exit code {exitCode}. See activity output.";
                AppendActivity(Status);
            }
        }
        catch (OperationCanceledException) when (_activeOperationCancellation.IsCancellationRequested)
        {
            Status = $"Stopped process of '{_activeOperationName}'.";
            AppendActivity(Status);
        }
        catch (Win32Exception ex)
        {
            Status = $"Could not start '{Executable}'. Check that Proton Drive CLI is installed and on PATH.";
            AppendActivity($"{Status} Details: {ex.Message}");
        }
        catch (Exception ex)
        {
            Status = $"Command failed: {ex.Message}";
            AppendActivity(Status);
        }
        finally
        {
            _activeOperationCancellation.Dispose();
            _activeOperationCancellation = null;
            IsBusy = false;
        }
    }

    public void ReportActivity(string message) => AppendActivity(message);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    private static List<RemoteSnapshotFile> ParseRemoteSnapshots(string json, string remoteFolder)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("The Proton Drive listing was not a JSON array.");

        var snapshots = new List<RemoteSnapshotFile>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "file", StringComparison.OrdinalIgnoreCase) ||
                !item.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            string? name = nameElement.ValueKind switch
            {
                JsonValueKind.String => nameElement.GetString(),
                JsonValueKind.Object when nameElement.TryGetProperty("value", out var valueElement) => valueElement.GetString(),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(name) ||
                !name.StartsWith("oaiResponsesAvalonia_snapshot_", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                name.Contains('/') || name.Contains('\\'))
            {
                continue;
            }

            string remotePath = $"{remoteFolder.TrimEnd('/')}/{name}";
            snapshots.Add(new RemoteSnapshotFile(name, remotePath));
        }

        return snapshots.OrderByDescending(snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsJsonArray(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsAuthenticationRequiredMessage(string output) =>
        output.Contains("need to login", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("login required", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("authentication required", StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractProtonAuthenticationUrl(string line, out Uri authenticationUrl)
    {
        foreach (var candidate in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var text = candidate.Trim('"', '\'', '<', '>', '[', ']', '(', ')', ',', ';', '.');
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !(string.Equals(uri.Host, "proton.me", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".proton.me", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            authenticationUrl = uri;
            return true;
        }

        authenticationUrl = null!;
        return false;
    }

    private void AppendActivity(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            ActivityLog = string.IsNullOrEmpty(ActivityLog)
                ? $"[{timestamp}] {message}"
                : $"{ActivityLog}{Environment.NewLine}[{timestamp}] {message}";
        });
    }

    private static string QuoteForDisplay(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}

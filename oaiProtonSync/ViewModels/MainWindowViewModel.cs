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
    private CancellationTokenSource? _activeOperationCancellation;
    private string _activeOperationName = "CLI operation";

    [ObservableProperty]
    private string _executable = "proton-drive";

    [ObservableProperty]
    private string _remoteFolder = "/my-files/oaiResponsesAvalonia";

    [ObservableProperty]
    private string _localFilePath = string.Empty;

    [ObservableProperty]
    private string _status = "Ready. CLI authentication is managed by Proton Drive.";

    [ObservableProperty]
    private string _authenticationStatus = "Not checked";

    [ObservableProperty]
    private string _activityLog = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public MainWindowViewModel(ProtonDriveCliService cliService)
    {
        _cliService = cliService;
        AppendActivity("Ready. Enter a disposable test-file path before testing uploads.");
    }

    public bool CanStart => !IsBusy;

    public event Action? ActivityWindowRequested;

    public event Action<Uri>? AuthenticationUrlDetected;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
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
            }
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
            }
        });

    [RelayCommand]
    private Task CheckAuthenticationStatusAsync()
    {
        if (string.IsNullOrWhiteSpace(RemoteFolder))
        {
            Status = "Enter a Proton Drive destination folder before checking login status.";
            AppendActivity(Status);
            return Task.CompletedTask;
        }

        return RunCliOperationAsync(
            "Checking login status by listing the configured Proton Drive folder…",
            ["filesystem", "list", RemoteFolder, "-j"],
            onCompleted: (exitCode, standardOutput, allOutput) =>
            {
                if (exitCode == 0 && IsJsonArray(standardOutput))
                {
                    AuthenticationStatus = "Signed in; remote folder is accessible.";
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

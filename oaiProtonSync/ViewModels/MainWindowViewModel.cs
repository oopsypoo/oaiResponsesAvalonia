using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using oaiProtonSync.Services;

namespace oaiProtonSync.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ProtonDriveCliService _cliService;

    [ObservableProperty]
    private string _executable = "proton-drive";

    [ObservableProperty]
    private string _remoteFolder = "/my-files/oaiResponsesAvalonia";

    [ObservableProperty]
    private string _localFilePath = string.Empty;

    [ObservableProperty]
    private string _status = "Ready. CLI authentication is managed by Proton Drive.";

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

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanStart));

    [RelayCommand]
    private void OpenActivityWindow() => ActivityWindowRequested?.Invoke();

    [RelayCommand]
    private Task LoginAsync() => RunCliOperationAsync(
        "Starting Proton Drive login. Waiting for browser authentication…",
        ["auth", "login"],
        openAuthenticationUrl: true);

    [RelayCommand]
    private Task LogoutAsync() => RunCliOperationAsync(
        "Logging out of Proton Drive…",
        ["auth", "logout"]);

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
        bool openAuthenticationUrl = false)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = startingStatus;
        AppendActivity(startingStatus);
        AppendActivity($"> {Executable} {string.Join(" ", arguments.Select(QuoteForDisplay))}");

        var waitingForAuthenticationUrl = 0;
        var browserLaunchRequested = 0;
        void HandleOutput(string line)
        {
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

        try
        {
            var exitCode = await _cliService.RunAsync(Executable, arguments, HandleOutput);
            if (exitCode == 0)
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
            IsBusy = false;
        }
    }

    public void ReportActivity(string message) => AppendActivity(message);

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

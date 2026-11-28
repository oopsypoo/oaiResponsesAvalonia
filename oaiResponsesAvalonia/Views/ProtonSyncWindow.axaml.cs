using Avalonia.Controls;
using Avalonia.Interactivity;
using oaiResponsesAvalonia.Services;
using oaiResponsesAvalonia.Data;
using System;
using System.IO;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Views;

public partial class ProtonSyncWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ProtonCliRunner _cliRunner = new();
    private bool _commandRunning;

    public ProtonSyncWindow() : this(AppSettings.LoadSettings())
    {
    }

    public ProtonSyncWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        SyncEnabledCheckBox.IsChecked = settings.ProtonDriveSyncEnabled;
        CliExecutableTextBox.Text = settings.ProtonDriveCliPath;
        RemoteRootTextBox.Text = settings.ProtonDriveRemoteRoot;

        string imagesPath = Path.GetFullPath(Path.Combine(settings.AppRoot, settings.ImagesFolder));
        string filesPath = Path.GetFullPath(Path.Combine(settings.AppRoot, settings.FilesFolder));
        string userPath;
        try
        {
            userPath = settings.GetSavedImagesDirectory(imagesPath);
        }
        catch (Exception ex)
        {
            userPath = $"(invalid Saved Images setting: {ex.Message})";
        }

        DatabasePathText.Text = $"Database: {AppDbContext.DatabaseFilePath}";
        ImagesPathText.Text = $"Images area: {imagesPath}";
        FilesPathText.Text = $"Files area: {filesPath}";
        UserPathText.Text = $"User images area: {userPath}";
        DeviceIdText.Text = $"Device ID: {Environment.UserName}@{Environment.MachineName}";
        ManifestPathText.Text = $"Local sync manifest: {Path.Combine(Path.GetFullPath(settings.AppRoot), "sync-state.json")}";
    }

    private async void LoginButton_Click(object? sender, RoutedEventArgs e) => await RunAuthCommandAsync("login");

    private async void LogoutButton_Click(object? sender, RoutedEventArgs e) => await RunAuthCommandAsync("logout");

    private async Task RunAuthCommandAsync(string action)
    {
        if (_commandRunning)
            return;

        _commandRunning = true;
        SetCommandButtonsEnabled(false);
        StatusText.Text = $"Running proton-drive auth {action}…";
        AppendOutput($"> {CliExecutableTextBox.Text?.Trim() ?? "proton-drive"} auth {action}{Environment.NewLine}");
        try
        {
            ProtonCommandResult result = await _cliRunner.RunAuthCommandAsync(CliExecutableTextBox.Text ?? "proton-drive", action);
            AppendOutput(result.Output);
            StatusText.Text = result.Succeeded
                ? $"proton-drive auth {action} completed successfully."
                : result.TimedOut
                    ? "The command timed out. See output for details."
                    : $"proton-drive auth {action} failed. See output for details.";
        }
        finally
        {
            _commandRunning = false;
            SetCommandButtonsEnabled(true);
        }
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e) => SaveSettingsFromUi();

    private bool SaveSettingsFromUi()
    {
        string remoteRoot = RemoteRootTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(remoteRoot) || !remoteRoot.StartsWith("/", StringComparison.Ordinal) || remoteRoot.Contains('\\'))
        {
            StatusText.Text = "Remote app root must be a non-empty POSIX path beginning with /.";
            return false;
        }

        _settings.ProtonDriveSyncEnabled = SyncEnabledCheckBox.IsChecked == true;
        _settings.ProtonDriveCliPath = string.IsNullOrWhiteSpace(CliExecutableTextBox.Text)
            ? "proton-drive"
            : CliExecutableTextBox.Text.Trim();
        _settings.ProtonDriveRemoteRoot = remoteRoot.TrimEnd('/');

        try
        {
            AppSettings.SaveSettings(_settings);
            StatusText.Text = "Proton Sync settings saved. Credentials are not stored by this app.";
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save settings: {ex.Message}";
            return false;
        }
    }

    private async void PullFilesButton_Click(object? sender, RoutedEventArgs e) => await RunFileSyncAsync(push: false);

    private async void PushFilesButton_Click(object? sender, RoutedEventArgs e) => await RunFileSyncAsync(push: true);

    private async void PullDatabaseButton_Click(object? sender, RoutedEventArgs e) => await RunDatabaseSyncAsync(push: false);

    private async void PushDatabaseButton_Click(object? sender, RoutedEventArgs e) => await RunDatabaseSyncAsync(push: true);

    private async Task RunDatabaseSyncAsync(bool push)
    {
        if (_commandRunning)
            return;
        if (!SaveSettingsFromUi())
            return;
        if (!_settings.ProtonDriveSyncEnabled)
        {
            StatusText.Text = "Enable Proton Sync before running a database sync.";
            return;
        }

        _commandRunning = true;
        SetCommandButtonsEnabled(false);
        StatusText.Text = push ? "Syncing regular files, then pushing the database snapshot…" : "Syncing regular files, then pulling the database snapshot…";
        AppendOutput($"{Environment.NewLine}--- {(push ? "Push" : "Pull")} files and database started ---{Environment.NewLine}");
        try
        {
            var store = new ProtonDriveRemoteStore(_settings.ProtonDriveCliPath);
            var fileSync = new ProtonFileSyncService(_settings, store);
            ProtonFileSyncResult files = push
                ? await fileSync.PushAsync(AppendOutput)
                : await fileSync.PullAsync(AppendOutput);
            AppendOutput($"Regular files: {files.Status}: {files.Summary}{Environment.NewLine}");
            if (files.Status != ProtonFileSyncStatus.Ok)
            {
                StatusText.Text = $"Database operation stopped because regular-file sync returned {files.Status}: {files.Summary}";
                return;
            }

            var databaseSync = new ProtonDatabaseSyncService(_settings, store);
            ProtonFileSyncResult database = push
                ? await databaseSync.PushAsync(AppendOutput)
                : await databaseSync.PullAsync(AppendOutput);
            StatusText.Text = database.Summary;
            AppendOutput($"Database: {database.Status}: {database.Summary}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Database sync failed: {ex.Message}";
            AppendOutput($"Database sync failed: {ex}{Environment.NewLine}");
        }
        finally
        {
            _commandRunning = false;
            SetCommandButtonsEnabled(true);
        }
    }

    private async Task RunFileSyncAsync(bool push)
    {
        if (_commandRunning)
            return;
        if (!SaveSettingsFromUi())
            return;
        if (!_settings.ProtonDriveSyncEnabled)
        {
            StatusText.Text = "Enable Proton Sync before running a file sync.";
            return;
        }

        _commandRunning = true;
        SetCommandButtonsEnabled(false);
        StatusText.Text = push ? "Pushing regular files and folders…" : "Pulling regular files and folders…";
        AppendOutput($"{Environment.NewLine}--- {(push ? "Push" : "Pull")} regular files started ---{Environment.NewLine}");
        try
        {
            var store = new ProtonDriveRemoteStore(_settings.ProtonDriveCliPath);
            var sync = new ProtonFileSyncService(_settings, store);
            ProtonFileSyncResult result = push
                ? await sync.PushAsync(AppendOutput)
                : await sync.PullAsync(AppendOutput);
            StatusText.Text = result.Summary;
            AppendOutput($"{result.Status}: {result.Summary}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"File sync failed: {ex.Message}";
            AppendOutput($"File sync failed: {ex}{Environment.NewLine}");
        }
        finally
        {
            _commandRunning = false;
            SetCommandButtonsEnabled(true);
        }
    }

    private void SetCommandButtonsEnabled(bool enabled)
    {
        LoginButton.IsEnabled = enabled;
        LogoutButton.IsEnabled = enabled;
        PullFilesButton.IsEnabled = enabled;
        PushFilesButton.IsEnabled = enabled;
        PullDatabaseButton.IsEnabled = enabled;
        PushDatabaseButton.IsEnabled = enabled;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void AppendOutput(string text)
    {
        CommandOutputTextBox.Text += text;
        CommandOutputTextBox.CaretIndex = CommandOutputTextBox.Text?.Length ?? 0;
    }
}

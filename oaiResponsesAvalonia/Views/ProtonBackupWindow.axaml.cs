using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using oaiProtonSync.Services;
using oaiProtonSync.ViewModels;

namespace oaiResponsesAvalonia.Views;

public partial class ProtonBackupWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public ProtonBackupWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(
            new ProtonDriveCliService(),
            new ConversationSnapshotService(),
            new SnapshotVerificationService(),
            new IsolatedSnapshotRestoreService());
        DataContext = _viewModel;
        _viewModel.AuthenticationUrlDetected += OnAuthenticationUrlDetected;
        Closing += (_, e) =>
        {
            if (_viewModel.IsBusy)
            {
                e.Cancel = true;
                _viewModel.ReportActivity("An operation is still running. Stop it or wait for it to finish before closing this window.");
            }
        };
        Closed += (_, _) => _viewModel.AuthenticationUrlDetected -= OnAuthenticationUrlDetected;
    }

    private async void BrowseFirstSnapshot(object? sender, RoutedEventArgs e)
    {
        var path = await PickSnapshotAsync();
        if (path is not null) _viewModel.FirstComparisonArchive = path;
    }

    private async void BrowseSecondSnapshot(object? sender, RoutedEventArgs e)
    {
        var path = await PickSnapshotAsync();
        if (path is not null) _viewModel.SecondComparisonArchive = path;
    }

    private async System.Threading.Tasks.Task<string?> PickSnapshotAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select a conversation snapshot",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Snapshot ZIP") { Patterns = new[] { "*.zip" } } }
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            _viewModel.ReportActivity($"Could not select a snapshot: {ex.Message}");
            return null;
        }
    }

    private async void BrowseFirstBaseline(object? sender, RoutedEventArgs e)
    {
        var path = await PickBaselineAsync();
        if (path is not null) _viewModel.FirstBaselineDirectory = path;
    }

    private async void BrowseSecondBaseline(object? sender, RoutedEventArgs e)
    {
        var path = await PickBaselineAsync();
        if (path is not null) _viewModel.SecondBaselineDirectory = path;
    }

    private async void BrowseSharedBaseline(object? sender, RoutedEventArgs e)
    {
        var path = await PickBaselineAsync();
        if (path is not null) _viewModel.SharedBaselineDirectory = path;
    }

    private async System.Threading.Tasks.Task<string?> PickBaselineAsync()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select a disposable baseline copy", AllowMultiple = false
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            _viewModel.ReportActivity($"Could not select a baseline folder: {ex.Message}");
            return null;
        }
    }

    private void OnAuthenticationUrlDetected(Uri authenticationUrl)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                _viewModel.ReportActivity("Opening the Proton authentication URL in the default browser…");
                bool launched = await Launcher.LaunchUriAsync(authenticationUrl);
                _viewModel.ReportActivity(launched
                    ? "Authentication URL handed off to the default browser. Waiting for CLI authentication to finish…"
                    : "Could not open the authentication URL. Use the URL shown above to continue manually.");
            }
            catch (Exception ex)
            {
                _viewModel.ReportActivity($"Could not open the authentication URL automatically: {ex.Message}");
            }
        });
    }
}

using System;
using Avalonia.Controls;
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

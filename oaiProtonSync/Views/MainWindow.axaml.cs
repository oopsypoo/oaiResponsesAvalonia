using Avalonia.Controls;
using Avalonia.Threading;
using oaiProtonSync.ViewModels;

namespace oaiProtonSync.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow() => InitializeComponent();

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.AuthenticationUrlDetected += OnAuthenticationUrlDetected;
    }

    private void OnAuthenticationUrlDetected(Uri authenticationUrl)
    {
        var viewModel = _viewModel;
        if (viewModel is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel is null)
                {
                    throw new InvalidOperationException("The main window is not attached to an Avalonia TopLevel.");
                }

                viewModel.ReportActivity("Opening the Proton authentication URL in the default browser…");
                var launched = await topLevel.Launcher.LaunchUriAsync(authenticationUrl);
                viewModel.ReportActivity(launched
                    ? "Authentication URL handed off to the default browser. Waiting for CLI authentication to finish…"
                    : "Avalonia could not open the authentication URL. Use the URL shown above to continue manually.");
            }
            catch (Exception ex)
            {
                viewModel.ReportActivity($"Could not open the authentication URL automatically: {ex.Message}");
            }
        });
    }
}

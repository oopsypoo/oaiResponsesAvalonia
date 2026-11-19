using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using oaiProtonSync.Services;
using oaiProtonSync.ViewModels;
using oaiProtonSync.Views;

namespace oaiProtonSync;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel(new ProtonDriveCliService());
            var mainWindow = new MainWindow { DataContext = viewModel };
            ActivityWindow? activityWindow = null;

            viewModel.ActivityWindowRequested += () =>
            {
                if (activityWindow is null)
                {
                    activityWindow = new ActivityWindow { DataContext = viewModel };
                    activityWindow.Closed += (_, _) => activityWindow = null;
                }

                activityWindow.Show();
                activityWindow.Activate();
            };

            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}

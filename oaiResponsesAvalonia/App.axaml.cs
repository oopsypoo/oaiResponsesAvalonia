using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using oaiResponsesAvalonia.Services;
using oaiResponsesAvalonia.Views;
using System;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia;

public partial class App : Application
{
    /// <summary>
    /// Loads application-level resources and styles from App.axaml.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Creates the initial window and pulls remote data before the main window initializes SQLite.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppSettings settings = AppSettings.LoadSettings();
            if (settings.ProtonDriveSyncEnabled)
            {
                var progress = new ProtonSyncProgressWindow("Pulling Proton Drive data before opening the database…");
                desktop.MainWindow = progress;
                base.OnFrameworkInitializationCompleted();
                progress.Show();
                _ = CompleteStartupAsync(desktop, progress, settings);
                return;
            }

            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task CompleteStartupAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        ProtonSyncProgressWindow progress,
        AppSettings settings)
    {
        ProtonSyncWorkflowResult result;
        try
        {
            var workflow = new ProtonSyncWorkflow(settings);
            progress.AppendOutput($"Local database: {oaiResponsesAvalonia.Data.AppDbContext.DatabaseFilePath}{Environment.NewLine}");
            result = await workflow.PullAsync(progress.AppendOutput);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            progress.AppendOutput($"Startup Proton Sync failed: {ex}{Environment.NewLine}");
            result = new ProtonSyncWorkflowResult(ProtonFileSyncStatus.Failed, ex.Message);
        }

        progress.SetStatus(result.Status == ProtonFileSyncStatus.Ok
            ? "Startup pull completed. Opening the application…"
            : "Startup pull did not complete. The application will open without blocking.");

        var mainWindow = new MainWindow();
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        progress.Close();

        if (result.Status != ProtonFileSyncStatus.Ok)
        {
            var dialogs = new AvaloniaDialogService();
            await dialogs.ShowMessageAsync(mainWindow, "Proton Sync startup notice",
                $"Automatic pull did not complete. The application will continue with local data.{Environment.NewLine}{Environment.NewLine}{result.Summary}",
                DialogSeverity.Warning);
        }
    }
}

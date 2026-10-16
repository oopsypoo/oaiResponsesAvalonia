using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using oaiResponsesAvalonia.Views;
#if DEBUG
using AvaloniaUI.DiagnosticsSupport;
#endif

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
    /// Creates the initial window for the classic desktop application lifetime.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
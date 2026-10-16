using Avalonia;
using System;

namespace oaiResponsesAvalonia;

sealed class Program
{
    /// <summary>
    /// Starts Avalonia's classic desktop lifetime with the configured application builder.
    /// </summary>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// Configures the application, platform detection, fonts, developer tools, and trace logging.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .WithDeveloperTools()    // <- call the method
        .LogToTrace();
}

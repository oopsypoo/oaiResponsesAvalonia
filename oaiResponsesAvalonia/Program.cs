using Avalonia;
using System;

namespace oaiResponsesAvalonia;

sealed class Program
{
    /// <summary>
    /// Starts Avalonia's classic desktop lifetime with the configured application builder.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // Temporary branch-specific development location. Remove this block when the
        // test branch no longer needs its own Linux data directory.
        if (OperatingSystem.IsLinux())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Services.AppPaths.ConfigureDevelopmentDataDirectory(System.IO.Path.Combine(home, "Documents", "oaira"));
        }

        if (args.Length > 0 && args[0] == Services.IsolatedVisualTestService.Argument)
        {
            if (args.Length != 2)
                throw new ArgumentException("The isolated visual test requires exactly one directory.");
            Services.AppPaths.ConfigureIsolatedVisualTest(args[1]);
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Configures the application, platform detection, fonts, developer tools, and trace logging.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
        /* Need subscription to use developer tools
#if DEBUG
        builder = builder.WithDeveloperTools();
#endif
        */
        return builder.LogToTrace();
    }
}

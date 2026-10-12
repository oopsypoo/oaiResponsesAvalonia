using System;
using System.IO;
using System.Text.Json;
using oaiResponsesAvalonia.Services;

namespace oaiResponsesAvalonia
{
    public class AppSettings
    {
        public string AppRoot { get; set; } = AppPaths.DataDirectory;
        public string LogsFolder { get; set; } = "logs";  // Relative to AppRoot
        public string ImagesFolder { get; set; } = "images";
        public string ResponsesMarkdownTheme { get; set; } = "github.min.css";
        public string ResponsesPageTheme { get; set; } = "github-light-page.css";

        private static string SettingsPath => AppPaths.SettingsFilePath;

        public static AppSettings LoadSettings()
        {
            AppPaths.EnsureDataDirectory();

            if (!File.Exists(SettingsPath) && File.Exists(AppPaths.LegacySettingsFilePath))
            {
                File.Copy(AppPaths.LegacySettingsFilePath, SettingsPath);
            }

            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            return new AppSettings();
        }

        public static void SaveSettings(AppSettings settings)
        {
            AppPaths.EnsureDataDirectory();
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
    }
}

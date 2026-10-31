using System;
using System.IO;
using System.Text.Json;
using oaiResponsesAvalonia.Services;

namespace oaiResponsesAvalonia
{
    public class AppSettings
    {
        public string AppRoot { get; set; } = AppPaths.DataDirectory;
        
        public string FilesFolder { get; set; } = "files";
        public string ImagesFolder { get; set; } = "images";
        // A simple folder name stays under ImagesFolder for compatibility; an absolute path selects any location.
        public string SavedImagesFolder { get; set; } = "user";
        public string ResponsesMarkdownTheme { get; set; } = "github.min.css";
        public string ResponsesPageTheme { get; set; } = "github-light-page.css";

        public string GetSavedImagesDirectory(string imagesDirectory)
        {
            string location = SavedImagesFolder?.Trim() ?? string.Empty;
            if (!IsValidSavedImagesFolder(location))
                throw new InvalidOperationException("Saved Images must be a valid folder path, or a single folder name inside the Images Folder.");

            return Path.IsPathFullyQualified(location)
                ? Path.GetFullPath(location)
                : Path.GetFullPath(Path.Combine(imagesDirectory, location));
        }

        public static bool IsValidSavedImagesFolder(string? folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
                return false;

            if (Path.IsPathFullyQualified(folderName))
            {
                try
                {
                    _ = Path.GetFullPath(folderName);
                    return true;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return false;
                }
            }

            if (folderName is "." or ".." || Path.IsPathRooted(folderName))
                return false;

            if (folderName.Contains('/') || folderName.Contains('\\'))
                return false;

            return folderName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

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
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

                // Migrate the previous built-in default while preserving explicitly chosen absolute paths.
                if (string.Equals(settings.SavedImagesFolder, "User Images", StringComparison.OrdinalIgnoreCase))
                    settings.SavedImagesFolder = "user";

                return settings;
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

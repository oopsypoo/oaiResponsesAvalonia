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
        public string CustomSearchProvider { get; set; } = "brave";
        public string ReputationProvider { get; set; } = "google-safe-browsing";
        public string CustomSearchContextSize { get; set; } = "medium";
        public bool CustomWebSearchExperimentalEnabled { get; set; }
        public string CustomSearchCountry { get; set; } = string.Empty;
        public string CustomSearchLanguage { get; set; } = string.Empty;
        public string CustomSearchAdditionalInstructions { get; set; } = string.Empty;

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

        public void ValidateDevelopmentPaths()
        {
            if (!AppPaths.IsDevelopmentDataDirectory || AppPaths.IsIsolatedVisualTest)
                return;

            string root = Path.GetFullPath(AppPaths.DataDirectory);
            if (string.IsNullOrWhiteSpace(AppRoot) || !Path.IsPathFullyQualified(AppRoot))
                throw new InvalidOperationException("Development settings AppRoot must be an absolute path inside the development data directory.");
            EnsureDevelopmentPath(root, AppRoot, "AppRoot");
            string images = Path.GetFullPath(Path.Combine(AppRoot, ImagesFolder));
            string files = Path.GetFullPath(Path.Combine(AppRoot, FilesFolder));
            EnsureDevelopmentPath(root, images, "ImagesFolder");
            EnsureDevelopmentPath(root, files, "FilesFolder");
            EnsureDevelopmentPath(root, GetSavedImagesDirectory(images), "SavedImagesFolder");
        }

        private static void EnsureDevelopmentPath(string root, string path, string setting)
        {
            string full = Path.GetFullPath(path);
            string relative = Path.GetRelativePath(root, full);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException($"Development setting {setting} must point inside {root}; current path: {full}");
        }

        private static string SettingsPath => AppPaths.SettingsFilePath;

        public static AppSettings LoadSettings()
        {
            AppPaths.EnsureDataDirectory();

            // Never use device-specific or previously changed paths in a visual restore test.
            if (AppPaths.IsIsolatedVisualTest)
                return new AppSettings();

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

                settings.ValidateDevelopmentPaths();
                return settings;
            }
            var defaults = new AppSettings();
            defaults.ValidateDevelopmentPaths();
            return defaults;
        }

        public static void SaveSettings(AppSettings settings)
        {
            settings.ValidateDevelopmentPaths();
            AppPaths.EnsureDataDirectory();
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
    }
}

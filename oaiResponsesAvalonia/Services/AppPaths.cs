using System;
using System.IO;

namespace oaiResponsesAvalonia.Services
{
    public static class AppPaths
    {
        public const string ApplicationDirectoryName = "oaiResponsesAvalonia";

        public static string DefaultDataDirectory { get; } = GetDataDirectory();
        public static string DataDirectory { get; private set; } = DefaultDataDirectory;
        public static string BaseDataDirectory { get; private set; } = DefaultDataDirectory;
        public static bool IsDevelopmentDataDirectory { get; private set; }
        public static bool IsIsolatedVisualTest { get; private set; }

        public static void ConfigureDevelopmentDataDirectory(string directory)
        {
            if (IsDevelopmentDataDirectory || IsIsolatedVisualTest)
                throw new InvalidOperationException("The data directory has already been configured.");
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
                throw new ArgumentException("The development data directory must be an absolute path.", nameof(directory));

            string full = Path.GetFullPath(directory);
            if (!Directory.Exists(full) || !File.Exists(Path.Combine(full, "localhistory.db")) ||
                !File.Exists(Path.Combine(full, "settings.json")))
                throw new InvalidOperationException($"The development data directory must already contain localhistory.db and settings.json: {full}");
            if (string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultDataDirectory)),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("The development data directory must not be the normal data directory.");

            DataDirectory = BaseDataDirectory = full;
            IsDevelopmentDataDirectory = true;
        }

        public static void ConfigureIsolatedVisualTest(string directory)
        {
            if (IsIsolatedVisualTest)
                throw new InvalidOperationException("The data directory has already been configured.");
            DataDirectory = IsolatedVisualTestService.ValidateVisualDirectory(directory);
            IsIsolatedVisualTest = true;
        }
        public static string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");
        public static string DatabaseFilePath => Path.Combine(DataDirectory, "localhistory.db");
        public static string ModelsFilePath => Path.Combine(DataDirectory, "available_models.json");
        
        public static string ImagesDirectory => Path.Combine(DataDirectory, "images");
        public static string FilesDirectory => Path.Combine(DataDirectory, "files");
        private static string LegacyDirectory => IsIsolatedVisualTest || IsDevelopmentDataDirectory ? Path.Combine(DataDirectory, ".legacy-disabled") : AppContext.BaseDirectory;
        public static string LegacySettingsFilePath => Path.Combine(LegacyDirectory, "settings.json");
        public static string LegacyDatabaseFilePath => Path.Combine(LegacyDirectory, "localhistory.db");
        public static string LegacyModelsFilePath => Path.Combine(LegacyDirectory, "available_models.txt");

        public static void EnsureDataDirectory()
        {
            Directory.CreateDirectory(DataDirectory);
        }

        private static string GetDataDirectory()
        {
            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                localApplicationData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local",
                    "share");
            }

            return Path.Combine(localApplicationData, ApplicationDirectoryName);
        }
    }
}

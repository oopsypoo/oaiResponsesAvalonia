using System;
using System.IO;

namespace oaiResponsesAvalonia.Services
{
    public static class AppPaths
    {
        public const string ApplicationDirectoryName = "oaiResponsesAvalonia";

        public static string DataDirectory { get; } = GetDataDirectory();
        public static string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");
        public static string DatabaseFilePath => Path.Combine(DataDirectory, "localhistory.db");
        public static string ModelsFilePath => Path.Combine(DataDirectory, "available_models.json");
        public static string LogsDirectory => Path.Combine(DataDirectory, "logs");
        public static string ImagesDirectory => Path.Combine(DataDirectory, "images");
        public static string LegacySettingsFilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");
        public static string LegacyDatabaseFilePath => Path.Combine(AppContext.BaseDirectory, "localhistory.db");
        public static string LegacyModelsFilePath => Path.Combine(AppContext.BaseDirectory, "available_models.txt");

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

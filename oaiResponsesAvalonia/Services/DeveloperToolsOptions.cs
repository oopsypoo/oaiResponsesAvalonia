namespace oaiResponsesAvalonia.Services
{
    public sealed class DeveloperToolsOptions
    {
        public bool Enabled { get; set; }

        public string RepositoryRoot { get; set; } = string.Empty;
        public string ScopeMode { get; set; } = "repository";

        public bool ReadOnlyOnly { get; set; } = true;
        public bool RequireConfirmation { get; set; } = false;
        public bool RequireWriteConfirmation { get; set; } = true;
        public bool RequireExecutionConfirmation { get; set; } = true;
        public bool ShowToolLogs { get; set; } = true;

        public bool SearchProjectTextEnabled { get; set; } = true;
        public bool ReadProjectFileEnabled { get; set; } = true;
        public bool ListProjectFilesEnabled { get; set; } = false;
        public bool RunDiagnosticsEnabled { get; set; } = false;
        public bool CreateDotNetSolutionEnabled { get; set; } = false;
        public bool BuildDotNetProjectEnabled { get; set; } = false;
        public bool RunDotNetProjectEnabled { get; set; } = false;

        public bool WriteProjectFileEnabled { get; set; } = false;
        public bool ReplaceInProjectFileEnabled { get; set; } = false;

        public string[] AllowedExtensions { get; set; } = new string[0];

        public int MaxReadLines { get; set; } = 300;
        public int MaxSearchResults { get; set; } = 100;
        public int MaxFileBytes { get; set; } = 512 * 1024;
        public int MaxWriteFileBytes { get; set; } = 512 * 1024;
        public int CommandTimeoutSeconds { get; set; } = 120;
        public int BuildTimeoutSeconds { get; set; } = 300;
    }
}

using System;

namespace oaiResponsesAvalonia.Services
{
    public sealed class ProcessExecutionResult
    {
        public bool Started { get; set; }
        public bool TimedOut { get; set; }
        public int? ExitCode { get; set; }
        public long DurationMs { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public bool OutputTruncated { get; set; }
        public string Error { get; set; } = string.Empty;

        public bool Ok => Started && !TimedOut && ExitCode == 0 && string.IsNullOrWhiteSpace(Error);
    }
}

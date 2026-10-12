using System;

namespace oaiResponsesAvalonia.Data
{
    public sealed class DeveloperToolCallLogItem
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string ToolName { get; set; } = string.Empty;
        public string WorkspaceRoot { get; set; } = string.Empty;
        public string ArgumentsJson { get; set; } = string.Empty;
        public string ResultJson { get; set; } = string.Empty;
        public string TimestampText => Timestamp.ToString("HH:mm:ss");

        public string ResultSummary
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ResultJson))
                    return string.Empty;

                if (ResultJson.IndexOf("\"ok\": true", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "OK";

                if (ResultJson.IndexOf("\"ok\": false", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Failed";


                return "Completed";
            }
        }
    }
}
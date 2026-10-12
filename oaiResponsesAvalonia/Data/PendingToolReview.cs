using System.Collections.Generic;

namespace oaiResponsesAvalonia.Data
{
    public sealed class PendingToolReview
    {
        public string ToolName { get; set; } = string.Empty;
        public string ArgumentsJson { get; set; } = string.Empty;
        public bool IsWriteTool { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string MarkdownPreview { get; set; } = string.Empty;
        public List<PendingFileChange> Changes { get; set; } = new();
    }

    public sealed class PendingFileChange
    {
        public string Path { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public bool FileExists { get; set; }
        public bool IsCreate { get; set; }
        public bool IsOverwrite { get; set; }
        public string OldContent { get; set; } = string.Empty;
        public string NewContent { get; set; } = string.Empty;
    }
}

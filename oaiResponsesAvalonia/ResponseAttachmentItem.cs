using System;
using System.IO;

namespace oaiResponsesAvalonia
{
    public sealed class ResponseAttachmentItem
    {
        public string LocalPath { get; set; } = string.Empty;

        public string MediaType { get; set; } = "application/octet-stream";

        public string FileName => Path.GetFileName(LocalPath ?? string.Empty);

        public bool IsImage =>
            !string.IsNullOrWhiteSpace(MediaType) &&
            MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }
}
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO;

namespace oaiResponsesAvalonia.Data
{
    public enum EndpointType
    {
        Responses
    }
    public class ChatSession
    {
        public ChatSession()
        {
        }

        // Keep this constructor in Phase 1 so existing code still works.
        public ChatSession(EndpointType endpoint, string type, string title)
        {
            Endpoint = endpoint;
            Type = string.IsNullOrWhiteSpace(type) ? endpoint.ToString() : type;
            Title = title ?? string.Empty;
            CreatedAt = DateTime.UtcNow;
            LastUsedAt = DateTime.UtcNow;
        }

        [Key]
        public int Id { get; set; }

        public EndpointType Endpoint { get; set; }

        // Keep for compatibility in Phase 1.
        // Later we can remove this redundant property with a proper migration.
        public string Type { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;

        public virtual ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
    }

    public class ChatMessage
    {
        [Key]
        public int Id { get; set; }

        public int ChatSessionId { get; set; }

        public string Role { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        // --- Responses DNA ---
        public string ModelUsed { get; set; } = string.Empty;
        public string ReasoningLevel { get; set; } = string.Empty;
        public string ActiveTools { get; set; } = string.Empty;
        public string SearchContextSize { get; set; } = string.Empty;

        // --- Image DNA ---
        public string ImageSize { get; set; } = string.Empty;
        public string ImageQuality { get; set; } = string.Empty;

        public string RawJson { get; set; } = string.Empty;

        public string ImageToolSettingsJson { get; set; } = string.Empty;
        public string DeveloperToolSettingsJson { get; set; } = string.Empty;
        public string ToolCallLogJson { get; set; } = string.Empty;
        public virtual ChatSession? ChatSession { get; set; }

        public virtual ICollection<MediaFile> MediaFiles { get; set; } = new List<MediaFile>();
        [NotMapped]
        public string ImageToolSummary => ImageToolSettingsSummaryHelper.Build(ImageToolSettingsJson);
    }

    public class MediaFile
    {
        public MediaFile()
        {
        }

        public MediaFile(int chatMessageId, string localPath, string mediaType)
        {
            ChatMessageId = chatMessageId;
            LocalPath = localPath ?? string.Empty;
            MediaType = mediaType ?? string.Empty;
        }

        [Key]
        public int Id { get; set; }

        public int ChatMessageId { get; set; }

        public string LocalPath { get; set; } = string.Empty;

        public string MediaType { get; set; } = string.Empty;

        public virtual ChatMessage? ChatMessage { get; set; }

        [NotMapped]
        public string FileName => Path.GetFileName((LocalPath ?? string.Empty).Replace('\\', '/'));

        [NotMapped]
        public bool IsImage =>
            !string.IsNullOrWhiteSpace(MediaType) &&
            MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }
}
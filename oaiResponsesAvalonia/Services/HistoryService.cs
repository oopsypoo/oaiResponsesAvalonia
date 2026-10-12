using Microsoft.EntityFrameworkCore;
using oaiResponsesAvalonia.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services
{
    public class HistoryService
    {
        private readonly MediaStorageService _mediaStorageService;

        public HistoryService(MediaStorageService? mediaStorageService = null)
        {
            _mediaStorageService = mediaStorageService ?? new MediaStorageService();
        }

        private AppDbContext CreateDbContext() => new AppDbContext();

        public async Task<int> StartNewSessionAsync(string initialTitle, EndpointType endpoint)
        {
            await using var context = CreateDbContext();

            string typeString = endpoint.ToString();

            var session = new ChatSession(endpoint, typeString, initialTitle);

            context.Sessions.Add(session);
            await context.SaveChangesAsync();

            return session.Id;
        }

        public async Task<int> AddMessageAsync(
                    int sessionId,
                    string role,
                    string content,
                    string? rawJson = null,
                    string? model = null,
                    string? reasoning = null,
                    string? tools = null,
                    string? imgSize = null,
                    string? imgQual = null,
                    string? searchSize = null,
                    string? imageToolSettingsJson = null,
                    string? developerToolSettingsJson = null,
                    string? toolCallLogJson = null)

        {
            await using var context = CreateDbContext();

            var session = await context.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (session == null)
            {
                throw new InvalidOperationException($"Session {sessionId} was not found.");
            }

            var msg = new ChatMessage
            {
                ChatSessionId = sessionId,
                Role = role ?? string.Empty,
                Content = content ?? string.Empty,
                RawJson = rawJson ?? string.Empty,
                ModelUsed = model ?? string.Empty,
                ReasoningLevel = reasoning ?? string.Empty,
                ActiveTools = tools ?? string.Empty,
                ImageSize = imgSize ?? string.Empty,
                ImageQuality = imgQual ?? string.Empty,
                SearchContextSize = searchSize ?? string.Empty,
                Timestamp = DateTime.UtcNow,
                ImageToolSettingsJson = imageToolSettingsJson ?? string.Empty,
                DeveloperToolSettingsJson = developerToolSettingsJson ?? string.Empty,
                ToolCallLogJson = toolCallLogJson ?? string.Empty
            };

            context.Messages.Add(msg);

            session.LastUsedAt = DateTime.UtcNow;

            await context.SaveChangesAsync();

            return msg.Id;
        }

        public async Task<List<ChatMessage>> GetFullSessionHistoryAsync(int sessionId)
        {
            await using var context = CreateDbContext();

            return await context.Messages
                .Include(m => m.MediaFiles)
                .Where(m => m.ChatSessionId == sessionId)
                .OrderBy(m => m.Timestamp)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task<List<ChatSession>> GetAllSessionsAsync()
        {
            await using var context = CreateDbContext();

            return await context.Sessions
                .OrderByDescending(s => s.LastUsedAt)
                .AsNoTracking()
                .ToListAsync();
        }
        public async Task<List<ChatSession>> GetAllSessionsForLogsAsync()
        {
            await using var context = CreateDbContext();

            return await context.Sessions
                .Include(s => s.Messages)
                    .ThenInclude(m => m.MediaFiles)
                .OrderByDescending(s => s.LastUsedAt)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task<List<string>> GetMediaPathsForSessionAsync(int sessionId)
        {
            await using var context = CreateDbContext();

            return await context.Media
                .Join(context.Messages,
                    media => media.ChatMessageId,
                    msg => msg.Id,
                    (media, msg) => new { media, msg })
                .Where(x => x.msg.ChatSessionId == sessionId)
                .Select(x => x.media.LocalPath)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task<List<object>> GetContextForApiAsync(int sessionId)
        {
            await using var context = CreateDbContext();

            var messages = await context.Messages
                .Include(m => m.MediaFiles)
                .Where(m => m.ChatSessionId == sessionId)
                .OrderBy(m => m.Timestamp)
                .AsNoTracking()
                .ToListAsync();

            return messages
                .Select(BuildApiMessage)
                .ToList();
        }

        private object BuildApiMessage(ChatMessage message)
        {
            string role = (message.Role ?? string.Empty).ToLowerInvariant();

            if (role == "user" && message.MediaFiles != null && message.MediaFiles.Count > 0)
            {
                var contentParts = new List<object>();

                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    contentParts.Add(new
                    {
                        type = "input_text",
                        text = message.Content
                    });
                }

                foreach (var media in message.MediaFiles)
                {
                    string? mediaPath = _mediaStorageService.ResolveMediaPath(media.LocalPath);
                    if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                        continue;

                    if (FileInputHelper.IsImageMimeType(media.MediaType))
                    {
                        string? dataUrl = ImageInputHelper.ToDataUrl(mediaPath);

                        if (!string.IsNullOrWhiteSpace(dataUrl))
                        {
                            contentParts.Add(new
                            {
                                type = "input_image",
                                image_url = dataUrl
                            });
                        }
                    }
                    else
                    {
                        string mimeType = !string.IsNullOrWhiteSpace(media.MediaType)
                            ? media.MediaType
                            : FileInputHelper.GetMimeType(mediaPath);

                        string fileData = FileInputHelper.ToDataUrl(mediaPath, mimeType);

                        if (!string.IsNullOrWhiteSpace(fileData))
                        {
                            contentParts.Add(new
                            {
                                type = "input_file",
                                filename = Path.GetFileName(mediaPath),
                                file_data = fileData
                            });
                        }
                    }
                }

                if (contentParts.Count > 0)
                {
                    return new
                    {
                        role,
                        content = contentParts.ToArray()
                    };
                }
            }

            return new
            {
                role,
                content = message.Content ?? string.Empty
            };
        }

        public async Task UpdateMessageContentAndRawJsonAsync(int messageId, string content, string rawJson)
        {
            await using var context = CreateDbContext();

            var message = await context.Messages.FirstOrDefaultAsync(m => m.Id == messageId);
            if (message == null)
                return;

            message.Content = content ?? string.Empty;
            message.RawJson = rawJson ?? string.Empty;

            await context.SaveChangesAsync();
        }

        public async Task DeleteSessionAsync(int sessionId)
        {
            await using var context = CreateDbContext();

            var session = await context.Sessions
                .Include(s => s.Messages)
                .ThenInclude(m => m.MediaFiles)
                .FirstOrDefaultAsync(s => s.Id == sessionId);

            if (session == null)
                return;

            context.Sessions.Remove(session);
            await context.SaveChangesAsync();
        }

        public async Task LinkMediaAsync(int chatMessageId, string localPath, string type)
        {
            await using var context = CreateDbContext();

            var media = new MediaFile
            {
                ChatMessageId = chatMessageId,
                LocalPath = localPath ?? string.Empty,
                MediaType = type ?? string.Empty
            };

            context.Media.Add(media);
            await context.SaveChangesAsync();
        }

        public async Task<List<ChatSession>> GetFilteredSessionsAsync(string searchTerm, string endpointFilter)
        {
            await using var context = CreateDbContext();

            var query = context.Sessions
                .Include(s => s.Messages)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(endpointFilter) &&
                !string.Equals(endpointFilter, "All", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<EndpointType>(endpointFilter, out var endpointType))
                {
                    query = query.Where(s => s.Endpoint == endpointType);
                }
            }

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                string term = searchTerm.Trim();

                query = query.Where(s =>
                    EF.Functions.Like(s.Title, $"%{term}%") ||
                    s.Messages.Any(m => EF.Functions.Like(m.Content, $"%{term}%")));
            }

            return await query
                .OrderByDescending(s => s.LastUsedAt)
                .AsNoTracking()
                .ToListAsync();
        }
        public async Task<List<string>> GetMediaPathsForMessageAsync(int messageId)
        {
            await using var context = CreateDbContext();

            return await context.Media
                .Where(m => m.ChatMessageId == messageId)
                .Select(m => m.LocalPath)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task<ChatMessage?> GetMessageAsync(int messageId)
        {
            await using var context = CreateDbContext();

            return await context.Messages
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == messageId);
        }

        public async Task<int> GetMessageCountForSessionAsync(int sessionId)
        {
            await using var context = CreateDbContext();

            return await context.Messages
                .CountAsync(m => m.ChatSessionId == sessionId);
        }

        public async Task DeleteMessageAsync(int messageId)
        {
            await using var context = CreateDbContext();

            var message = await context.Messages
                .Include(m => m.MediaFiles)
                .FirstOrDefaultAsync(m => m.Id == messageId);

            if (message == null)
                return;

            context.Messages.Remove(message);
            await context.SaveChangesAsync();
        }
    }
}
using oaiResponsesAvalonia.Data;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services
{
    public class SessionCleanupService
    {
        private readonly HistoryService _historyService;

        public SessionCleanupService(
            HistoryService historyService,
            MediaStorageService mediaStorageService)
        {
            _historyService = historyService;
            // Keep this constructor compatible with existing callers. Media files are
            // retained until a safe, coordinated garbage-collection policy exists.
            _ = mediaStorageService;
        }

        public async Task DeleteSessionAsync(int sessionId)
        {
            // A file can be shared by other messages, or become referenced by another
            // operation. Keep files rather than risking data loss; database records are
            // removed normally. A separate coordinated cleanup can reclaim orphans later.
            await _historyService.DeleteSessionAsync(sessionId);
        }

        public async Task<bool> DeleteTurnAsync(int messageId)
        {
            var message = await _historyService.GetMessageAsync(messageId);
            if (message == null)
                return false;

            int sessionId = message.ChatSessionId;

            // Preserve files, including files shared by other messages. Automatic
            // deletion will require coordinated reference checking and cleanup.
            await _historyService.DeleteMessageAsync(messageId);

            int remainingMessages = await _historyService.GetMessageCountForSessionAsync(sessionId);
            if (remainingMessages == 0)
            {
                await _historyService.DeleteSessionAsync(sessionId);
                return true;
            }

            return false;
        }
    }
}
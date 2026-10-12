using oaiResponsesAvalonia.Data;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services
{
    public class SessionCleanupService
    {
        private readonly HistoryService _historyService;
        private readonly MediaStorageService _mediaStorageService;

        public SessionCleanupService(
            HistoryService historyService,
            MediaStorageService mediaStorageService)
        {
            _historyService = historyService;
            _mediaStorageService = mediaStorageService;
        }

        public async Task DeleteSessionAsync(int sessionId)
        {
            var mediaPaths = await _historyService.GetMediaPathsForSessionAsync(sessionId);
            _mediaStorageService.DeleteFiles(mediaPaths);
            await _historyService.DeleteSessionAsync(sessionId);
        }

        public async Task<bool> DeleteTurnAsync(int messageId)
        {
            var message = await _historyService.GetMessageAsync(messageId);
            if (message == null)
                return false;

            var mediaPaths = await _historyService.GetMediaPathsForMessageAsync(messageId);
            _mediaStorageService.DeleteFiles(mediaPaths);

            int sessionId = message.ChatSessionId;

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
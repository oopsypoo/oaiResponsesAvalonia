using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services
{
    public enum DialogSeverity
    {
        Information,
        Warning,
        Error,
        Question
    }

    public interface IDialogService
    {
        Task ShowMessageAsync(object owner, string title, string message, DialogSeverity severity = DialogSeverity.Information);
        Task<bool> ConfirmAsync(object owner, string title, string message);
        Task<string?> PickFolderAsync(object owner, string title, string? currentPath = null);
        Task ShowModalAsync(object owner, object dialogWindow);
        Task<bool?> ShowModalForResultAsync(object owner, object dialogWindow);
    }
}

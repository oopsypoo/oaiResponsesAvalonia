using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace oaiResponsesAvalonia.Services
{
    public sealed class AvaloniaDialogService : IDialogService
    {
        public async Task ShowMessageAsync(object owner, string title, string message, DialogSeverity severity = DialogSeverity.Information)
        {
            var dialog = new PromptDialogWindow(title, message, severity, showConfirmationButtons: false);
            var ownerWindow = GetOwnerWindow(owner, dialog);
            if (ownerWindow is not null)
                await dialog.ShowDialog<object?>(ownerWindow);
        }

        public async Task<bool> ConfirmAsync(object owner, string title, string message)
        {
            var dialog = new PromptDialogWindow(title, message, DialogSeverity.Question, showConfirmationButtons: true);
            var ownerWindow = GetOwnerWindow(owner, dialog);
            return ownerWindow is not null && await dialog.ShowDialog<bool>(ownerWindow);
        }

        public async Task ShowModalAsync(object owner, object dialogWindow)
        {
            if (dialogWindow is Window dialog && GetOwnerWindow(owner, dialog) is { } ownerWindow)
                await dialog.ShowDialog<object?>(ownerWindow);
        }

        public async Task<bool?> ShowModalForResultAsync(object owner, object dialogWindow)
        {
            if (dialogWindow is Window dialog && GetOwnerWindow(owner, dialog) is { } ownerWindow)
                return await dialog.ShowDialog<bool?>(ownerWindow);

            return null;
        }

        private static Window? GetOwnerWindow(object owner, Window dialog)
        {
            if (owner is Window ownerWindow && !ReferenceEquals(ownerWindow, dialog))
                return ownerWindow;

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow is { } mainWindow && !ReferenceEquals(mainWindow, dialog))
                return mainWindow;

            return null;
        }
    }
}

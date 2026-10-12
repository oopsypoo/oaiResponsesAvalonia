using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using oaiResponsesAvalonia.Services;
using System.IO;
using System.Threading.Tasks;
namespace oaiResponsesAvalonia.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly IDialogService _dialogs = new AvaloniaDialogService();
        public AppSettings Settings { get; }

        public SettingsWindow() : this(new AppSettings())
        {
        }

        public SettingsWindow(AppSettings settings)
        {
            InitializeComponent();
            Settings = settings;
            DataContext = settings;
        }
        private async void BrowseAppRoot_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = await PickFolderAsync("Select Application Root Directory", AppRootTextBox.Text);
            if (string.IsNullOrWhiteSpace(selectedPath))
                return;

            if (await HasWritePermissionAsync(selectedPath))
            {
                AppRootTextBox.Text = selectedPath;
                UpdateSubPathsFromAppRoot();
            }
            else
            {
                await _dialogs.ShowMessageAsync(
                    this,
                    "Access Denied",
                    "You don't have permission to write to this folder. Please choose a different location (like your Documents folder).",
                    DialogSeverity.Warning);
            }
        }

        private async Task<bool> HasWritePermissionAsync(string folderPath)
        {
            string tempFilePath = Path.Combine(folderPath, Path.GetRandomFileName());
            try
            {
                await using (var stream = new FileStream(
                    tempFilePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1,
                    useAsync: true))
                {
                    await stream.FlushAsync();
                }

                File.Delete(tempFilePath);
                return true;
            }
            catch
            {
                try
                {
                    if (File.Exists(tempFilePath))
                        File.Delete(tempFilePath);
                }
                catch
                {
                }

                return false;
            }
        }

        private async void BrowseLogs_Click(object sender, RoutedEventArgs e) => await BrowseFolderAsync(LogsTextBox);
        private async void BrowseImages_Click(object sender, RoutedEventArgs e) => await BrowseFolderAsync(ImagesTextBox);
        /// <summary>
        /// Opens a folder browser dialog to select a folder for the given TextBox. It checks if the application has write permission to the selected folder before updating the TextBox.
        /// </summary>
        /// <param name="textBox"></param>
        private async Task BrowseFolderAsync(TextBox textBox)
        {
            var folder = await PickFolderAsync("Select the save location", textBox.Text);
            if (string.IsNullOrWhiteSpace(folder))
                return;

            if (await HasWritePermissionAsync(folder))
            {
                textBox.Text = folder;
            }
            else
            {
                await _dialogs.ShowMessageAsync(
                    this,
                    "Permission Denied",
                    "The application does not have permission to write to this folder. Please select a different location.",
                    DialogSeverity.Warning);
            }
        }

        private async Task<string?> PickFolderAsync(string title, string? currentPath)
        {
            var options = new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            };

            if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(currentPath);

            var folders = await StorageProvider.OpenFolderPickerAsync(options);
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        private void UpdateSubPathsFromAppRoot()
        {
            var appRoot = AppRootTextBox.Text;

            // If the user clears the Root, we shouldn't try to build subpaths
            if (string.IsNullOrWhiteSpace(appRoot))
                return;

            // We just update the UI text boxes. 
            // Your EnsureSavePaths() will handle the actual directory creation later.
            LogsTextBox.Text = Path.Combine(appRoot, "logs");
            ImagesTextBox.Text = Path.Combine(appRoot, "images");
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.SaveSettings(Settings);
            Close(true);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close(false);
        }
    }
}
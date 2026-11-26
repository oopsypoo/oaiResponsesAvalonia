using Avalonia.Controls;
using Avalonia.Interactivity;
using oaiResponsesAvalonia.Services;
using System;
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
            var selectedPath = await _dialogs.PickFolderAsync(this, "Select Application Root Directory", AppRootTextBox.Text);
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

        private async void BrowseImages_Click(object sender, RoutedEventArgs e) => await BrowseFolderAsync(ImagesTextBox);
        private async void BrowseFiles_Click(object sender, RoutedEventArgs e) => await BrowseFolderAsync(FilesTextBox);
        private async void BrowseSavedImages_Click(object sender, RoutedEventArgs e) => await BrowseFolderAsync(SavedImagesFolderTextBox);

        private async Task BrowseFolderAsync(TextBox textBox)
        {
            var folder = await _dialogs.PickFolderAsync(this, "Select the save location", GetInitialFolderPath(textBox));
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

        private string? GetInitialFolderPath(TextBox textBox)
        {
            try
            {
                string appRoot = AppRootTextBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(appRoot))
                    return null;

                appRoot = Path.GetFullPath(appRoot);

                if (ReferenceEquals(textBox, SavedImagesFolderTextBox))
                {
                    string savedImagesLocation = SavedImagesFolderTextBox.Text?.Trim() ?? string.Empty;
                    if (Path.IsPathFullyQualified(savedImagesLocation))
                        return Path.GetFullPath(savedImagesLocation);

                    string imagesDirectory = ResolveConfiguredPath(appRoot, ImagesTextBox.Text);
                    return string.IsNullOrWhiteSpace(savedImagesLocation)
                        ? imagesDirectory
                        : Path.GetFullPath(Path.Combine(imagesDirectory, savedImagesLocation));
                }

                return ResolveConfiguredPath(appRoot, textBox.Text);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                return null;
            }
        }

        private static string ResolveConfiguredPath(string basePath, string? configuredPath)
        {
            string path = configuredPath?.Trim() ?? string.Empty;
            return Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(basePath, path));
        }

        private void UpdateSubPathsFromAppRoot()
        {
            var appRoot = AppRootTextBox.Text;
            if (string.IsNullOrWhiteSpace(appRoot))
                return;

            ImagesTextBox.Text = Path.Combine(appRoot, "images");
            FilesTextBox.Text = Path.Combine(appRoot, "files");
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            Settings.SavedImagesFolder = SavedImagesFolderTextBox.Text?.Trim() ?? string.Empty;
            if (!AppSettings.IsValidSavedImagesFolder(Settings.SavedImagesFolder))
            {
                await _dialogs.ShowMessageAsync(
                    this,
                    "Invalid User Images Location",
                    "Choose a folder, or enter a single folder name such as 'user' to create it under the Images Folder.",
                    DialogSeverity.Warning);
                return;
            }

            try
            {
                string imagesDirectory = Path.Combine(Settings.AppRoot, Settings.ImagesFolder);
                string savedImagesDirectory = Settings.GetSavedImagesDirectory(imagesDirectory);
                Directory.CreateDirectory(savedImagesDirectory);

                if (!await HasWritePermissionAsync(savedImagesDirectory))
                {
                    await _dialogs.ShowMessageAsync(
                        this,
                        "Permission Denied",
                        "The application cannot write to the selected User Images folder. Choose another folder.",
                        DialogSeverity.Warning);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                await _dialogs.ShowMessageAsync(
                    this,
                    "Invalid User Images Location",
                    ex.Message,
                    DialogSeverity.Warning);
                return;
            }

            AppSettings.SaveSettings(Settings);
            Close(true);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close(false);
    }
}

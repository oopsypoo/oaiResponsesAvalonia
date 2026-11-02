using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using oaiResponsesAvalonia.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Views
{
    public partial class SettingsWindow : Window
    {
        private const string GoogleSafeBrowsingCredentialId = "GoogleSafeBrowsing.ApiKey";
        private readonly IDialogService _dialogs = new AvaloniaDialogService();
        private readonly ICredentialService _credentialService;
        private readonly bool _ownsCredentialService;
        public AppSettings Settings { get; }

        public SettingsWindow() : this(new AppSettings(), new KtsuCredentialService(), ownsCredentialService: true)
        {
        }

        public SettingsWindow(AppSettings settings, ICredentialService credentialService)
            : this(settings, credentialService, ownsCredentialService: false)
        {
        }

        private SettingsWindow(AppSettings settings, ICredentialService credentialService, bool ownsCredentialService)
        {
            InitializeComponent();
            Settings = settings;
            _credentialService = credentialService;
            _ownsCredentialService = ownsCredentialService;
            DataContext = settings;
            Opened += async (_, _) => await RefreshGoogleCredentialStatusAsync();
            Closed += (_, _) =>
            {
                if (_ownsCredentialService && _credentialService is IDisposable disposable)
                    disposable.Dispose();
            };
        }

        private async Task RefreshGoogleCredentialStatusAsync()
        {
            try
            {
                bool exists = await _credentialService.ExistsAsync(GoogleSafeBrowsingCredentialId);
                GoogleSafeBrowsingKeyStatusText.Text = exists
                    ? "API key is stored in the operating system credential store."
                    : "No API key is stored.";
                RemoveGoogleSafeBrowsingKeyButton.IsEnabled = exists;
            }
            catch (Exception ex)
            {
                GoogleSafeBrowsingKeyStatusText.Text = $"Secure credential storage is unavailable: {ex.Message}";
                RemoveGoogleSafeBrowsingKeyButton.IsEnabled = false;
            }
        }

        private async void SaveGoogleSafeBrowsingKey_Click(object? sender, RoutedEventArgs e)
        {
            string secret = GoogleSafeBrowsingKeyTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(secret))
            {
                GoogleSafeBrowsingKeyStatusText.Text = "Enter an API key before saving.";
                return;
            }

            try
            {
                await _credentialService.SaveAsync(GoogleSafeBrowsingCredentialId, secret);
                GoogleSafeBrowsingKeyTextBox.Clear();
                GoogleSafeBrowsingKeyStatusText.Text = "API key saved in the operating system credential store.";
                RemoveGoogleSafeBrowsingKeyButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                GoogleSafeBrowsingKeyStatusText.Text = $"Unable to save the API key: {ex.Message}";
            }
        }

        private async void RemoveGoogleSafeBrowsingKey_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                await _credentialService.DeleteAsync(GoogleSafeBrowsingCredentialId);
                GoogleSafeBrowsingKeyTextBox.Clear();
                await RefreshGoogleCredentialStatusAsync();
            }
            catch (Exception ex)
            {
                GoogleSafeBrowsingKeyStatusText.Text = $"Unable to remove the API key: {ex.Message}";
            }
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

        private async void BrowseImages_Click(object sender, RoutedEventArgs e) => await BrowseFolderAsync(ImagesTextBox);
        private async void BrowseFiles_Click(object sender, RoutedEventArgs e) => await BrowseFolderAsync(FilesTextBox);
        private async void BrowseSavedImages_Click(object sender, RoutedEventArgs e) => await BrowseFolderAsync(SavedImagesFolderTextBox);

        /// <summary>
        /// Opens a folder browser dialog to select a folder and checks write permission before updating the text box.
        /// </summary>
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

            // If the user clears the Root, we shouldn't try to build subpaths.
            if (string.IsNullOrWhiteSpace(appRoot))
                return;

            // The path initialization code creates these directories when settings are applied.
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

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close(false);
        }
    }
}

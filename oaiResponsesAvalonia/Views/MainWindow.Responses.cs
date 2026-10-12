using Markdig;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using oaiResponsesAvalonia.Data;
using oaiResponsesAvalonia.Native;
using oaiResponsesAvalonia.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
/*
 * Code for Responses-tab-controls
*/

namespace oaiResponsesAvalonia.Views
{
    public partial class MainWindow
    {
        private List<string> _allModelsFromApi = new();
        private List<string> _activeModelsForResponses = new();
        private bool _isApplyingResponsesSettings;
        private bool _responsesWebViewInitialized;
        private bool _responsesViewerPageLoaded;
        private Task? _responsesViewerPageLoadTask;
        private MarkdownViewerAssetServer? _responsesAssetServer;
        private EnvironmentCapabilityReport _environmentCapabilityReport = new EnvironmentCapabilityReport(string.Empty, null);

        private bool _bindingMarkdownThemeOptions;
        private bool _bindingPageThemeOptions;
        private List<MarkdownThemeOption> _markdownThemeOptions = new();
        private MarkdownThemeOption _selectedMarkdownTheme;
        private List<PageThemeOption> _pageThemeOptions = new();
        private PageThemeOption _selectedPageTheme;
        private PendingToolReview _pendingToolReview;
        private TaskCompletionSource<bool> _pendingToolReviewTcs;
        
        private string ApplyModelsToResponsesCombo(IEnumerable<string> models, string preferredModel = "gpt-4o")
        {
            var list = models
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _activeModelsForResponses = list;

            cmbResponsesModel.Items.Clear();

            foreach (string model in list)
            {
                cmbResponsesModel.Items.Add(new ComboBoxItem
                {
                    Content = model,
                    Tag = model
                });
            }

            if (list.Count == 0)
            {
                return string.Empty;
            }

            string selectedModel =
                list.FirstOrDefault(m => m.Equals(_responsesClient.CurrentModel, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(m => m.Equals(preferredModel, StringComparison.OrdinalIgnoreCase))
                ?? list[0];

            int selectedIndex = list.FindIndex(m => m.Equals(selectedModel, StringComparison.OrdinalIgnoreCase));
            cmbResponsesModel.SelectedIndex = selectedIndex;

            return selectedModel;
        }
        /// <summary>
        /// Retrieves a predefined list of response model identifiers available for use within the application.
        /// </summary>
        /// <remarks>The returned model identifiers include both reasoning-enabled and non-reasoning
        /// variants to support a range of use cases. This method is intended for scenarios where a fixed set of
        /// supported models is required, such as populating selection controls or validating user input.</remarks>
        /// <returns>A list of strings containing the identifiers of supported response models, including variants from the GPT-5
        /// and GPT-4 families as well as dedicated reasoning models.</returns>
        private List<string> GetHardcodedResponseModels()
        {
            return new List<string>
            {
                // Frontier GPT-5 family (reasoning-enabled)
                "gpt-5.2",
                "gpt-5.2-pro",
                "gpt-5.1",
                "gpt-5-pro",
                "gpt-5-mini",
                // GPT-4.1 family (non-reasoning)
                "gpt-4.1",
                "gpt-4.1-mini",
                // GPT-4o series
                "gpt-4o",
                "gpt-4o-mini",
                // Dedicated reasoning models (o-series)
                "o3",
                "o3-pro",
                "o3-mini",
                "o4-mini"
            };
        }
        private async Task LoadApiModelsCacheAsync()
        {
            try
            {
                _allModelsFromApi = await ModelApiService.GetAvailableModelsAsync(OpenAPIKey);
            }
            catch
            {
                // Ignore - fallback already exists
            }
        }
        private async Task InitResponsesControlsAsync()
        {
            _responsesClient = new Responses(OpenAPIKey);

            List<string> modelsToUse = AvailableModelsStorage.Load();

            if (modelsToUse.Count == 0)
            {
                try
                {
                    _allModelsFromApi = await ModelApiService.GetAvailableModelsAsync(OpenAPIKey);
                    modelsToUse = _allModelsFromApi;
                }
                catch
                {
                    modelsToUse = GetHardcodedResponseModels();
                }
            }
            else
            {
                // Optional: load API list in background so AvailableModels window can use full live list later
                _ = LoadApiModelsCacheAsync();
            }

            if (modelsToUse.Count == 0)
            {
                modelsToUse = GetHardcodedResponseModels();
            }

            ApplyModelsToResponsesCombo(modelsToUse, "gpt-4o");

            ResponsesState.SelectedModel = ApplyModelsToResponsesCombo(modelsToUse, "gpt-4o");
            ResponsesState.SelectedReasoning = "none";
            ResponsesState.UseTextTool = true;
            ResponsesState.UseWebSearch = false;
            ResponsesState.UseImageGeneration = false;
            ResponsesState.SearchContextSize = "medium";
            ResponsesState.ImageGenQuality = "auto";
            ResponsesState.ImageGenSize = "auto";
            ResponsesState.ImageGenOutputFormat = "jpeg";
            ResponsesState.ImageGenOutputCompression = 85;
            ResponsesState.ImageGenBackground = "auto";
            //--------Developer function-tool-calls-----------
            ResponsesState.UseDeveloperTools = false;
            ResponsesState.DeveloperRepositoryRoot = string.Empty;
            ResponsesState.DeveloperScope = "repository";
            ResponsesState.DeveloperAllowReadOnlyOnly = true;
            ResponsesState.DeveloperRequireConfirmation = false;
            ResponsesState.DeveloperRequireWriteConfirmation = true;
            ResponsesState.DeveloperRequireExecutionConfirmation = true;
            ResponsesState.DeveloperShowToolLogs = true;
            ResponsesState.DeveloperToolSearchProjectText = true;
            ResponsesState.DeveloperToolReadProjectFile = true;
            ResponsesState.DeveloperToolListProjectFiles = false;
            ResponsesState.DeveloperToolRunDiagnostics = false;
            ResponsesState.DeveloperToolCreateDotNetSolution = false;
            ResponsesState.DeveloperToolBuildDotNetProject = false;
            ResponsesState.DeveloperToolRunDotNetProject = false;
            ResponsesState.DeveloperAllowedExtensionsCsv = ResponsesPanelState.GetDefaultAllowedExtensionsCsv();
            ApplyResponsesStateToClient();
        }
        private ChatMessage GetSelectedResponseMessage()
        {
            return ResponsesState.SelectedTurn;
        }

        private string GetPrimaryAttachmentPath(ChatMessage message)
        {
            var media = message?.MediaFiles?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.LocalPath));
            return media == null ? null : _mediaStorageService.ResolveMediaPath(media.LocalPath);
        }

        private bool IsImageMediaFile(MediaFile media)
        {
            string path = _mediaStorageService.ResolveMediaPath(media?.LocalPath);
            return media != null &&
                   !string.IsNullOrWhiteSpace(path) &&
                   File.Exists(path) &&
                   !string.IsNullOrWhiteSpace(media.MediaType) &&
                   media.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        private string GetPrimaryImagePath(ChatMessage message)
        {
            var media = message?.MediaFiles?.FirstOrDefault(IsImageMediaFile);
            return media == null ? null : _mediaStorageService.ResolveMediaPath(media.LocalPath);
        }

        private List<MediaFile> GetImageMediaFiles(ChatMessage message)
        {
            return message?.MediaFiles?.Where(IsImageMediaFile).ToList() ?? new List<MediaFile>();
        }

        private void ShowResponsesImagePreview(string path)
        {
            path = _mediaStorageService.ResolveMediaPath(path);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                HideResponsesImagePreview();
                return;
            }

            imgResponsesPreview.Source = GetImageSource(path);
            _responsesPreviewImagePath = path;

            borderResponsesImage.IsVisible = true;
            gridResponsesPromptImage.ColumnDefinitions[0].Width = new GridLength(2, GridUnitType.Star);
            gridResponsesPromptImage.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        }

        private void HideResponsesImagePreview()
        {
            imgResponsesPreview.Source = null;
            _responsesPreviewImagePath = string.Empty;

            borderResponsesImage.IsVisible = false;
            gridResponsesPromptImage.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            gridResponsesPromptImage.ColumnDefinitions[1].Width = new GridLength(0);
        }
        private void ReplaceResponsePreviewImages(IEnumerable<MediaFile> images)
        {
            ResponsePreviewImages.Clear();

            if (images == null)
                return;

            foreach (var image in images)
            {
                ResponsePreviewImages.Add(image);
            }
        }

        private void ClearResponsePreviewImages()
        {
            ResponsePreviewImages.Clear();

            if (lstResponsesImages != null)
                lstResponsesImages.SelectedItem = null;

            if (txtResponsesPreviewInfo != null)
                txtResponsesPreviewInfo.Text = string.Empty;
        }

        private void UpdateResponsesPreviewInfo()
        {
            if (txtResponsesPreviewInfo == null)
                return;

            if (ResponsePreviewImages.Count == 0)
            {
                txtResponsesPreviewInfo.Text = string.Empty;
                return;
            }

            if (lstResponsesImages?.SelectedItem is MediaFile selected)
            {
                int index = ResponsePreviewImages.IndexOf(selected);
                txtResponsesPreviewInfo.Text = index >= 0
                    ? $"{index + 1} / {ResponsePreviewImages.Count}"
                    : string.Empty;
                return;
            }

            txtResponsesPreviewInfo.Text = $"1 / {ResponsePreviewImages.Count}";
        }

        private void ShowResponsesImageGallery(ChatMessage message, string preferredPath = null)
        {
            var images = GetImageMediaFiles(message);
            ReplaceResponsePreviewImages(images);

            if (images.Count == 0)
            {
                ClearResponsePreviewImages();
                HideResponsesImagePreview();
                return;
            }

            MediaFile selected =
                !string.IsNullOrWhiteSpace(preferredPath)
                    ? images.FirstOrDefault(m =>
                        string.Equals(_mediaStorageService.ResolveMediaPath(m.LocalPath), preferredPath, StringComparison.OrdinalIgnoreCase))
                    : null;

            selected ??= images[0];

            if (lstResponsesImages != null)
                lstResponsesImages.SelectedItem = selected;

            ShowResponsesImagePreview(_mediaStorageService.ResolveMediaPath(selected.LocalPath));
            UpdateResponsesPreviewInfo();
        }

        private void ReplaceCurrentChatMessages(IEnumerable<ChatMessage> history)
        {
            CurrentChatMessages.Clear();

            foreach (var message in history)
            {
                CurrentChatMessages.Add(message);
            }
        }

        private async Task LoadResponsesSessionAsync(int sessionId, bool restoreLastUserPrompt = true)
        {
            if (sessionId <= 0)
                return;

            PendingResponseAttachments.Clear();
            _appStatus.Set("Update attachment panel...");
            UpdatePendingAttachmentsPanel();
            _appStatus.Set("Loading session history...");
            var history = await _historyService.GetFullSessionHistoryAsync(sessionId);

            ReplaceCurrentChatMessages(history);
            _appStatus.Set($"Rendering {history.Count} messages...");
            ApplyResponsesSettingsFromHistory(history);
            ClearResponsePreviewImages();
            ClearDeveloperToolCallLogs();
            if (CurrentChatMessages.Count > 0)
            {
                var lastMessage = CurrentChatMessages.Last();
                ResponsesState.SelectedTurn = lastMessage;
                lstResponsesTurns.ScrollIntoView(lastMessage);
            }
            else
            {
                ResponsesState.SelectedTurn = null;
            }

            if (restoreLastUserPrompt)
            {
                var lastUserMessage = history.LastOrDefault(m =>
                    string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));

                if (lastUserMessage != null)
                {
                    ResponsesState.PromptText = lastUserMessage.Content;
                }
            }

            if (ResponsesState.SelectedTurn is ChatMessage selected)
            {
                if (string.Equals(selected.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    await RenderResponsesMarkdownAsync(selected.Content);
                    ApplyDeveloperToolCallLogJson(selected.ToolCallLogJson);
                }
                else
                {
                    await RenderResponsesMarkdownAsync(string.Empty);
                    ClearDeveloperToolCallLogs();
                }
                _appStatus.Set("Showing Image gallery ");
                ShowResponsesImageGallery(selected);
            }
            else
            {
                await RenderResponsesMarkdownAsync(string.Empty);
                ClearResponsePreviewImages();
                HideResponsesImagePreview();
                DeveloperToolCallLogs.Clear();
                ClearDeveloperToolCallLogs();
            }
        }

        private async Task ResetResponsesUi(bool clearPrompt = true, bool preserveWorkspaceRoot = false)
        {
            string workspaceRoot = preserveWorkspaceRoot
                ? ResponsesState.DeveloperRepositoryRoot ?? string.Empty
                : string.Empty;

            CurrentChatMessages.Clear();
            ResponsesState.SelectedTurn = null;
            
            if (clearPrompt)
                ResponsesState.PromptText = string.Empty;

            _responsesImagePath = string.Empty;
            _responsesPreviewImagePath = string.Empty;
            ClearResponsePreviewImages();
            ClearDeveloperToolCallLogs();
            HideResponsesImagePreview();
            ResetDeveloperToolsState();

            if (!string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot))
                ResponsesState.DeveloperRepositoryRoot = Path.GetFullPath(workspaceRoot);

            await RenderResponsesMarkdownAsync(string.Empty);
            if (_responsesClient != null)
                _responsesClient.ClearConversation();
        }
        private void ResetDeveloperToolsState()
        {
            ResponsesState.UseDeveloperTools = false;
            ResponsesState.DeveloperRepositoryRoot = string.Empty;
            ResponsesState.DeveloperScope = "repository";
            ResponsesState.DeveloperAllowReadOnlyOnly = true;
            ResponsesState.DeveloperRequireConfirmation = false;
            ResponsesState.DeveloperRequireWriteConfirmation = true;
            ResponsesState.DeveloperRequireExecutionConfirmation = true;
            ResponsesState.DeveloperShowToolLogs = true;
            ResponsesState.DeveloperToolSearchProjectText = true;
            ResponsesState.DeveloperToolReadProjectFile = true;
            ResponsesState.DeveloperToolListProjectFiles = false;
            ResponsesState.DeveloperToolRunDiagnostics = false;
            ResponsesState.DeveloperToolCreateDotNetSolution = false;
            ResponsesState.DeveloperToolBuildDotNetProject = false;
            ResponsesState.DeveloperToolRunDotNetProject = false;
            ResponsesState.DeveloperToolWriteProjectFile = false;
            ResponsesState.DeveloperToolReplaceInProjectFile = false;
            ResponsesState.DeveloperPendingReviewVisible = false;
            ResponsesState.DeveloperPendingReviewTitle = string.Empty;
            ResponsesState.DeveloperPendingReviewToolName = string.Empty;
            ResponsesState.DeveloperPendingReviewSummary = string.Empty;
            ResponsesState.DeveloperAllowedExtensionsCsv =
                ResponsesPanelState.GetDefaultAllowedExtensionsCsv();
        }
        private void UpdatePendingAttachmentsPanel()
        {
            if (borderResponsesAttachments != null)
            {
                borderResponsesAttachments.IsVisible = PendingResponseAttachments.Count > 0;
            }
        }

        private void AddPendingResponseAttachments(IEnumerable<string> filePaths)
        {
            if (filePaths == null)
                return;

            ResponseAttachmentItem lastAddedImage = null;

            foreach (string filePath in filePaths)
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    continue;

                bool alreadyExists = PendingResponseAttachments.Any(a =>
                    string.Equals(a.LocalPath, filePath, StringComparison.OrdinalIgnoreCase));

                if (alreadyExists)
                    continue;

                var item = new ResponseAttachmentItem
                {
                    LocalPath = filePath,
                    MediaType = FileInputHelper.GetMimeType(filePath)
                };

                PendingResponseAttachments.Add(item);

                if (item.IsImage)
                    lastAddedImage = item;
            }

            UpdatePendingAttachmentsPanel();

            if (lastAddedImage != null)
            {
                ShowResponsesImagePreview(lastAddedImage.LocalPath);
            }
            else if (PendingResponseAttachments.Count == 0)
            {
                HideResponsesImagePreview();
            }
        }
        private void ClearPendingResponseAttachments()
        {
            PendingResponseAttachments.Clear();
            UpdatePendingAttachmentsPanel();

            _responsesImagePath = string.Empty;

            if (ResponsesState.SelectedTurn != null)
            {
                string selectedTurnImage = GetPrimaryImagePath(ResponsesState.SelectedTurn);
                if (!string.IsNullOrWhiteSpace(selectedTurnImage) && File.Exists(selectedTurnImage))
                    ShowResponsesImagePreview(selectedTurnImage);
                else
                    HideResponsesImagePreview();
            }
            else
            {
                HideResponsesImagePreview();
            }
        }

        private async Task OpenLocalFileAsync(string path, string caption)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                await _dialogs.ShowMessageAsync(
                    this,
                    "Error",
                    $"Could not open {caption}:\n{ex.Message}",
                    DialogSeverity.Error);
            }
        }
        private string GetCurrentPreviewImagePath()
        {
            if (lstResponsesImages?.SelectedItem is MediaFile selected &&
                !string.IsNullOrWhiteSpace(selected.LocalPath) &&
                File.Exists(_mediaStorageService.ResolveMediaPath(selected.LocalPath)))
            {
                return _mediaStorageService.ResolveMediaPath(selected.LocalPath);
            }

            if (!string.IsNullOrWhiteSpace(_responsesPreviewImagePath) &&
                File.Exists(_responsesPreviewImagePath))
            {
                return _responsesPreviewImagePath;
            }

            if (!string.IsNullOrWhiteSpace(_responsesImagePath) &&
                File.Exists(_responsesImagePath))
            {
                return _responsesImagePath;
            }

            return null;
        }

        private void ShowFirstAssistantImageOfSelectedTurn()
        {
            var message = GetSelectedResponseMessage();

            if (message == null ||
                !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                ClearResponsePreviewImages();
                HideResponsesImagePreview();
                return;
            }

            ShowResponsesImageGallery(message);
        }

        private async void btnResponsesSendRequestClick(object sender, RoutedEventArgs e)
        {
            string userPrompt = ResponsesState.PromptText ?? string.Empty;
            bool hasAttachedFiles = PendingResponseAttachments.Count > 0;

            if (string.IsNullOrWhiteSpace(userPrompt) && !hasAttachedFiles)
                return;

            ResponsesState.IsRequestInProgress = true;
            using (_appStatus.Operation("Preparing request..."))
            {
                try
                {
                    string model = ResponsesState.SelectedModel;
                    string reasoning = ResponsesState.SelectedReasoning;
                    string imgSize = ResponsesState.ImageGenSize;
                    string imgQual = ResponsesState.ImageGenQuality;
                    string searchSize = ResponsesState.SearchContextSize;
                    string imageToolSettingsJson = BuildImageToolSettingsJson();
                    string developerToolSettingsJson = BuildDeveloperToolSettingsJson();

                    string toolsCsv = string.Join(",",
                        _responsesClient.ActiveTools
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase));

                    string titleSeed = !string.IsNullOrWhiteSpace(userPrompt)
                                    ? userPrompt
                                    : (PendingResponseAttachments.FirstOrDefault()?.FileName ?? "[attachment prompt]");
                    _appStatus.Set("Ensuring session...");
                    int sid = await EnsureSessionActiveAsync(EndpointType.Responses, titleSeed);
                    _appStatus.Set("Add user-message to DB");
                    int userMsgId = await _historyService.AddMessageAsync(
                        sid,
                        "user",
                        userPrompt,
                        model: model,
                        reasoning: reasoning,
                        tools: toolsCsv,
                        imgSize: imgSize,
                        imgQual: imgQual,
                        searchSize: searchSize,
                        imageToolSettingsJson: imageToolSettingsJson,
                        developerToolSettingsJson: developerToolSettingsJson);

                    if (hasAttachedFiles)
                    {
                        int i = 0;
                        var list = PendingResponseAttachments.ToList();
                        _appStatus.Set($"Attaching {list.Count} file(s)...");
                        foreach (var attachment in list)
                        {
                            i++;
                            _appStatus.Set($"Attaching file {i}/{list.Count}: {attachment.FileName}");
                            string storedPath = _mediaStorageService.ImportUserFile(attachment.LocalPath);

                            if (!string.IsNullOrWhiteSpace(storedPath))
                            {
                                await _historyService.LinkMediaAsync(userMsgId, storedPath, attachment.MediaType);
                            }
                        }
                    }
                    _appStatus.Set("Getting context for API");
                    var context = await _historyService.GetContextForApiAsync(sid);
                    var developerToolsOptions = BuildDeveloperToolsOptionsFromState();
                    // We are sending full DB history, so start a fresh response chain.
                    ClearDeveloperToolCallLogs();
                    _responsesClient.ClearConversation();
                    _appStatus.Set("GetChatCompletionWLocal tools.");
                    var progress = new Progress<string>(msg => _appStatus.Set(msg));
                    var result = await _responsesClient.GetChatCompletionWithLocalToolsAsync(
                        context,
                        developerToolsOptions,
                        confirmLocalCallAsync: ConfirmDeveloperToolCallAsync,
                        onToolCallLoggedAsync: LogDeveloperToolCallAsync,
                        onWorkspaceRootChangedAsync: UpdateDeveloperWorkspaceRootAsync,
                        progress: progress);
                    developerToolSettingsJson = BuildDeveloperToolSettingsJson();
                    string toolCallLogJson = BuildDeveloperToolCallLogJson();
                    if (result != null)
                    {
                        _appStatus.Set("Add assistant message to DB");
                        int assistantMsgId = await _historyService.AddMessageAsync(
                            sid,
                            "assistant",
                            result.AssistantText,
                            result.RawJson,
                            model: model,
                            reasoning: reasoning,
                            tools: toolsCsv,
                            imgSize: imgSize,
                            imgQual: imgQual,
                            searchSize: searchSize,
                            developerToolSettingsJson: developerToolSettingsJson,
                            toolCallLogJson: toolCallLogJson);


                        if (result.ImagePayloads?.Count > 0)
                        {
                            _appStatus.Set($"Saving {result.ImagePayloads.Count} images...");
                            var paths = _mediaStorageService.SaveAssistantImages(
                                result.ImagePayloads,
                                result.ImageOutputFormat);

                            foreach (var path in paths)
                            {
                                await _historyService.LinkMediaAsync(
                                    assistantMsgId,
                                    path,
                                    ImageInputHelper.GetMimeType(path));
                            }
                        }
                        _appStatus.Set("Refreshing chat UI...");
                        ClearPendingResponseAttachments();

                        await RefreshCurrentChatUI(sid);
                    }
                    else
                    {
                        _appStatus.Set("No response");
                    }
                }
                catch (Exception ex)
                {
                    _appStatus.Set("Error: " + ex.Message);
                    await _dialogs.ShowMessageAsync(
                        this,
                        "Execution Error",
                        ex.Message,
                        DialogSeverity.Error);
                }
                finally
                {
                    ResponsesState.IsRequestInProgress = false;
                }
            }
        }


        // New helper to keep UI in sync with DB
        private async Task RefreshCurrentChatUI(int sessionId)
        {
            using (_appStatus.Operation("Refreshing Current chat UI..."))
            {
                await LoadResponsesSessionAsync(sessionId, restoreLastUserPrompt: false);
            }
        }

        private async void btnResponsesNewChat_Click(object sender, RoutedEventArgs e)
        {
            _activeResponsesSessionId = null;
            await ResetResponsesUi(clearPrompt: true, preserveWorkspaceRoot: true);

            _appStatus.Set("New session started. The previous workspace root is retained; Developer Tools remains disabled until you enable it.");
        }

        private async void btnResponsesDeleteChat_Click(object sender, RoutedEventArgs e)
        {
            if (_activeResponsesSessionId == null)
                return;

            if (ResponsesState.SelectedTurn is not ChatMessage selectedTurn)
                return;

            bool confirm = await _dialogs.ConfirmAsync(
                this,
                "Confirm",
                "Delete the currently selected turn?");

            if (!confirm)
                return;

            _appStatus.Set("Deleting current turn...");

            bool sessionDeleted = await _sessionCleanupService.DeleteTurnAsync(selectedTurn.Id);

            if (sessionDeleted)
            {
                _activeResponsesSessionId = null;
                await ResetResponsesUi(clearPrompt: true);
            }
            else
            {
                await RefreshCurrentChatUI(_activeResponsesSessionId.Value);
            }

            RefreshLogsTab();
        }

        private async void lstResponsesTurns_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_responsesClient == null || _isApplyingResponsesSettings)
                return;

            if (lstResponsesTurns.SelectedItem is not ChatMessage selectedMsg)
                return;

            ResponsesState.SelectedTurn = selectedMsg;

            if (string.Equals(selectedMsg.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                ResponsesState.PromptText = selectedMsg.Content;
                await RenderResponsesMarkdownAsync(string.Empty);
                ShowResponsesImageGallery(selectedMsg);
                ClearDeveloperToolCallLogs();
            }
            else if (string.Equals(selectedMsg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                await RenderResponsesMarkdownAsync(selectedMsg.Content);
                ShowResponsesImageGallery(selectedMsg);
                ApplyDeveloperToolCallLogJson(selectedMsg.ToolCallLogJson);
            }
            else
            {
                ClearResponsePreviewImages();
                HideResponsesImagePreview();
                DeveloperToolCallLogs.Clear();
            }
        }

        private void lstResponsesImages_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (lstResponsesImages.SelectedItem is not MediaFile selectedImage)
                return;

            string imagePath = _mediaStorageService.ResolveMediaPath(selectedImage.LocalPath);
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return;

            ShowResponsesImagePreview(imagePath);
            UpdateResponsesPreviewInfo();
        }

        private async void btnResponsesAttachImage_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select an image for this message",
                AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(savepath_images),
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Image Files") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif" } },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
                }
            });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { } localPath)
            {
                _responsesImagePath = localPath;
                ShowResponsesImagePreview(_responsesImagePath);
            }
        }
        private void btnResponsesRemoveImage_Click(object? sender, RoutedEventArgs e)
        {
            _responsesImagePath = string.Empty;
            HideResponsesImagePreview();
        }
        //This is probably unneccessary. Check it out first. Is it really functional?
        //Does it make sense do double-click inside the chat-history.
        //Double-clicking on the preview-image is logical...here maybe not so.
        //REMOVE or NOT? test first
        private async void lstResponsesTurns_MouseDoubleClick(object? sender, TappedEventArgs e)
        {
            if (lstResponsesTurns.SelectedItem is not ChatMessage message)
                return;

            string path = GetPrimaryAttachmentPath(message);

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                await _dialogs.ShowMessageAsync(this, "Error", $"Could not open image:\n{ex.Message}", DialogSeverity.Error);
            }
        }

        private async void MenuItemImageOpen_Click(object? sender, RoutedEventArgs e)
        {
            var path = GetCurrentPreviewImagePath();
            if (path == null) return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                await _dialogs.ShowMessageAsync(this, "Error", $"Could not open image:\n{ex.Message}", DialogSeverity.Error);
            }
        }
        /// <summary>
        /// Handles the Click event of the 'Open With' menu item for an image, displaying the system 'Open with' dialog
        /// for the currently selected image file.
        /// </summary>
        /// <remarks>If no image is selected or the file does not exist, an informational message is shown
        /// and the dialog is not displayed. If an error occurs while attempting to show the 'Open with' dialog, an
        /// error message is displayed to the user.</remarks>
        /// <param name="sender">The source of the event, typically the menu item that was clicked.</param>
        /// <param name="e">The event data associated with the click event.</param>
        private async void MenuItemImageOpenWith_Click(object? sender, RoutedEventArgs e)
        {
            var path = GetCurrentPreviewImagePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                await _dialogs.ShowMessageAsync(this, "Open with", "No image selected or file not found.");
                return;
            }

            try
            {
                ShellLauncher.ShowOpenWithDialog(this, path);
            }
            catch (Exception ex)
            {
                await _dialogs.ShowMessageAsync(this, "Error", $"Could not show Open with dialog:\n{ex.Message}", DialogSeverity.Error);
            }
        }
        private static bool HasResponsesSettings(ChatMessage message)
        {
            if (message == null)
                return false;

            return
                !string.IsNullOrWhiteSpace(message.ModelUsed) ||
                !string.IsNullOrWhiteSpace(message.ReasoningLevel) ||
                !string.IsNullOrWhiteSpace(message.ActiveTools) ||
                !string.IsNullOrWhiteSpace(message.SearchContextSize) ||
                !string.IsNullOrWhiteSpace(message.ImageSize) ||
                !string.IsNullOrWhiteSpace(message.ImageQuality) ||
                !string.IsNullOrWhiteSpace(message.ImageToolSettingsJson) ||
                !string.IsNullOrWhiteSpace(message.DeveloperToolSettingsJson);
        }


        //see use-case in ApplyResponsesSettingsFromHistory. It explains why it still is not used
        /// <summary>
        /// Ensures that the specified model is present in the responses model selection list. If the model does not
        /// already exist, it is added to the list.
        /// </summary>
        /// <remarks>This method does not add duplicate entries. The check is case-insensitive and
        /// considers both the content and tag of each item. If the model is not already tracked, it is also added to
        /// the active models collection.</remarks>
        /// <param name="model">The name of the model to ensure exists in the responses model selection list. Cannot be null, empty, or
        /// whitespace.</param>
        private void EnsureResponsesModelExists(string model)
        {
            if (string.IsNullOrWhiteSpace(model))
                return;

            bool exists = cmbResponsesModel.Items
                .OfType<ComboBoxItem>()
                .Any(i =>
                    string.Equals(i.Tag?.ToString(), model, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(i.Content?.ToString(), model, StringComparison.OrdinalIgnoreCase));

            if (exists)
                return;

            cmbResponsesModel.Items.Insert(0, new ComboBoxItem
            {
                Content = model,
                Tag = model
            });

            if (!_activeModelsForResponses.Contains(model, StringComparer.OrdinalIgnoreCase))
                _activeModelsForResponses.Insert(0, model);
        }
        //This is a point: If the setting(ModelUsed) does not exist we can add it.
        //But this will be a 'problem' when reading from the model-list that is
        //created. This value will then be removed again. Maybe it should be saved by using
        //AvailableModelsStorage (new_list). Think about it before doing any changes.
        //EnsureResponsesModelExists(settingsMessage.ModelUsed);
        private void ApplyResponsesSettingsFromHistory(IReadOnlyList<ChatMessage> history)
        {
            if (history == null || history.Count == 0)
                return;

            var settingsMessage = history.LastOrDefault(HasResponsesSettings);
            if (settingsMessage == null)
                return;

            _isApplyingResponsesSettings = true;
            try
            {
                ResponsesState.SelectedModel = settingsMessage.ModelUsed;
                ResponsesState.SelectedReasoning = string.IsNullOrWhiteSpace(settingsMessage.ReasoningLevel)
                    ? "none"
                    : settingsMessage.ReasoningLevel;

                ResponsesState.SearchContextSize = string.IsNullOrWhiteSpace(settingsMessage.SearchContextSize)
                    ? "medium"
                    : settingsMessage.SearchContextSize;

                ResponsesState.ImageGenQuality = string.IsNullOrWhiteSpace(settingsMessage.ImageQuality)
                    ? "auto"
                    : settingsMessage.ImageQuality;

                ResponsesState.ImageGenSize = string.IsNullOrWhiteSpace(settingsMessage.ImageSize)
                    ? "auto"
                    : settingsMessage.ImageSize;

                ResponsesState.ImageGenOutputFormat = "jpeg";
                ResponsesState.ImageGenBackground = "auto";
                ResponsesState.ImageGenOutputCompression = 85;
                ApplyResponsesToolsToState(settingsMessage.ActiveTools);
                ApplyDeveloperToolSettingsFromJson(settingsMessage.DeveloperToolSettingsJson);
            }
            finally
            {
                _isApplyingResponsesSettings = false;
            }

            NormalizeResponsesToolsState();
            ApplyResponsesStateToClient();
            ApplyDeveloperToolSettingsFromJson(settingsMessage.DeveloperToolSettingsJson);
        }
        private void NormalizeResponsesToolsState()
        {
            if (_isApplyingResponsesSettings)
                return;

            _isApplyingResponsesSettings = true;
            try
            {
                bool anyNonText =
                    ResponsesState.UseWebSearch ||
                    ResponsesState.UseImageGeneration;

                if (ResponsesState.UseTextTool && anyNonText)
                {
                    ResponsesState.UseTextTool = false;
                }

                if (!ResponsesState.UseTextTool && !anyNonText)
                {
                    ResponsesState.UseTextTool = true;
                }

                if (ResponsesState.UseTextTool)
                {
                    ResponsesState.UseWebSearch = false;
                    ResponsesState.UseImageGeneration = false;
                }
            }
            finally
            {
                _isApplyingResponsesSettings = false;
            }
        }
        private void ApplyResponsesStateToClient()
        {
            if (_responsesClient == null)
                return;

            _responsesClient.CurrentModel = ResponsesState.SelectedModel;
            _responsesClient.CurrentReasoning = ResponsesState.SelectedReasoning;
            _responsesClient.WebSearchContextSize = ResponsesState.SearchContextSize;
            _responsesClient.ImageGenQuality = ResponsesState.ImageGenQuality;
            _responsesClient.ImageGenSize = ResponsesState.ImageGenSize;
            _responsesClient.ImageGenOutputFormat = ResponsesState.ImageGenOutputFormat;
            _responsesClient.ImageGenOutputCompression = ResponsesState.ImageGenOutputCompression;
            _responsesClient.ImageGenBackground = ResponsesState.ImageGenBackground;
            
            _responsesClient.ActiveTools.Clear();

            if (ResponsesState.UseTextTool)
                _responsesClient.ActiveTools.Add(ResponseToolKeys.Text);

            if (ResponsesState.UseWebSearch)
                _responsesClient.ActiveTools.Add(ResponseToolKeys.WebSearch);

            if (ResponsesState.UseImageGeneration)
                _responsesClient.ActiveTools.Add(ResponseToolKeys.ImageGeneration);

            if (_responsesClient.ActiveTools.Count == 0)
                _responsesClient.ActiveTools.Add(ResponseToolKeys.Text);
        }

        private void ValidateResponsesState()
        {
            string model = ResponsesState.SelectedModel;
            string reasoning = ResponsesState.SelectedReasoning;

            bool supportsReasoning = model.StartsWith("gpt-5") ||
                                     model.StartsWith("o") ||
                                     model == "gpt-5-pro";

            if (!supportsReasoning && reasoning != "none")
            {
                ResponsesState.SelectedReasoning = "none";
                _appStatus.Set($"Model '{model}' does not support reasoning settings.");
            }
            else if (model == "gpt-5-pro" && reasoning != "high")
            {
                ResponsesState.SelectedReasoning = "high";
                _appStatus.Set("gpt-5-pro requires high reasoning.");
            }
        }
        private sealed class ImageToolSettingsSnapshot
        {
            [JsonPropertyName("quality")]
            public string Quality { get; set; } = "auto";

            [JsonPropertyName("size")]
            public string Size { get; set; } = "auto";

            [JsonPropertyName("output_format")]
            public string OutputFormat { get; set; } = "jpeg";

            [JsonPropertyName("output_compression")]
            public int? OutputCompression { get; set; }

            [JsonPropertyName("background")]
            public string Background { get; set; } = "auto";

        }

        private sealed class DeveloperToolSettingsSnapshot
        {
            [JsonPropertyName("enabled")]
            public bool Enabled { get; set; }

            [JsonPropertyName("repository_root")]
            public string RepositoryRoot { get; set; } = string.Empty;

            [JsonPropertyName("scope")]
            public string Scope { get; set; } = "repository";

            [JsonPropertyName("search_project_text")]
            public bool SearchProjectText { get; set; } = true;

            [JsonPropertyName("read_project_file")]
            public bool ReadProjectFile { get; set; } = true;

            [JsonPropertyName("list_project_files")]
            public bool ListProjectFiles { get; set; }

            [JsonPropertyName("run_diagnostics")]
            public bool RunDiagnostics { get; set; }
            
            [JsonPropertyName("create_dotnet_solution")]
            public bool CreateDotNetSolution { get; set; }

            [JsonPropertyName("build_dotnet_project")]
            public bool BuildDotNetProject { get; set; }

            [JsonPropertyName("run_dotnet_project")]
            public bool RunDotNetProject { get; set; }

            [JsonPropertyName("write_project_file")]
            public bool WriteProjectFile { get; set; }

            [JsonPropertyName("replace_in_project_file")]
            public bool ReplaceInProjectFile { get; set; }

            [JsonPropertyName("read_only_only")]
            public bool ReadOnlyOnly { get; set; } = true;

            [JsonPropertyName("require_confirmation")]
            public bool RequireConfirmation { get; set; }

            [JsonPropertyName("require_write_confirmation")]
            public bool RequireWriteConfirmation { get; set; } = true;

            [JsonPropertyName("require_execution_confirmation")]
            public bool RequireExecutionConfirmation { get; set; } = true;

            [JsonPropertyName("show_tool_logs")]
            public bool ShowToolLogs { get; set; } = true;

            [JsonPropertyName("allowed_extensions_csv")]
            public string AllowedExtensionsCsv { get; set; } =
                ResponsesPanelState.GetDefaultAllowedExtensionsCsv();
            
        }
        private string BuildImageToolSettingsJson()
        {
            if (!ResponsesState.UseImageGeneration)
                return string.Empty;

            var snapshot = new ImageToolSettingsSnapshot
            {
                Quality = string.IsNullOrWhiteSpace(ResponsesState.ImageGenQuality)
                    ? "auto"
                    : ResponsesState.ImageGenQuality,

                Size = string.IsNullOrWhiteSpace(ResponsesState.ImageGenSize)
                    ? "auto"
                    : ResponsesState.ImageGenSize,

                OutputFormat = string.IsNullOrWhiteSpace(ResponsesState.ImageGenOutputFormat)
                    ? "jpeg"
                    : ResponsesState.ImageGenOutputFormat,

                OutputCompression = ResponsesState.IsOutputCompressionEnabled
                    ? ResponsesState.ImageGenOutputCompression
                    : null,

                Background = string.IsNullOrWhiteSpace(ResponsesState.ImageGenBackground)
                    ? "auto"
                    : ResponsesState.ImageGenBackground,

            };

            return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        private string BuildDeveloperToolSettingsJson()
        {
            var snapshot = new DeveloperToolSettingsSnapshot
            {
                Enabled = ResponsesState.UseDeveloperTools,
                RepositoryRoot = ResponsesState.DeveloperRepositoryRoot ?? string.Empty,
                Scope = string.IsNullOrWhiteSpace(ResponsesState.DeveloperScope)
                    ? "repository"
                    : ResponsesState.DeveloperScope,

                SearchProjectText = ResponsesState.DeveloperToolSearchProjectText,
                ReadProjectFile = ResponsesState.DeveloperToolReadProjectFile,
                ListProjectFiles = ResponsesState.DeveloperToolListProjectFiles,
                RunDiagnostics = ResponsesState.DeveloperToolRunDiagnostics,
                CreateDotNetSolution = ResponsesState.DeveloperToolCreateDotNetSolution,
                BuildDotNetProject = ResponsesState.DeveloperToolBuildDotNetProject,
                RunDotNetProject = ResponsesState.DeveloperToolRunDotNetProject,
                WriteProjectFile = ResponsesState.DeveloperToolWriteProjectFile,
                ReplaceInProjectFile = ResponsesState.DeveloperToolReplaceInProjectFile,

                ReadOnlyOnly = ResponsesState.DeveloperAllowReadOnlyOnly,
                RequireConfirmation = ResponsesState.DeveloperRequireConfirmation,
                RequireWriteConfirmation = ResponsesState.DeveloperRequireWriteConfirmation,
                RequireExecutionConfirmation = ResponsesState.DeveloperRequireExecutionConfirmation,
                ShowToolLogs = ResponsesState.DeveloperShowToolLogs,

                AllowedExtensionsCsv = string.IsNullOrWhiteSpace(ResponsesState.DeveloperAllowedExtensionsCsv)
                    ? ResponsesPanelState.GetDefaultAllowedExtensionsCsv()
                    : ResponsesState.DeveloperAllowedExtensionsCsv
            };

            return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }
        private void ApplyImageToolSettingsFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                var snapshot = JsonSerializer.Deserialize<ImageToolSettingsSnapshot>(json);
                if (snapshot == null)
                    return;

                ResponsesState.ImageGenQuality =
                    string.IsNullOrWhiteSpace(snapshot.Quality) ? "auto" : snapshot.Quality;

                ResponsesState.ImageGenSize =
                    string.IsNullOrWhiteSpace(snapshot.Size) ? "auto" : snapshot.Size;

                ResponsesState.ImageGenOutputFormat =
                    string.IsNullOrWhiteSpace(snapshot.OutputFormat) ? "jpeg" : snapshot.OutputFormat;

                ResponsesState.ImageGenBackground =
                    string.IsNullOrWhiteSpace(snapshot.Background) ? "auto" : snapshot.Background;

                if (snapshot.OutputCompression.HasValue)
                    ResponsesState.ImageGenOutputCompression = snapshot.OutputCompression.Value;
            }
            catch
            {
                // Keep tolerant; old rows may not have valid JSON
            }
        }
        private void ApplyDeveloperToolSettingsFromJson(string json)
        {
            // Always reset first so opening a session without developer-tool settings
            // does not inherit stale UI state from the previously opened session.
            ResetDeveloperToolsState();

            if (string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                var snapshot = JsonSerializer.Deserialize<DeveloperToolSettingsSnapshot>(json);
                if (snapshot == null)
                    return;

                ResponsesState.UseDeveloperTools = snapshot.Enabled;
                ResponsesState.DeveloperRepositoryRoot = snapshot.RepositoryRoot ?? string.Empty;
                ResponsesState.DeveloperScope = string.IsNullOrWhiteSpace(snapshot.Scope)
                    ? "repository"
                    : snapshot.Scope;

                ResponsesState.DeveloperToolSearchProjectText = snapshot.SearchProjectText;
                ResponsesState.DeveloperToolReadProjectFile = snapshot.ReadProjectFile;
                ResponsesState.DeveloperToolListProjectFiles = snapshot.ListProjectFiles;
                ResponsesState.DeveloperToolRunDiagnostics = snapshot.RunDiagnostics;
                ResponsesState.DeveloperToolCreateDotNetSolution = snapshot.CreateDotNetSolution;
                ResponsesState.DeveloperToolBuildDotNetProject = snapshot.BuildDotNetProject;
                ResponsesState.DeveloperToolRunDotNetProject = snapshot.RunDotNetProject;
                ResponsesState.DeveloperToolWriteProjectFile = snapshot.WriteProjectFile;
                ResponsesState.DeveloperToolReplaceInProjectFile = snapshot.ReplaceInProjectFile;

                ResponsesState.DeveloperAllowReadOnlyOnly = snapshot.ReadOnlyOnly;
                ResponsesState.DeveloperRequireConfirmation = snapshot.RequireConfirmation;
                ResponsesState.DeveloperRequireWriteConfirmation = snapshot.RequireWriteConfirmation;
                ResponsesState.DeveloperRequireExecutionConfirmation = snapshot.RequireExecutionConfirmation;
                ResponsesState.DeveloperShowToolLogs = snapshot.ShowToolLogs;
                ResponsesState.DeveloperAllowedExtensionsCsv =
                    string.IsNullOrWhiteSpace(snapshot.AllowedExtensionsCsv)
                        ? ResponsesPanelState.GetDefaultAllowedExtensionsCsv()
                        : snapshot.AllowedExtensionsCsv;
            }
            catch
            {
                // Keep tolerant for older rows or malformed JSON
            }
        }
        private void ApplyResponsesToolsToState(string toolsCsv)
        {
            var tools = (toolsCsv ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (tools.Count == 0)
                tools.Add(ResponseToolKeys.Text);

            if (tools.Count > 1 && tools.Contains(ResponseToolKeys.Text))
                tools.Remove(ResponseToolKeys.Text);

            ResponsesState.UseTextTool = tools.Contains(ResponseToolKeys.Text);
            ResponsesState.UseWebSearch = tools.Contains(ResponseToolKeys.WebSearch);
            ResponsesState.UseImageGeneration = tools.Contains(ResponseToolKeys.ImageGeneration);
        }
        private void ValidateImageGenerationSettings()
        {
            if (ResponsesState.ImageGenOutputCompression < 0)
                ResponsesState.ImageGenOutputCompression = 0;

            if (ResponsesState.ImageGenOutputCompression > 100)
                ResponsesState.ImageGenOutputCompression = 100;

            if (!ResponsesState.UseImageGeneration)
                return;

            bool compressionAllowed =
                string.Equals(ResponsesState.ImageGenOutputFormat, "jpeg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ResponsesState.ImageGenOutputFormat, "webp", StringComparison.OrdinalIgnoreCase);

            bool transparentAllowed =
                string.Equals(ResponsesState.ImageGenOutputFormat, "png", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ResponsesState.ImageGenOutputFormat, "webp", StringComparison.OrdinalIgnoreCase);

            if (!compressionAllowed)
            {
                // Keep a default value stored, but it won't be sent.
                if (ResponsesState.ImageGenOutputCompression < 0 || ResponsesState.ImageGenOutputCompression > 100)
                    ResponsesState.ImageGenOutputCompression = 85;
            }

            if (!transparentAllowed &&
                string.Equals(ResponsesState.ImageGenBackground, "transparent", StringComparison.OrdinalIgnoreCase))
            {
                ResponsesState.ImageGenBackground = "auto";
                _appStatus.Set($"Transparent background is not supported with {ResponsesState.ImageGenOutputFormat}.");
            }

            if (string.IsNullOrWhiteSpace(ResponsesState.ImageGenOutputFormat))
                ResponsesState.ImageGenOutputFormat = "jpeg";

            if (string.IsNullOrWhiteSpace(ResponsesState.ImageGenBackground))
                ResponsesState.ImageGenBackground = "auto";

        }

        private async void btnResponsesAttachFiles_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select files for this message",
                AllowMultiple = true,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(savepath_images),
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Common Files") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp", "*.pdf", "*.txt", "*.md", "*.csv", "*.json", "*.xml", "*.cs", "*.docx", "*.xlsx" } },
                    new FilePickerFileType("Image Files") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp" } },
                    new FilePickerFileType("Document Files") { Patterns = new[] { "*.pdf", "*.txt", "*.md", "*.csv", "*.json", "*.xml", "*.docx", "*.xlsx", "*.cs", "*.css", "*.js", "*.html" } },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
                }
            });

            AddPendingResponseAttachments(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        }

        private void btnResponsesClearAttachments_Click(object sender, RoutedEventArgs e)
        {
            ClearPendingResponseAttachments();
        }

        private void btnResponsesRemovePendingAttachment_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is not ResponseAttachmentItem item)
                return;

            PendingResponseAttachments.Remove(item);
            UpdatePendingAttachmentsPanel();

            if (string.Equals(_responsesImagePath, item.LocalPath, StringComparison.OrdinalIgnoreCase))
            {
                var nextImage = PendingResponseAttachments.FirstOrDefault(a => a.IsImage && File.Exists(a.LocalPath));
                if (nextImage != null)
                    ShowResponsesImagePreview(nextImage.LocalPath);
                else
                    HideResponsesImagePreview();
            }
            else if (PendingResponseAttachments.Count == 0)
            {
                HideResponsesImagePreview();
            }
        }

        private async void btnResponsesOpenPendingAttachment_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is not ResponseAttachmentItem item)
                return;

            await OpenLocalFileAsync(item.LocalPath, "attachment");
        }

        private void lstResponsesPendingAttachments_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstResponsesPendingAttachments.SelectedItem is not ResponseAttachmentItem item)
                return;

            if (item.IsImage && File.Exists(item.LocalPath))
                ShowResponsesImagePreview(item.LocalPath);
        }
        private void cbDeveloperTools_Checked(object sender, RoutedEventArgs e)
        {
            RefreshEnvironmentCapabilityReport();
        }

        private void txtDeveloperWorkspaceRoot_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ResponsesState?.UseDeveloperTools == true)
                RefreshEnvironmentCapabilityReport();
        }

        private async void btnDeveloperEnvironmentReport_Click(object? sender, RoutedEventArgs e)
        {
            RefreshEnvironmentCapabilityReport();
            var reportWindow = new EnvironmentReportWindow(_environmentCapabilityReport);
            await reportWindow.ShowDialog(this);
        }

        private void RefreshEnvironmentCapabilityReport()
        {
            _environmentCapabilityReport = new EnvironmentCapabilityReportService()
                .CreateReport(ResponsesState?.DeveloperRepositoryRoot);
        }

        private void btnDeveloperStopAllProcesses_Click(object sender, RoutedEventArgs e)
        {
            int stopped = new DeveloperProcessManager().StopAllRunningProcesses();
            _appStatus.Set(stopped == 1
                ? "Stopped 1 Developer Tools-launched application."
                : $"Stopped {stopped} Developer Tools-launched applications.");
        }

        private async void btnDeveloperBrowseRoot_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select workspace root",
                AllowMultiple = false
            });

            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folderPath)
                ResponsesState.DeveloperRepositoryRoot = folderPath;
        }
        private DeveloperToolsOptions BuildDeveloperToolsOptionsFromState()
        {
            var extensions = (ResponsesState.DeveloperAllowedExtensionsCsv ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            return new DeveloperToolsOptions
            {
                Enabled = ResponsesState.UseDeveloperTools,
                RepositoryRoot = ResponsesState.DeveloperRepositoryRoot ?? string.Empty,
                ScopeMode = ResponsesState.DeveloperScope ?? "repository",

                ReadOnlyOnly = ResponsesState.DeveloperAllowReadOnlyOnly,
                RequireConfirmation = ResponsesState.DeveloperRequireConfirmation,
                RequireWriteConfirmation = ResponsesState.DeveloperRequireWriteConfirmation,
                RequireExecutionConfirmation = ResponsesState.DeveloperRequireExecutionConfirmation,
                ShowToolLogs = ResponsesState.DeveloperShowToolLogs,

                SearchProjectTextEnabled = ResponsesState.DeveloperToolSearchProjectText,
                ReadProjectFileEnabled = ResponsesState.DeveloperToolReadProjectFile,
                ListProjectFilesEnabled = ResponsesState.DeveloperToolListProjectFiles,
                RunDiagnosticsEnabled = ResponsesState.DeveloperToolRunDiagnostics,
                CreateDotNetSolutionEnabled = ResponsesState.DeveloperToolCreateDotNetSolution,
                BuildDotNetProjectEnabled = ResponsesState.DeveloperToolBuildDotNetProject,
                RunDotNetProjectEnabled = ResponsesState.DeveloperToolRunDotNetProject,

                WriteProjectFileEnabled = ResponsesState.DeveloperToolWriteProjectFile,
                ReplaceInProjectFileEnabled = ResponsesState.DeveloperToolReplaceInProjectFile,

                AllowedExtensions = extensions,
                MaxReadLines = 300,
                MaxSearchResults = 100,
                MaxFileBytes = 512 * 1024,
                MaxWriteFileBytes = 512 * 1024
            };
        }
        private async Task<bool> ConfirmDeveloperToolCallAsync(string toolName, string argumentsJson)
        {
            bool isWriteTool =
                string.Equals(toolName, "write_project_file", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolName, "replace_in_project_file", StringComparison.OrdinalIgnoreCase);

            if (isWriteTool)
                return await ShowPendingToolReviewAsync(toolName, argumentsJson);

            string message =
                $"Allow local tool call?\n\n" +
                $"Tool: {toolName}\n\n" +
                $"Arguments:\n{argumentsJson}";

            return await _dialogs.ConfirmAsync(
                this,
                "Confirm local developer tool call",
                message);
        }

        private async Task<bool> ShowPendingToolReviewAsync(string toolName, string argumentsJson)
        {
            var review = await BuildPendingToolReviewAsync(toolName, argumentsJson);
            _pendingToolReview = review;

            ResponsesState.DeveloperPendingReviewTitle = review.Title;
            ResponsesState.DeveloperPendingReviewToolName = review.ToolName;
            ResponsesState.DeveloperPendingReviewSummary = review.Summary;
            ResponsesState.DeveloperPendingReviewVisible = true;

            _pendingToolReviewTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await RenderResponsesMarkdownAsync(review.MarkdownPreview);

            return await _pendingToolReviewTcs.Task;
        }

        private Task<PendingToolReview> BuildPendingToolReviewAsync(string toolName, string argumentsJson)
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            JsonElement root = doc.RootElement;

            PendingToolReview review = toolName switch
            {
                "write_project_file" => BuildWriteProjectFileReview(root, argumentsJson),
                "replace_in_project_file" => BuildReplaceInProjectFileReview(root, argumentsJson),
                _ => new PendingToolReview
                {
                    ToolName = toolName ?? string.Empty,
                    ArgumentsJson = argumentsJson ?? string.Empty,
                    IsWriteTool = true,
                    Title = "Pending write review",
                    Summary = "Unknown write-capable tool.",
                    MarkdownPreview = "# Pending write review\n\nUnknown write-capable tool."
                }
            };

            return Task.FromResult(review);
        }

        private PendingToolReview BuildWriteProjectFileReview(JsonElement root, string argumentsJson)
        {
            string path = GetRequiredString(root, "path");
            string newContent = GetOptionalString(root, "content") ?? string.Empty;
            string fullPath = ResolveToolReviewFullPath(path);
            bool fileExists = File.Exists(fullPath);
            string oldContent = fileExists ? File.ReadAllText(fullPath) : string.Empty;
            string language = GetCodeFenceLanguageFromPath(path);

            bool isCreate = !fileExists;
            bool isOverwrite = fileExists;

            string summary = isCreate
                ? $"Create 1 file: {path}"
                : $"Overwrite 1 existing file: {path}";

            string markdown = isCreate
                ? $"""
# Pending write review

The assistant wants to create a file inside the current repository.

## File
`{path}`

- **Tool:** `write_project_file`
- **Operation:** create file

## Proposed content

```{language}
{newContent}
```
"""
                : $"""
# Pending write review

The assistant wants to overwrite an existing file.

## File
`{path}`

- **Tool:** `write_project_file`
- **Operation:** overwrite file

## Current content

```{language}
{oldContent}
```

## New content

```{language}
{newContent}
```
""";

            return new PendingToolReview
            {
                ToolName = "write_project_file",
                ArgumentsJson = argumentsJson ?? string.Empty,
                IsWriteTool = true,
                Title = "Pending write review",
                Summary = summary,
                MarkdownPreview = markdown,
                Changes =
                {
                    new PendingFileChange
                    {
                        Path = path,
                        Operation = isCreate ? "create" : "overwrite",
                        FileExists = fileExists,
                        IsCreate = isCreate,
                        IsOverwrite = isOverwrite,
                        OldContent = oldContent,
                        NewContent = newContent
                    }
                }
            };
        }

        private PendingToolReview BuildReplaceInProjectFileReview(JsonElement root, string argumentsJson)
        {
            string path = GetRequiredString(root, "path");
            string find = GetRequiredString(root, "find");
            string replace = GetOptionalString(root, "replace") ?? string.Empty;
            bool replaceAll = GetOptionalBool(root, "replace_all") == true;
            int? expectedMatchCount = GetOptionalInt(root, "expected_match_count");

            string fullPath = ResolveToolReviewFullPath(path);
            string oldContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty;
            string newContent = replaceAll
                ? oldContent.Replace(find, replace)
                : ReplaceFirst(oldContent, find, replace);

            string language = GetCodeFenceLanguageFromPath(path);

            string expectedMatchesText = expectedMatchCount.HasValue
                ? expectedMatchCount.Value.ToString()
                : "(not specified)";

            string markdown = $"""
# Pending write review

The assistant wants to replace text in an existing file.

## File
`{path}`

- **Tool:** `replace_in_project_file`
- **Operation:** replace text
- **Replace all:** `{replaceAll}`
- **Expected matches:** `{expectedMatchesText}`

## Find

```{language}
{find}
```

## Replace with

```{language}
{replace}
```

## Current content

```{language}
{oldContent}
```

## Resulting content

```{language}
{newContent}
```
""";

            return new PendingToolReview
            {
                ToolName = "replace_in_project_file",
                ArgumentsJson = argumentsJson ?? string.Empty,
                IsWriteTool = true,
                Title = "Pending write review",
                Summary = $"Modify 1 existing file: {path}",
                MarkdownPreview = markdown,
                Changes =
                {
                    new PendingFileChange
                    {
                        Path = path,
                        Operation = "replace",
                        FileExists = File.Exists(fullPath),
                        IsCreate = false,
                        IsOverwrite = true,
                        OldContent = oldContent,
                        NewContent = newContent
                    }
                }
            };
        }

        private string ResolveToolReviewFullPath(string relativePath)
        {
            string repositoryRoot = ResponsesState.DeveloperRepositoryRoot ?? string.Empty;
            if (string.IsNullOrWhiteSpace(repositoryRoot))
                throw new InvalidOperationException("Developer workspace root is not configured.");

            if (Path.IsPathRooted(relativePath))
                throw new InvalidOperationException("Tool review path must be relative.");

            string fullRepositoryRoot = Path.GetFullPath(repositoryRoot);
            string combined = Path.GetFullPath(Path.Combine(fullRepositoryRoot, relativePath));

            if (!combined.StartsWith(fullRepositoryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Tool review path escapes workspace root.");

            return combined;
        }

        private static string GetRequiredString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"Missing required string property '{propertyName}'.");

            return value.GetString() ?? string.Empty;
        }

        private static string GetOptionalString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value))
                return null;

            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static bool? GetOptionalBool(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value))
                return null;

            return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False
                ? value.GetBoolean()
                : null;
        }

        private static int? GetOptionalInt(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value))
                return null;

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int intValue)
                ? intValue
                : null;
        }

        private static string ReplaceFirst(string input, string find, string replace)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(find))
                return input ?? string.Empty;

            int index = input.IndexOf(find, StringComparison.Ordinal);
            if (index < 0)
                return input;

            return input.Remove(index, find.Length).Insert(index, replace ?? string.Empty);
        }

        private static string GetCodeFenceLanguageFromPath(string path)
        {
            string extension = Path.GetExtension(path) ?? string.Empty;

            return extension.ToLowerInvariant() switch
            {
                ".cs" => "csharp",
                ".xaml" => "xml",
                ".xml" => "xml",
                ".json" => "json",
                ".js" => "javascript",
                ".css" => "css",
                ".html" => "html",
                ".md" => "markdown",
                ".sql" => "sql",
                ".ps1" => "powershell",
                _ => string.Empty
            };
        }

        private void ApprovePendingToolReview_Click(object sender, RoutedEventArgs e)
        {
            ResponsesState.DeveloperPendingReviewVisible = false;
            ResponsesState.DeveloperPendingReviewTitle = string.Empty;
            ResponsesState.DeveloperPendingReviewToolName = string.Empty;
            ResponsesState.DeveloperPendingReviewSummary = string.Empty;

            _pendingToolReviewTcs?.TrySetResult(true);
            _pendingToolReviewTcs = null;
            _pendingToolReview = null;
        }

        private void RejectPendingToolReview_Click(object sender, RoutedEventArgs e)
        {
            ResponsesState.DeveloperPendingReviewVisible = false;
            ResponsesState.DeveloperPendingReviewTitle = string.Empty;
            ResponsesState.DeveloperPendingReviewToolName = string.Empty;
            ResponsesState.DeveloperPendingReviewSummary = string.Empty;

            _pendingToolReviewTcs?.TrySetResult(false);
            _pendingToolReviewTcs = null;
            _pendingToolReview = null;
        }

        private async Task UpdateDeveloperWorkspaceRootAsync(string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                throw new InvalidOperationException("The created workspace root does not exist.");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ResponsesState.DeveloperRepositoryRoot = Path.GetFullPath(workspaceRoot);
                _appStatus.Set("Workspace root switched to the created solution folder.");
            });
        }

        private async Task LogDeveloperToolCallAsync(string toolName, string argumentsJson, string resultJson)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _appStatus.Set($"Local tool used: {toolName}");
            });

            Debug.WriteLine($"[LOCAL TOOL] {toolName}");
            Debug.WriteLine(argumentsJson);
            Debug.WriteLine(resultJson);

            await AddDeveloperToolCallLogAsync(toolName, argumentsJson, resultJson);
        }
        private async Task AddDeveloperToolCallLogAsync(string toolName, string argumentsJson, string resultJson)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DeveloperToolCallLogs.Add(new DeveloperToolCallLogItem
                {
                    Timestamp = DateTime.Now,
                    ToolName = toolName ?? string.Empty,
                    WorkspaceRoot = ResponsesState.DeveloperRepositoryRoot ?? string.Empty,
                    ArgumentsJson = argumentsJson ?? string.Empty,
                    ResultJson = resultJson ?? string.Empty
                });
            });
        }

        private void ClearDeveloperToolCallLogs()
        {
            DeveloperToolCallLogs.Clear();
        }
        private string BuildDeveloperToolCallLogJson()
        {
            if (DeveloperToolCallLogs.Count == 0)
                return string.Empty;

            return JsonSerializer.Serialize(DeveloperToolCallLogs);
        }
        private void ApplyDeveloperToolCallLogJson(string json)
        {
            DeveloperToolCallLogs.Clear();

            if (string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                var items = JsonSerializer.Deserialize<List<DeveloperToolCallLogItem>>(json);
                if (items == null)
                    return;

                foreach (var item in items)
                {
                    DeveloperToolCallLogs.Add(item);
                }
            }
            catch
            {
                // tolerate older rows / malformed JSON
            }
        }
        /// <summary>
        /// Represents a selectable Markdown theme option with associated display and file information.
        /// </summary>
        /// <remarks>This class is typically used to provide theme choices in a Markdown rendering or
        /// editing context. Instances are immutable and intended for use as value objects in UI or configuration
        /// scenarios.</remarks>
        private sealed class MarkdownThemeOption
        {
            public string FileName { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public string RelativeHref { get; init; } = string.Empty;

            public override string ToString() => DisplayName;
        }
        private sealed class PageThemeOption
        {
            public string FileName { get; init; } = string.Empty;
            public string DisplayName { get; init; } = string.Empty;
            public string RelativeHref { get; init; } = string.Empty;

            public override string ToString() => DisplayName;
        }
        private string GetMarkdownViewerAssetsOutputPath()
        {
            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Assets",
                "MarkdownViewer");
        }

        private List<MarkdownThemeOption> LoadMarkdownThemeOptions()
        {
            string stylesPath = System.IO.Path.Combine(GetMarkdownViewerAssetsOutputPath(), "Styles");

            if (!Directory.Exists(stylesPath))
                return new List<MarkdownThemeOption>();

            var files = Directory.GetFiles(stylesPath, "*.css", SearchOption.TopDirectoryOnly);

            return files
                .Select(path =>
                {
                    string fileName = Path.GetFileName(path);

                    return new MarkdownThemeOption
                    {
                        FileName = fileName,
                        DisplayName = FormatMarkdownThemeDisplayName(fileName),
                        RelativeHref = "Styles/" + fileName
                    };
                })
                .OrderBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private string FormatMarkdownThemeDisplayName(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName);

            if (name.EndsWith(".min", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            name = name.Replace("-", " ");

            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name);
        }

        private MarkdownThemeOption GetDefaultMarkdownThemeOption(List<MarkdownThemeOption> options)
        {
            var github = options.FirstOrDefault(t =>
                t.FileName.Equals("github.css", StringComparison.OrdinalIgnoreCase));

            return github ?? options.FirstOrDefault();
        }
        /// Markdown pipeline for rendering response text (used for copy as markdown)
        private readonly MarkdownPipeline _responsesMarkdownPipeline =
                                        new MarkdownPipelineBuilder()
                                            .UseAdvancedExtensions()
                                            .Build();

        private async Task EnsureResponsesWebViewInitializedAsync()
        {
            if (_responsesWebViewInitialized)
                return;

            wvResponsesResponse.NavigationStarted += ResponsesWebView_NavigationStarted;
            wvResponsesResponse.NewWindowRequested += ResponsesWebView_NewWindowRequested;
            wvResponsesResponse.WebMessageReceived += ResponsesWebView_WebMessageReceived;

            _responsesWebViewInitialized = true;

            _markdownThemeOptions = LoadMarkdownThemeOptions();
            BindMarkdownThemeOptions();

            _pageThemeOptions = LoadPageThemeOptions();
            BindPageThemeOptions();

            await EnsureResponsesViewerPageLoadedAsync();
        }
        private string ConvertMarkdownToHtmlBody(string markdown)
        {
            markdown ??= string.Empty;
            return Markdig.Markdown.ToHtml(markdown, _responsesMarkdownPipeline);
        }

        private void BindMarkdownThemeOptions()
        {
            _settings ??= AppSettings.LoadSettings();

            _bindingMarkdownThemeOptions = true;
            try
            {
                cmbResponsesMarkdownTheme.ItemsSource = _markdownThemeOptions;

                string savedTheme = _settings.ResponsesMarkdownTheme;

                _selectedMarkdownTheme =
                    _markdownThemeOptions.FirstOrDefault(t =>
                        t.FileName.Equals(savedTheme, StringComparison.OrdinalIgnoreCase))
                    ?? GetDefaultMarkdownThemeOption(_markdownThemeOptions);

                if (_selectedMarkdownTheme != null)
                    cmbResponsesMarkdownTheme.SelectedItem = _selectedMarkdownTheme;
            }
            finally
            {
                _bindingMarkdownThemeOptions = false;
            }
        }
        private async Task EnsureResponsesViewerPageLoadedAsync()
        {
            if (_responsesViewerPageLoaded)
            {
                if (_responsesViewerPageLoadTask is { IsCompleted: false } ||
                    await IsResponsesViewerScriptReadyAsync())
                {
                    return;
                }

                _responsesViewerPageLoaded = false;
                if (_responsesViewerPageLoadTask?.IsCompleted == true)
                    _responsesViewerPageLoadTask = null;
            }

            Task loadTask = _responsesViewerPageLoadTask ??= LoadResponsesViewerPageAsync();

            try
            {
                await loadTask;
            }
            catch
            {
                if (ReferenceEquals(_responsesViewerPageLoadTask, loadTask))
                    _responsesViewerPageLoadTask = null;
                throw;
            }
        }

        private async Task LoadResponsesViewerPageAsync()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(object? sender, WebViewNavigationCompletedEventArgs e)
            {
                wvResponsesResponse.NavigationCompleted -= Handler;
                tcs.TrySetResult(e.IsSuccess);
            }

            wvResponsesResponse.NavigationCompleted += Handler;
            string templatePath = Path.Combine(GetMarkdownViewerAssetsOutputPath(), "template.html");
            if (!File.Exists(templatePath))
            {
                wvResponsesResponse.NavigationCompleted -= Handler;
                throw new FileNotFoundException("Markdown viewer template was not found.", templatePath);
            }

            _responsesAssetServer ??= MarkdownViewerAssetServer.Start(GetMarkdownViewerAssetsOutputPath());
            wvResponsesResponse.Navigate(_responsesAssetServer.TemplateUri);

            if (!await tcs.Task)
                throw new InvalidOperationException("The Markdown viewer page could not be loaded.");

            await WaitForResponsesViewerScriptAsync();

            await InvokeResponsesScriptWithStringResultAsync(
                "window.chrome = window.chrome || {}; window.chrome.webview = window.chrome.webview || {}; window.chrome.webview.postMessage = window.invokeCSharpAction;");

            _responsesViewerPageLoaded = true;
            await ApplySelectedPageThemeAsync();
            await ApplySelectedMarkdownThemeAsync();
        }

        private async Task WaitForResponsesViewerScriptAsync()
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                if (await IsResponsesViewerScriptReadyAsync())
                    return;

                await Task.Delay(100);
            }

            throw new InvalidOperationException("The Markdown viewer script did not initialize after navigation completed.");
        }

        private async Task<bool> IsResponsesViewerScriptReadyAsync()
        {
            try
            {
                string? result = await wvResponsesResponse.InvokeScript("Boolean(window.markdownViewer && typeof window.markdownViewer.setContent === 'function' && typeof window.markdownViewer.setPageTheme === 'function' && typeof window.markdownViewer.setHighlightTheme === 'function') ? 1 : 0;");
                return string.Equals(result?.Trim().Trim('"'), "1", StringComparison.Ordinal);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("Unable to invoke script before any page was loaded", StringComparison.Ordinal))
            {
                return false;
            }
        }

        private async void MainWindow_Closed(object? sender, EventArgs e)
        {
            if (_responsesAssetServer is not null)
            {
                await _responsesAssetServer.DisposeAsync();
                _responsesAssetServer = null;
            }
        }
        private void ResponsesWebView_EnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
        {
            e.EnableDevTools = true;
        }
        private void ResponsesWebView_NavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
        {
            Uri? uri = e.Request;
            if (uri is null)
                return;

            bool isResponsesAsset = _responsesAssetServer?.BaseUri.IsBaseOf(uri) == true;
            bool isExternalLink =
                !isResponsesAsset &&
                (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase));

            if (!isExternalLink)
            {
                // The loaded flag describes the current WebView document, not just
                // whether the viewer was loaded once. A later in-app navigation
                // invalidates it so the next render waits for a usable page.
                _responsesViewerPageLoaded = false;
                if (_responsesViewerPageLoadTask?.IsCompleted == true)
                    _responsesViewerPageLoadTask = null;
                return;
            }

            e.Cancel = true;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }
        private void ResponsesWebView_NewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            OpenExternalUri(e.Request);
        }

        private void OpenExternalUri(Uri? uri)
        {
            if (uri is null ||
                !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                  uri.Scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private async void ResponsesWebView_WebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.Body;
                if (string.IsNullOrWhiteSpace(message))
                    return;
                System.Diagnostics.Debug.WriteLine($"WebView message: {message}");
                const string prefix = "copy-code:";
                if (!message.StartsWith(prefix, StringComparison.Ordinal))
                    return;

                string code = message.Substring(prefix.Length).TrimEnd();

                if (string.IsNullOrEmpty(code))
                    return;

                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                    await ClipboardExtensions.SetTextAsync(clipboard, code);
            }
            catch
            {
                // Optional: log or show status message
            }
        }

        private Task<string?> InvokeResponsesScriptWithStringResultAsync(string script)
        {
            return wvResponsesResponse.InvokeScript($"{script}\n; 'ok';");
        }

        private async Task InvokeResponsesScriptAsync(string script)
        {
            await EnsureResponsesViewerPageLoadedAsync();

            try
            {
                await InvokeResponsesScriptWithStringResultAsync(script);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("Unable to invoke script before any page was loaded", StringComparison.Ordinal))
            {
                // The WebView can lose its document while the cached loaded flag is still true.
                // Invalidate that cache and synchronize against a fresh NavigationCompleted.
                _responsesViewerPageLoaded = false;
                if (_responsesViewerPageLoadTask?.IsCompleted == true)
                    _responsesViewerPageLoadTask = null;

                await EnsureResponsesViewerPageLoadedAsync();
                await InvokeResponsesScriptWithStringResultAsync(script);
            }
        }

        private async Task RenderResponsesMarkdownAsync(string markdown)
        {
            try
            {
                await EnsureResponsesWebViewInitializedAsync();
                await EnsureResponsesViewerPageLoadedAsync();

                string htmlBody = ConvertMarkdownToHtmlBody(markdown);
                string jsArgument = System.Text.Json.JsonSerializer.Serialize(htmlBody);

                await InvokeResponsesScriptAsync(
                    $"window.markdownViewer.setContent({jsArgument});");

                await ApplySelectedPageThemeAsync();
                await ApplySelectedMarkdownThemeAsync();
                // Scroll to top after rendering new content
                await InvokeResponsesScriptAsync("window.scrollTo(0, 0);");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to render response markdown: {ex}");
                _appStatus.Set("Response preview rendering failed.");
            }
        }
        private async Task ApplySelectedPageThemeAsync()
        {
            if (_selectedPageTheme == null)
                return;

            string hrefArgument = JsonSerializer.Serialize(_selectedPageTheme.RelativeHref);

            await InvokeResponsesScriptAsync(
                $"window.markdownViewer.setPageTheme({hrefArgument});");
        }
        private async Task ApplySelectedMarkdownThemeAsync()
        {
            if (_selectedMarkdownTheme == null)
                return;

            string hrefArgument = System.Text.Json.JsonSerializer.Serialize(_selectedMarkdownTheme.RelativeHref);

            await InvokeResponsesScriptAsync(
                $"window.markdownViewer.setHighlightTheme({hrefArgument});");
            string diagnostic = await wvResponsesResponse.InvokeScript("""
            JSON.stringify((() => {
                const link = document.getElementById("highlight-theme");
                const block = document.querySelector("#markdown-root pre > code");
                const token = block?.querySelector("span[class^='hljs-']");

                return {
                    href: link?.href ?? null,
                    sheetLoaded: !!link?.sheet,
                    codeBlockCount: document.querySelectorAll("#markdown-root pre > code").length,
                    highlighted: block?.classList.contains("hljs") ?? false,
                    tokenClass: token?.className ?? null,
                    tokenColor: token ? getComputedStyle(token).color : null
                };
            })())
            """);
            System.Diagnostics.Debug.WriteLine($"WebView highlight diagnostic: {diagnostic}");
            /// Refresh the syntax highlighting after changing the theme
            /// This was necessary because the highlight.js library does not automatically re-apply styles when the theme is changed.
            /// this was commented out, for some reason so we will re-enable it to see if it fixes the issue with the syntax highlighting not updating when changing themes.
            await InvokeResponsesScriptWithStringResultAsync(
                "window.markdownViewer.refreshHighlighting();");
        }
        private async void cmbResponsesMarkdownTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_bindingMarkdownThemeOptions)
                return;

            if (cmbResponsesMarkdownTheme.SelectedItem is not MarkdownThemeOption selected)
                return;

            _selectedMarkdownTheme = selected;

            _settings ??= AppSettings.LoadSettings();
            _settings.ResponsesMarkdownTheme = selected.FileName;
            AppSettings.SaveSettings(_settings);

            if (!_responsesViewerPageLoaded)
                return;

            await ApplySelectedMarkdownThemeAsync();
        }
        private async void cmbResponsesPageTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_bindingPageThemeOptions)
                return;

            if (cmbResponsesPageTheme.SelectedItem is not PageThemeOption selected)
                return;

            _selectedPageTheme = selected;

            _settings ??= AppSettings.LoadSettings();
            _settings.ResponsesPageTheme = selected.FileName;
            AppSettings.SaveSettings(_settings);

            if (!_responsesViewerPageLoaded)
                return;

            await ApplySelectedPageThemeAsync();
        }
        private List<PageThemeOption> LoadPageThemeOptions()
        {
            string themesPath = Path.Combine(GetMarkdownViewerAssetsOutputPath(), "page-themes");

            if (!Directory.Exists(themesPath))
                return new List<PageThemeOption>();

            var files = Directory.GetFiles(themesPath, "*.css", SearchOption.TopDirectoryOnly);

            return files
                .Select(path =>
                {
                    string fileName = Path.GetFileName(path);

                    return new PageThemeOption
                    {
                        FileName = fileName,
                        DisplayName = FormatThemeDisplayName(fileName),
                        RelativeHref = "page-themes/" + fileName
                    };
                })
                .OrderBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private string FormatThemeDisplayName(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName);

            if (name.EndsWith(".min", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            name = name.Replace("-", " ");
            name = name.Replace("_", " ");

            return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name);
        }

        private PageThemeOption GetDefaultPageThemeOption(List<PageThemeOption> options)
        {
            var preferred = options.FirstOrDefault(t =>
                t.FileName.Equals("github-light-page.css", StringComparison.OrdinalIgnoreCase));

            return preferred ?? options.FirstOrDefault();
        }
        private void BindPageThemeOptions()
        {
            _settings ??= AppSettings.LoadSettings();

            _bindingPageThemeOptions = true;
            try
            {
                cmbResponsesPageTheme.ItemsSource = _pageThemeOptions;

                string savedTheme = _settings.ResponsesPageTheme;

                _selectedPageTheme =
                    _pageThemeOptions.FirstOrDefault(t =>
                        t.FileName.Equals(savedTheme, StringComparison.OrdinalIgnoreCase))
                    ?? GetDefaultPageThemeOption(_pageThemeOptions);

                if (_selectedPageTheme != null)
                    cmbResponsesPageTheme.SelectedItem = _selectedPageTheme;
            }
            finally
            {
                _bindingPageThemeOptions = false;
            }
        }
    }
}

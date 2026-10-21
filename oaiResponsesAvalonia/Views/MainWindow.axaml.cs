using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using oaiResponsesAvalonia.Data;
using oaiResponsesAvalonia.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Views;

public partial class MainWindow : Window
{
    private readonly string OpenAPIKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
    private AppSettings _settings = null!;
    private string savepath_logs = string.Empty;
    private string savepath_images = string.Empty;
    private readonly IDialogService _dialogs = new AvaloniaDialogService();
    private AvailableModels? _availableModelsWindow;
    private int? _activeResponsesSessionId;
    private DispatcherTimer? _statusEllipsisTimer;
    private int _ellipsisCounter;
    private AppStatus _appStatus = null!;
    private Responses _responsesClient = null!;
    private bool _responsesTabNeedsRefresh;
    private string _responsesImagePath = string.Empty;
    private string _responsesPreviewImagePath = string.Empty;
    private LogRowViewModel? _lastSelectedLogRow;
    private readonly HistoryService _historyService;
    private readonly MediaStorageService _mediaStorageService;
    private readonly SessionCleanupService _sessionCleanupService;
    private DeveloperToolsActivityWindow? _developerToolsActivityWindow;

    public event Action<List<string>>? ModelsApplied;

    public static HttpResponseMessage GlobalhttpResponse = new();

    public ObservableCollection<ChatMessage> CurrentChatMessages { get; } = new();
    public ObservableCollection<LogRowViewModel> LogRows { get; } = new();
    public ObservableCollection<LogRowViewModel> LogView { get; } = new();
    public ObservableCollection<ResponseAttachmentItem> PendingResponseAttachments { get; } = new();
    public ObservableCollection<MediaFile> ResponsePreviewImages { get; } = new();
    public ObservableCollection<DeveloperToolCallLogItem> DeveloperToolCallLogs { get; } = new();
    public ObservableCollection<DeveloperToolActivityItem> DeveloperToolActivities { get; } = new();
    public LogsPanelState LogsState { get; } = new();
    public ResponsesPanelState ResponsesState { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => RefreshMcpSummary();
        Closed += MainWindow_Closed;
        _appStatus = new AppStatus(text => StatusText.Text = text ?? string.Empty);

        AppDbContext.InitializeDatabase();
        _mediaStorageService = new MediaStorageService();
        _historyService = new HistoryService(_mediaStorageService);
        _sessionCleanupService = new SessionCleanupService(_historyService, _mediaStorageService);
        _settings = AppSettings.LoadSettings();

        LogsState.PropertyChanged += LogsState_PropertyChanged;
        ResponsesState.PropertyChanged += ResponsesState_PropertyChanged;

        EnsureSavePaths();
        tabMain.SelectedItem = tpResponses;
    }

    private void OpenDeveloperActivityWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (_developerToolsActivityWindow is null)
        {
            _developerToolsActivityWindow = new DeveloperToolsActivityWindow(DeveloperToolActivities);
            _developerToolsActivityWindow.Closed += (_, _) => _developerToolsActivityWindow = null;
            _developerToolsActivityWindow.Show(this);
            return;
        }

        if (!_developerToolsActivityWindow.IsVisible)
            _developerToolsActivityWindow.Show(this);
        else
            _developerToolsActivityWindow.Activate();
    }

    private void InitStatusAnimation()
    {
        _statusEllipsisTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusEllipsisTimer.Tick += (_, _) =>
        {
            _ellipsisCounter = (_ellipsisCounter + 1) % 4;
            if (ResponsesState.IsRequestInProgress)
            {
                string baseText = StatusText.Text?.Split(new[] { '·', '.' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim()
                    ?? "Working";
                _appStatus.Set(baseText + new string('.', _ellipsisCounter));
            }
        };

        ResponsesState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ResponsesState.IsRequestInProgress))
                return;

            if (ResponsesState.IsRequestInProgress)
            {
                _ellipsisCounter = 0;
                _statusEllipsisTimer.Start();
            }
            else
            {
                _statusEllipsisTimer.Stop();
            }
        };
    }

    private async Task<int> EnsureSessionActiveAsync(EndpointType type, string firstPrompt)
    {
        if (_activeResponsesSessionId is null)
            _activeResponsesSessionId = await _historyService.StartNewSessionAsync(ExtractTitle(firstPrompt), type);

        return _activeResponsesSessionId.Value;
    }

    private static string ExtractTitle(string? prompt)
    {
        prompt = prompt?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
            return "Image prompt";

        return prompt.Length > 60 ? $"{prompt[..60]}..." : prompt;
    }

    private void EnsureSavePaths()
    {
        _settings ??= AppSettings.LoadSettings();
        savepath_logs = Path.Combine(_settings.AppRoot, _settings.LogsFolder);
        savepath_images = Path.Combine(_settings.AppRoot, _settings.ImagesFolder);
        Directory.CreateDirectory(savepath_logs);
        Directory.CreateDirectory(savepath_images);
        _mediaStorageService.SetImagesFolder(savepath_images);
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            InitStatusAnimation();
            await InitResponsesControlsAsync();
            await Task.Run(_mediaStorageService.MigrateLegacyMediaPaths);
            ApplyLogColumnVisibility();
            await LoadInitialLogsAsync();
            await EnsureResponsesWebViewInitializedAsync();
            await EnsureResponsesViewerPageLoadedAsync();
        }
        catch (Exception ex)
        {
            _appStatus.Set("Initialization failed.");
            await _dialogs.ShowMessageAsync(this, "Initialization error", ex.Message, DialogSeverity.Error);
        }
    }

    private async Task LoadInitialLogsAsync()
    {
        var sessions = await _historyService.GetAllSessionsForLogsAsync();
        ReplaceLogRows(sessions.Select(BuildLogRow));
    }

    private void LogsState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogsPanelState.SelectedLogRow))
        {
            if (LogsState.SelectedLogRow is { } selectedRow)
                _lastSelectedLogRow = selectedRow;
            else
                RestoreLastSelectedLogRow();
        }

        if (e.PropertyName == nameof(LogsPanelState.SearchText) || e.PropertyName == nameof(LogsPanelState.TypeFilter))
            ApplyFilters();

        if (e.PropertyName == nameof(LogsPanelState.ShowTurns) ||
            e.PropertyName == nameof(LogsPanelState.ShowMedia) ||
            e.PropertyName == nameof(LogsPanelState.ShowTools) ||
            e.PropertyName == nameof(LogsPanelState.ShowModel) ||
            e.PropertyName == nameof(LogsPanelState.ShowDev))
            ApplyLogColumnVisibility();
    }

    private void cbLogTypeFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (cbLogTypeFilter.SelectedItem is ComboBoxItem item && item.Tag is string filter)
            LogsState.TypeFilter = filter;
    }

    private void ResponsesState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResponsesPanelState.UseDeveloperTools))
        {
            RefreshEnvironmentCapabilityReport();
            return;
        }

        if (_responsesClient is null || _isApplyingResponsesSettings)
            return;

        switch (e.PropertyName)
        {
            case nameof(ResponsesPanelState.SelectedModel):
            case nameof(ResponsesPanelState.SelectedReasoning):
            case nameof(ResponsesPanelState.SearchContextSize):
            case nameof(ResponsesPanelState.ImageGenQuality):
            case nameof(ResponsesPanelState.ImageGenSize):
            case nameof(ResponsesPanelState.ImageGenOutputFormat):
            case nameof(ResponsesPanelState.ImageGenOutputCompression):
            case nameof(ResponsesPanelState.ImageGenBackground):
            case nameof(ResponsesPanelState.UseTextTool):
            case nameof(ResponsesPanelState.UseWebSearch):
            case nameof(ResponsesPanelState.UseImageGeneration):
                ValidateResponsesState();
                NormalizeResponsesToolsState();
                ValidateImageGenerationSettings();
                ApplyResponsesStateToClient();
                break;
        }
    }

    private void ReplaceLogRows(IEnumerable<LogRowViewModel> rows)
    {
        LogRows.Clear();
        foreach (var row in rows)
            LogRows.Add(row);
        ApplyFilters();
    }

    private static LogRowViewModel BuildLogRow(ChatSession session)
    {
        var messages = session.Messages ?? new List<ChatMessage>();
        var mediaFiles = messages.Where(m => m.MediaFiles is not null).SelectMany(m => m.MediaFiles).ToList();
        return new LogRowViewModel
        {
            SessionId = session.Id,
            Endpoint = session.Endpoint,
            Title = session.Title ?? string.Empty,
            CreatedAt = session.CreatedAt,
            LastUsedAt = session.LastUsedAt,
            Turns = messages.Count,
            Media = BuildMediaSummary(mediaFiles),
            Tools = BuildDistinctSummary(messages.Select(m => m.ActiveTools)),
            Model = BuildDistinctSummary(messages.Select(m => m.ModelUsed)),
            Dev = BuildDevSummary(messages),
            Session = session
        };
    }

    private static string BuildMediaSummary(IEnumerable<MediaFile> mediaFiles)
    {
        var files = mediaFiles?.ToList() ?? new List<MediaFile>();
        if (files.Count == 0)
            return "—";

        bool hasImage = files.Any(f => f.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true);
        return $"{(hasImage ? "Image" : "Media")} ({files.Count})";
    }

    private static string BuildDistinctSummary(IEnumerable<string?> values)
    {
        var items = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v!.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return items.Count == 0 ? "—" : string.Join(", ", items);
    }

    private void ApplyLogColumnVisibility()
    {
        if (dgUnifiedLogs is null || dgUnifiedLogs.Columns.Count < 9)
            return;

        dgUnifiedLogs.Columns[2].Width = new DataGridLength(LogsState.ShowTurns ? 65 : 0);
        dgUnifiedLogs.Columns[3].Width = new DataGridLength(LogsState.ShowMedia ? 100 : 0);
        dgUnifiedLogs.Columns[4].Width = new DataGridLength(LogsState.ShowTools ? 160 : 0);
        dgUnifiedLogs.Columns[5].Width = new DataGridLength(LogsState.ShowDev ? 55 : 0);
        dgUnifiedLogs.Columns[6].Width = new DataGridLength(LogsState.ShowModel ? 140 : 0);
    }

    private async void RefreshLogsTab()
    {
        var sessions = await _historyService.GetAllSessionsForLogsAsync();
        var rows = sessions.Select(BuildLogRow).ToList();
        if (!Dispatcher.UIThread.CheckAccess())
            await Dispatcher.UIThread.InvokeAsync(() => ReplaceLogRows(rows));
        else
            ReplaceLogRows(rows);
    }

    private void ApplyFilters()
    {
        var filtered = LogRows.Where(row =>
        {
            bool matchesType = LogsState.TypeFilter == "All" ||
                string.Equals(row.Endpoint.ToString(), LogsState.TypeFilter, StringComparison.OrdinalIgnoreCase);
            bool matchesText = string.IsNullOrWhiteSpace(LogsState.SearchText) ||
                row.Title.Contains(LogsState.SearchText, StringComparison.OrdinalIgnoreCase);
            return matchesType && matchesText;
        }).ToList();

        if (LogsState.SelectedLogRow is { } selectedRow && !filtered.Contains(selectedRow))
            LogsState.SelectedLogRow = null;
        if (_lastSelectedLogRow is not null && !filtered.Contains(_lastSelectedLogRow))
            _lastSelectedLogRow = null;

        LogView.Clear();
        foreach (var row in filtered)
            LogView.Add(row);
    }

    private async void OnLogEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (dgUnifiedLogs.SelectedItem is not LogRowViewModel { Session: { } session })
            return;

        await OpenSessionFromLogsAsync(session);
        _appStatus.Set($"Opened session '{session.Title}'");
    }

    private void tabMain_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, tabMain))
            return;

        if (tabMain.SelectedItem == tabLogs)
        {
            _responsesTabNeedsRefresh = true;
            RefreshLogsTab();
            return;
        }

        if (tabMain.SelectedItem == tpResponses && _responsesTabNeedsRefresh)
        {
            _responsesTabNeedsRefresh = false;
            Dispatcher.UIThread.Post(() => _ = RefreshSelectedResponsePreviewAsync());
        }
    }

    private async Task RefreshSelectedResponsePreviewAsync()
    {
        if (tabMain.SelectedItem != tpResponses)
            return;

        var selectedTurn = ResponsesState.SelectedTurn ?? lstResponsesTurns.SelectedItem as ChatMessage;
        string markdown = selectedTurn is not null &&
            string.Equals(selectedTurn.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? selectedTurn.Content
                : string.Empty;

        await RenderResponsesMarkdownAsync(markdown);
    }

    private async Task ClearDeletedSessionFromUi(ChatSession session)
    {
        if (session.Endpoint == EndpointType.Responses && _activeResponsesSessionId == session.Id)
        {
            _activeResponsesSessionId = null;
            await ResetResponsesUi(clearPrompt: true);
        }
    }

    private async void OnDeleteSessionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        var row = (button.CommandParameter ?? button.DataContext) as LogRowViewModel;
        var session = row?.Session;
        if (row is null || session is null)
            return;

        if (!await _dialogs.ConfirmAsync(this, "Confirm Delete", $"Permanently delete '{session.Title}'?"))
            return;

        await _sessionCleanupService.DeleteSessionAsync(session.Id);
        await ClearDeletedSessionFromUi(session);
        LogRows.Remove(row);
        ApplyFilters();
        if (ReferenceEquals(LogsState.SelectedLogRow, row))
            LogsState.SelectedLogRow = null;
        if (ReferenceEquals(_lastSelectedLogRow, row))
            _lastSelectedLogRow = null;
        _appStatus.Set($"Deleted session '{session.Title}'");
    }

    private void dgUnifiedLogs_LostFocus(object? sender, RoutedEventArgs e)
    {
        RestoreLastSelectedLogRow();
    }

    private void RestoreLastSelectedLogRow()
    {
        if (_lastSelectedLogRow is not { } selectedRow)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (LogsState.SelectedLogRow is null &&
                ReferenceEquals(_lastSelectedLogRow, selectedRow) &&
                LogView.Contains(selectedRow))
                dgUnifiedLogs.SelectedItem = selectedRow;
        });
    }

    private async Task OpenSessionFromLogsAsync(ChatSession session)
    {
        _activeResponsesSessionId = null;
        if (session.Endpoint != EndpointType.Responses)
            return;

        tabMain.SelectedItem = tpResponses;
        _activeResponsesSessionId = session.Id;
        await LoadResponsesSessionAsync(session.Id);
    }

    private void menuHelp_Click(object? sender, RoutedEventArgs e)
    {
    }

    private async void menuAbout_Click(object? sender, RoutedEventArgs e)
    {
        await _dialogs.ShowModalAsync(this, new About());
    }

    private void menuExit_Click(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown(0);
        else
            Close();
    }

    private async void menuAvailableModels_Click(object? sender, RoutedEventArgs e)
    {
        if (_availableModelsWindow?.IsVisible == true)
        {
            _availableModelsWindow.Activate();
            return;
        }

        List<ModelData> models;
        try
        {
            models = _allModelsFromApi.Count > 0
                ? _allModelsFromApi
                : await ModelApiService.GetAvailableModelsAsync(OpenAPIKey);
        }
        catch
        {
            models = GetHardcodedResponseModels()
                .Select(model => new ModelData { Id = model })
                .ToList();
        }

        _availableModelsWindow = new AvailableModels(models, _activeModelsForResponses);
        _availableModelsWindow.ModelsApplied += AvailableModelsWindow_ModelsApplied;
        _availableModelsWindow.Closed += AvailableModelsWindow_Closed;
        _availableModelsWindow.Show(this);
    }

    private async void menuMcpServers_Click(object? sender, RoutedEventArgs e)
    {
        await _dialogs.ShowModalForResultAsync(this, new McpServersWindow());
        RefreshMcpSummary();
    }

    private async void menuSettings_Click(object? sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings);
        if (await _dialogs.ShowModalForResultAsync(this, window) == true)
            EnsureSavePaths();
    }

    private void AvailableModelsWindow_ModelsApplied(List<string> models)
    {
        _activeModelsForResponses = models.ToList();
        ModelsApplied?.Invoke(_activeModelsForResponses);
        ResponsesState.SelectedModel = ApplyModelsToResponsesCombo(_activeModelsForResponses, "gpt-4o");
    }

    private void AvailableModelsWindow_Closed(object? sender, EventArgs e)
    {
        if (_availableModelsWindow is null)
            return;
        _availableModelsWindow.ModelsApplied -= AvailableModelsWindow_ModelsApplied;
        _availableModelsWindow.Closed -= AvailableModelsWindow_Closed;
        _availableModelsWindow = null;
    }

    public static string? ExtractFileName(string? path) =>
        string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);

    public static Bitmap GetImageSource(string filePath) => new(filePath);

    private async void OnExportLogMarkdownClick(object? sender, RoutedEventArgs e) => await ExportSelectedLogAsync("md");
    private async void OnExportLogTextClick(object? sender, RoutedEventArgs e) => await ExportSelectedLogAsync("txt");
    private async void OnExportLogHtmlClick(object? sender, RoutedEventArgs e) => await ExportSelectedLogAsync("html");

    private async Task ExportSelectedLogAsync(string format)
    {
        var selectedRow = LogsState.SelectedLogRow ?? _lastSelectedLogRow;
        if (selectedRow?.Session is not { } session)
        {
            await _dialogs.ShowMessageAsync(this, "Export", "Select a log row first.");
            return;
        }

        var history = await _historyService.GetFullSessionHistoryAsync(session.Id);
        string extension = format is "txt" or "html" ? format : "md";
        var options = new FilePickerSaveOptions
        {
            Title = "Export session",
            SuggestedFileName = BuildSafeExportFileName(session, extension),
            FileTypeChoices = new[]
            {
                new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = new[] { $"*.{extension}" } }
            }
        };

        var file = await TopLevel.GetTopLevel(this)?.StorageProvider.SaveFilePickerAsync(options)!;
        string? filePath = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        string content = extension switch
        {
            "txt" => BuildSessionExportText(session, history),
            "html" => await BuildSessionExportHtmlAsync(session, history),
            _ => BuildSessionExportMarkdown(session, history)
        };
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);
        _appStatus.Set($"Exported '{session.Title}' to {filePath}");
    }

    private static string BuildSafeExportFileName(ChatSession session, string extension)
    {
        string title = string.IsNullOrWhiteSpace(session.Title) ? $"session-{session.Id}" : session.Title;
        foreach (char c in Path.GetInvalidFileNameChars())
            title = title.Replace(c, '_');
        if (title.Length > 80)
            title = title[..80];
        return $"{title}.{extension}";
    }

    private static string BuildSessionExportMarkdown(ChatSession session, IReadOnlyList<ChatMessage> history)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Session Export");
        sb.AppendLine();
        sb.AppendLine($"- Session ID: {session.Id}");
        sb.AppendLine($"- Title: {EscapeMarkdownInline(session.Title)}");
        sb.AppendLine($"- Endpoint: {session.Endpoint}");
        sb.AppendLine($"- Created: {session.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Last Used: {session.LastUsedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Messages: {history.Count}");
        sb.AppendLine();
        foreach (var message in history)
        {
            sb.AppendLine($"## [{message.Timestamp:yyyy-MM-dd HH:mm:ss}] {EscapeMarkdownInline(message.Role)}");
            sb.AppendLine();
            sb.AppendLine(message.Content ?? string.Empty);
            sb.AppendLine();
            AppendMarkdownBullet(sb, "Model", message.ModelUsed);
            AppendMarkdownBullet(sb, "Tools", message.ActiveTools);
            AppendMarkdownBullet(sb, "Reasoning", message.ReasoningLevel);
            AppendMarkdownBullet(sb, "Search Context Size", message.SearchContextSize);
            AppendMarkdownCodeBlockIfAny(sb, "Raw JSON", "json", message.RawJson);
            foreach (var media in message.MediaFiles ?? Array.Empty<MediaFile>())
                sb.AppendLine($"- Attachment: {EscapeMarkdownInline(media.FileName)} ({media.MediaType})");
            sb.AppendLine("---");
        }
        return sb.ToString();
    }

    private static string BuildSessionExportText(ChatSession session, IReadOnlyList<ChatMessage> history)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SESSION EXPORT");
        sb.AppendLine($"Session ID: {session.Id}");
        sb.AppendLine($"Title: {session.Title}");
        sb.AppendLine($"Endpoint: {session.Endpoint}");
        sb.AppendLine($"Created: {session.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Last Used: {session.LastUsedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        foreach (var message in history)
        {
            sb.AppendLine($"[{message.Timestamp:yyyy-MM-dd HH:mm:ss}] {message.Role}");
            sb.AppendLine(message.Content ?? string.Empty);
            AppendTextLineIfAny(sb, "Model", message.ModelUsed);
            AppendTextLineIfAny(sb, "Tools", message.ActiveTools);
            AppendTextLineIfAny(sb, "Reasoning", message.ReasoningLevel);
            foreach (var media in message.MediaFiles ?? Array.Empty<MediaFile>())
                sb.AppendLine($"Attachment: {media.FileName} ({media.MediaType})");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task<string> BuildSessionExportHtmlAsync(ChatSession session, IReadOnlyList<ChatMessage> history)
    {
        string markdown = BuildSessionExportMarkdown(session, history);
        string encoded = System.Net.WebUtility.HtmlEncode(markdown);
        string css = await TryLoadMarkdownViewerCssAsync();
        return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{System.Net.WebUtility.HtmlEncode(session.Title)}</title><style>{css}</style></head><body><pre>{encoded}</pre></body></html>";
    }

    private async Task<string> TryLoadMarkdownViewerCssAsync()
    {
        string cssPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "MarkdownViewer", "markdown-base.css");
        return File.Exists(cssPath) ? await File.ReadAllTextAsync(cssPath) : "body{font-family:Arial,sans-serif;margin:24px}pre{white-space:pre-wrap}";
    }

    private static string EscapeMarkdownInline(string? value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("`", "\\`").Replace("*", "\\*").Replace("_", "\\_");

    private static void AppendMarkdownBullet(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sb.AppendLine($"- {label}: {EscapeMarkdownInline(value)}");
    }

    private static void AppendMarkdownCodeBlockIfAny(StringBuilder sb, string label, string language, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        sb.AppendLine($"### {label}");
        sb.AppendLine($"```{language}");
        sb.AppendLine(value);
        sb.AppendLine("```");
    }

    private static void AppendTextLineIfAny(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sb.AppendLine($"{label}: {value}");
    }

    private static string BuildDevSummary(IEnumerable<ChatMessage> messages) =>
        messages.Any(m => IsDeveloperToolsEnabled(m.DeveloperToolSettingsJson)) ? "Yes" : "No";

    private static bool IsDeveloperToolsEnabled(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("Enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }
}
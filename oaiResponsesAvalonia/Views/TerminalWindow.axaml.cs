using Avalonia.Controls;
using oaiResponsesAvalonia.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Views;

public sealed partial class TerminalWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly MarkdownViewerAssetServer _assetServer;
    private readonly AppConsoleService _console;
    private readonly Queue<AppConsoleWorkspaceConfigurationChange> _pendingWorkspaceNotifications = new();
    private bool _terminalPageReady;
    private bool _flushingWorkspaceNotifications;

    public TerminalWindow() : this(false, null)
    {
    }

    public TerminalWindow(bool developerToolsEnabled, string? workspaceRoot)
    {
        _console = new AppConsoleService(developerToolsEnabled, workspaceRoot);
        InitializeComponent();

        string assetDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "xterm");
        string indexPath = Path.Combine(assetDirectory, "index.html");
        if (!File.Exists(indexPath))
            throw new FileNotFoundException("The bundled xterm.js page was not found.", indexPath);

        _assetServer = MarkdownViewerAssetServer.Start(assetDirectory);
        Opened += (_, _) =>
        {
            TerminalWebView.NavigationCompleted += TerminalWebView_NavigationCompleted;
            TerminalWebView.Navigate(_assetServer.IndexUri);
        };
        Closed += TerminalWindow_Closed;
    }

    public void UpdateWorkspaceConfiguration(bool developerToolsEnabled, string? workspaceRoot)
    {
        AppConsoleWorkspaceConfigurationChange? change = _console.UpdateWorkspaceConfiguration(developerToolsEnabled, workspaceRoot);
        if (change is null)
            return;

        _pendingWorkspaceNotifications.Enqueue(change);
        if (_terminalPageReady)
            _ = FlushWorkspaceNotificationsAsync();
    }

    private async void TerminalWebView_NavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
            return;

        _terminalPageReady = true;
        await FlushWorkspaceNotificationsAsync();
    }

    private async Task FlushWorkspaceNotificationsAsync()
    {
        if (!_terminalPageReady || _flushingWorkspaceNotifications)
            return;

        _flushingWorkspaceNotifications = true;
        try
        {
            while (_pendingWorkspaceNotifications.Count > 0)
            {
                AppConsoleWorkspaceConfigurationChange change = _pendingWorkspaceNotifications.Peek();
                string payload = JsonSerializer.Serialize(new
                {
                    message = change.Message,
                    prompt = _console.Prompt,
                    cancelActiveWorkspacePager = change.CancelActiveWorkspacePager
                }, JsonOptions);
                await TerminalWebView.InvokeScript($"window.workspaceConfigurationChanged({payload});");
                _pendingWorkspaceNotifications.Dequeue();
            }
        }
        catch
        {
            // The web page may be navigating or closing; keep queued notifications for a later successful navigation.
        }
        finally
        {
            _flushingWorkspaceNotifications = false;
        }
    }

    private void TerminalWebView_EnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = true;
    }

    private async void TerminalWebView_WebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(e.Body))
                return;

            using JsonDocument document = JsonDocument.Parse(e.Body);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeElement))
                return;

            string? messageType = typeElement.GetString();
            if (messageType == "close")
            {
                Close();
                return;
            }

            if (!root.TryGetProperty("line", out JsonElement lineElement) || lineElement.ValueKind != JsonValueKind.String)
                return;

            string line = lineElement.GetString() ?? string.Empty;
            if (messageType == "complete")
            {
                AppConsoleCompletionResult completions = _console.GetPathCompletions(line);
                string completionJson = JsonSerializer.Serialize(completions, JsonOptions);
                await TerminalWebView.InvokeScript($"window.receiveCompletions({completionJson});");
                return;
            }

            if (messageType != "command")
                return;

            AppConsoleResult result = _console.Execute(line);
            string resultJson = JsonSerializer.Serialize(result, JsonOptions);
            await TerminalWebView.InvokeScript($"window.receiveResult({resultJson});");
        }
        catch (Exception ex)
        {
            try
            {
                var errorResult = new AppConsoleResult($"error: {ex.Message}", _console.Prompt, Kind: "error");
                string resultJson = JsonSerializer.Serialize(errorResult, JsonOptions);
                await TerminalWebView.InvokeScript($"window.receiveResult({resultJson});");
            }
            catch
            {
                // The page may be closing or navigating away.
            }
        }
    }

    private async void TerminalWindow_Closed(object? sender, EventArgs e)
    {
        await _assetServer.DisposeAsync();
    }
}

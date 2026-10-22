using Avalonia.Controls;
using oaiResponsesAvalonia.Services;
using System;
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
    private readonly AppConsoleService _console = new();

    public TerminalWindow()
    {
        InitializeComponent();

        string assetDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "xterm");
        string indexPath = Path.Combine(assetDirectory, "index.html");
        if (!File.Exists(indexPath))
            throw new FileNotFoundException("The bundled xterm.js page was not found.", indexPath);

        _assetServer = MarkdownViewerAssetServer.Start(assetDirectory);
        Opened += (_, _) => TerminalWebView.Navigate(_assetServer.IndexUri);
        Closed += TerminalWindow_Closed;
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
            if (!root.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.GetString() != "command" ||
                !root.TryGetProperty("line", out JsonElement lineElement) ||
                lineElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            AppConsoleResult result = _console.Execute(lineElement.GetString() ?? string.Empty);
            string resultJson = JsonSerializer.Serialize(result, JsonOptions);
            await TerminalWebView.InvokeScript($"window.receiveResult({resultJson});");
        }
        catch (Exception ex)
        {
            try
            {
                var errorResult = new AppConsoleResult($"error: {ex.Message}", _console.Prompt);
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

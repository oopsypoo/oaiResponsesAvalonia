using Avalonia.Controls;
using Avalonia.Interactivity;
using oaiResponsesAvalonia.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace oaiResponsesAvalonia.Views;

public sealed partial class McpServersWindow : Window
{
    private readonly IDialogService _dialogs = new AvaloniaDialogService();
    private readonly ObservableCollection<McpServerConfig> _servers = new(McpServerStore.Load());
    private bool _updatingTransport;

    public McpServersWindow()
    {
        InitializeComponent();
        ServerList.ItemsSource = _servers;
        ServerList.SelectionChanged += (_, _) => SyncTransportBox();
        if (_servers.Count > 0)
            ServerList.SelectedIndex = 0;
    }

    private McpServerConfig? Selected => ServerList.SelectedItem as McpServerConfig;

    private void SyncTransportBox()
    {
        if (Selected == null)
            return;

        _updatingTransport = true;
        TransportBox.SelectedIndex = Selected.IsHttp ? 0 : 1;
        _updatingTransport = false;
        SyncCheckAll();
    }

    private void Transport_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingTransport || Selected == null)
            return;

        if (TransportBox.SelectedItem is ComboBoxItem { Tag: string tag })
            Selected.Transport = tag;
    }

    private void CheckAllEnabled_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected == null)
            return;

        bool value = CheckAllEnabled.IsChecked == true;
        foreach (var tool in Selected.Tools)
            tool.Enabled = value;
    }

    private void CheckAllApproval_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected == null)
            return;

        bool value = CheckAllApproval.IsChecked == true;
        foreach (var tool in Selected.Tools)
            tool.RequireApproval = value;
    }

    private void SyncCheckAll()
    {
        var tools = Selected?.Tools;
        CheckAllEnabled.IsChecked = tools?.Count > 0 && tools.All(t => t.Enabled);
        CheckAllApproval.IsChecked = tools?.Count > 0 && tools.All(t => t.RequireApproval);
    }

    private void AddServer_Click(object? sender, RoutedEventArgs e)
    {
        var server = new McpServerConfig();
        _servers.Add(server);
        ServerList.SelectedItem = server;
    }

    private async void RemoveServer_Click(object? sender, RoutedEventArgs e)
    {
        var server = Selected;
        if (server == null)
            return;

        if (!await _dialogs.ConfirmAsync(this, "Remove MCP server", $"Remove '{server.Name}'?"))
            return;

        _servers.Remove(server);
        ServerList.SelectedIndex = _servers.Count > 0 ? 0 : -1;
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        var server = Selected;
        if (server == null)
            return;

        // A stdio server is a program that runs as the current user, so show exactly what will start.
        if (server.IsStdio && !await _dialogs.ConfirmAsync(
                this,
                "Start local program?",
                $"This will start the following program on your computer:\n\n{McpClientService.DescribeCommand(server)}\n\nOnly continue if you trust it."))
            return;

        ConnectButton.IsEnabled = false;
        server.Status = "Connecting...";
        try
        {
            var tools = await McpClientService.ListToolsAsync(server);
            McpClientService.MergeTools(server, tools);
            server.Status = $"Connected. {tools.Count} tool(s) available.";
            SyncCheckAll();
        }
        catch (Exception ex)
        {
            server.Status = $"Failed: {ex.Message}";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        McpServerStore.Save(_servers.ToList());
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace oaiResponsesAvalonia.Views;

internal sealed class FolderPickerWindow : Window
{
    private readonly TextBlock _pathText = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _statusText = new();
    private readonly ListBox _directoryList = new();
    private readonly Button _upButton = new() { Content = "Up" };
    private readonly Button _openButton = new() { Content = "Open" };
    private readonly List<string> _directories = new();
    private string _currentDirectory;

    public FolderPickerWindow(string title, string? initialDirectory)
    {
        _currentDirectory = GetUsableStartingDirectory(initialDirectory);
        Title = title;
        Width = 620;
        Height = 440;
        MinWidth = 420;
        MinHeight = 300;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var pathPanel = new DockPanel { LastChildFill = true, Margin = new Thickness(12, 12, 12, 6) };
        DockPanel.SetDock(_upButton, Dock.Right);
        pathPanel.Children.Add(_upButton);
        pathPanel.Children.Add(_pathText);

        _directoryList.Margin = new Thickness(12, 6, 12, 6);
        _directoryList.DoubleTapped += (_, _) => OpenSelectedDirectory();
        _directoryList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenSelectedDirectory();
                e.Handled = true;
            }
        };

        var cancelButton = new Button { Content = "Cancel", MinWidth = 90 };
        var chooseButton = new Button { Content = "Select this folder", MinWidth = 130 };
        _openButton.MinWidth = 90;
        _openButton.Click += (_, _) => OpenSelectedDirectory();
        _upButton.Click += (_, _) => NavigateToParent();
        cancelButton.Click += (_, _) => Close(null);
        chooseButton.Click += (_, _) => Close(_currentDirectory);

        var actionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(12, 6, 12, 12)
        };
        actionPanel.Children.Add(_statusText);
        actionPanel.Children.Add(_openButton);
        actionPanel.Children.Add(chooseButton);
        actionPanel.Children.Add(cancelButton);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        layout.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
        layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(pathPanel, 0);
        Grid.SetRow(_directoryList, 1);
        Grid.SetRow(actionPanel, 2);
        layout.Children.Add(pathPanel);
        layout.Children.Add(_directoryList);
        layout.Children.Add(actionPanel);
        Content = layout;

        RefreshDirectories();
    }

    private static string GetUsableStartingDirectory(string? requestedDirectory)
    {
        string? candidate = requestedDirectory;
        while (!string.IsNullOrWhiteSpace(candidate))
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (Directory.Exists(fullPath))
                    return fullPath;
                candidate = Path.GetDirectoryName(fullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                break;
            }
        }

        string userDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userDirectory) && Directory.Exists(userDirectory))
            return userDirectory;

        return Directory.GetCurrentDirectory();
    }

    private void NavigateToParent()
    {
        DirectoryInfo? parent = Directory.GetParent(_currentDirectory);
        if (parent is not null)
            NavigateTo(parent.FullName);
    }

    private void OpenSelectedDirectory()
    {
        if (_directoryList.SelectedIndex >= 0 && _directoryList.SelectedIndex < _directories.Count)
            NavigateTo(_directories[_directoryList.SelectedIndex]);
    }

    private void NavigateTo(string directory)
    {
        try
        {
            string fullPath = Path.GetFullPath(directory);
            if (Directory.Exists(fullPath))
            {
                _currentDirectory = fullPath;
                RefreshDirectories();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _statusText.Text = $"Could not open folder: {ex.Message}";
        }
    }

    private void RefreshDirectories()
    {
        _pathText.Text = _currentDirectory;
        _statusText.Text = string.Empty;
        _directories.Clear();
        try
        {
            _directories.AddRange(Directory.EnumerateDirectories(_currentDirectory)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));
            _directoryList.ItemsSource = _directories.Select(path => Path.GetFileName(path)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _directoryList.ItemsSource = Array.Empty<string>();
            _statusText.Text = $"Could not list folders: {ex.Message}";
        }

        _upButton.IsEnabled = Directory.GetParent(_currentDirectory) is not null;
        _openButton.IsEnabled = _directories.Count > 0;
    }
}

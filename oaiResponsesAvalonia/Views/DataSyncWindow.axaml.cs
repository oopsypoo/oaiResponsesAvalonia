using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using oaiResponsesAvalonia.Services;

namespace oaiResponsesAvalonia.Views;

public partial class DataSyncWindow : Window
{
    public DataSyncWindow() => InitializeComponent();

    private async void OpenProton_Click(object? sender, RoutedEventArgs e)
    {
        await new ProtonBackupWindow().ShowDialog(this);
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var options = new FolderPickerOpenOptions { Title = "Select a prepared isolated restore folder", AllowMultiple = false };
            if (Directory.Exists(IsolatedVisualTestService.RestoreRoot))
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(IsolatedVisualTestService.RestoreRoot);
            var folders = await StorageProvider.OpenFolderPickerAsync(options);
            if (folders.Count > 0)
                RestorePath.Text = folders[0].TryGetLocalPath() ?? string.Empty;
        }
        catch (Exception ex) { Status.Text = $"Could not select a folder: {ex.Message}"; }
    }

    private async void OpenTest_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RestorePath.Text))
        {
            Status.Text = "Choose a prepared restore folder first.";
            return;
        }

        OpenTestButton.IsEnabled = false;
        Status.Text = "Creating a disposable copy and checking media references…";
        try
        {
            string source = RestorePath.Text;
            string copy = await Task.Run(() => IsolatedVisualTestService.CreateCopy(source));
            IsolatedVisualTestService.Launch(copy);
            Status.Text = $"Started the isolated test. Disposable copy: {copy}";
        }
        catch (Exception ex) { Status.Text = $"Could not start the isolated test: {ex.Message}"; }
        finally { OpenTestButton.IsEnabled = true; }
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace oaiResponsesAvalonia.Views;

public partial class DataSyncWindow : Window
{
    public DataSyncWindow() => InitializeComponent();

    private async void OpenProton_Click(object? sender, RoutedEventArgs e)
    {
        await new ProtonBackupWindow().ShowDialog(this);
    }

}

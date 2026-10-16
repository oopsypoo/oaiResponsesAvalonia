using Avalonia.Controls;
using oaiResponsesAvalonia.Data;
using System.Collections.ObjectModel;

namespace oaiResponsesAvalonia.Views;

public sealed partial class DeveloperToolsActivityWindow : Window
{
    public DeveloperToolsActivityWindow(ObservableCollection<DeveloperToolActivityItem> activities)
    {
        InitializeComponent();
        ActivityList.ItemsSource = activities;
    }
}

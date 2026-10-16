using Avalonia.Controls;
using oaiResponsesAvalonia.Data;
using System.Collections.ObjectModel;

namespace oaiResponsesAvalonia.Views;

public sealed partial class DeveloperToolsActivityWindow : Window
{
    public DeveloperToolsActivityWindow()
        : this(new ObservableCollection<DeveloperToolActivityItem>())
    {
    }

    public DeveloperToolsActivityWindow(ObservableCollection<DeveloperToolActivityItem> activities)
    {
        InitializeComponent();
        DataContext = activities;
    }
}

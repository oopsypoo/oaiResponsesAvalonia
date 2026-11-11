using Avalonia.Controls;
using oaiResponsesAvalonia.Services;
using System.Collections.ObjectModel;

namespace oaiResponsesAvalonia.Views;

public sealed partial class CustomSearchResearchWindow : Window
{
    public CustomSearchResearchWindow()
        : this(new ObservableCollection<CustomSearchResearchTrail>())
    {
    }

    public CustomSearchResearchWindow(ObservableCollection<CustomSearchResearchTrail> entries)
    {
        InitializeComponent();
        DataContext = entries;
    }
}

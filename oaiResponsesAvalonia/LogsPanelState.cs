using CommunityToolkit.Mvvm.ComponentModel;

namespace oaiResponsesAvalonia
{
    public class LogsPanelState : ObservableObject
    {
        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value ?? string.Empty);
        }

        private string _typeFilter = "All";
        public string TypeFilter
        {
            get => _typeFilter;
            set => SetProperty(ref _typeFilter,
                string.IsNullOrWhiteSpace(value) ? "All" : value);
        }

        private bool _showTurns = true;
        public bool ShowTurns
        {
            get => _showTurns;
            set => SetProperty(ref _showTurns, value);
        }

        private bool _showMedia = true;
        public bool ShowMedia
        {
            get => _showMedia;
            set => SetProperty(ref _showMedia, value);
        }

        private bool _showTools = true;
        public bool ShowTools
        {
            get => _showTools;
            set => SetProperty(ref _showTools, value);
        }

        private bool _showModel = true;
        public bool ShowModel
        {
            get => _showModel;
            set => SetProperty(ref _showModel, value);
        }
        private bool _showDev = true;
        public bool ShowDev
        {
            get => _showDev;
            set => SetProperty(ref _showDev, value);
        }

        private LogRowViewModel? _selectedLogRow;
        public LogRowViewModel? SelectedLogRow
        {
            get => _selectedLogRow;
            set => SetProperty(ref _selectedLogRow, value);
        }
    }
}
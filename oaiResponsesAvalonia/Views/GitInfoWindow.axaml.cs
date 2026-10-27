using Avalonia.Controls;
using Avalonia.Interactivity;

namespace oaiResponsesAvalonia.Views
{
    public partial class GitInfoWindow : Window
    {
        public GitInfoWindow()
        {
            InitializeComponent();
        }

        public GitInfoWindow(string statusText) : this()
        {
            GitInfoTextBox.Text = statusText;
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    }
}

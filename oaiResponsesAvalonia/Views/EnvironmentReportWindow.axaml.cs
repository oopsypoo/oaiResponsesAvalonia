using Avalonia.Controls;
using Avalonia.Interactivity;
using oaiResponsesAvalonia.Services;

namespace oaiResponsesAvalonia.Views
{
    public partial class EnvironmentReportWindow : Window
    {
        public EnvironmentReportWindow(EnvironmentCapabilityReport? report)
        {
            InitializeComponent();
            ReportTextBox.Text = report?.Text ?? string.Empty;

            bool hasWorkspaceRoot = report?.WorkspaceRoot != null;
            WorkspaceTreeSection.IsVisible = hasWorkspaceRoot;
            Height = hasWorkspaceRoot ? 760 : 480;

            if (hasWorkspaceRoot)
                WorkspaceTreeView.ItemsSource = new[] { report!.WorkspaceRoot };
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    }
}

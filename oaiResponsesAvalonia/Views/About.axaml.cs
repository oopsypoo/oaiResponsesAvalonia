using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Linq;
using System.Reflection;

namespace oaiResponsesAvalonia.Views;

public partial class About : Window
{
    public About()
    {
        InitializeComponent();
        tbGitCommit.Text =
            $"Git commit: {GetShortGitCommit() ?? "Not embedded in this build"}";
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private static string? GetShortGitCommit()
    {
        string? informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informationalVersion))
            return null;

        int metadataSeparator = informationalVersion.IndexOf('+');
        if (metadataSeparator < 0)
            return null;

        string revision = informationalVersion[(metadataSeparator + 1)..];
        if (revision.Length < 7 || !revision.All(Uri.IsHexDigit))
            return null;

        return revision[..7];
    }
}
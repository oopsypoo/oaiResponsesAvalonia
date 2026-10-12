using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using System;
using System.Collections.Generic;
using System.Linq;
using oaiResponsesAvalonia.Native;

namespace oaiResponsesAvalonia.Views
{
    internal sealed class AppChooserWindow : Window
    {
        public AppChooserWindow(IReadOnlyList<AppEntry> apps)
        {
            Title = "Open with";
            Width = 380; Height = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var filter = new TextBox { PlaceholderText = "Search applications" };
            var list = new ListBox
            {
                ItemsSource = apps,
                ItemTemplate = new FuncDataTemplate<AppEntry>((a, _) =>
                {
                    var row = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8
                    };
                    row.Children.Add(IconLookup.CreateControl(a?.Icon, 24) ?? new Border { Width = 24, Height = 24 });
                    row.Children.Add(new TextBlock { Text = a?.Name, VerticalAlignment = VerticalAlignment.Center });
                    return row;
                })
            };
            var open = new Button
            {
                Content = "Open",
                IsDefault = true,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };

            void Accept() { if (list.SelectedItem is AppEntry a) Close(a); }
            open.Click += (_, _) => Accept();
            list.DoubleTapped += (_, _) => Accept();
            filter.TextChanged += (_, _) =>
                list.ItemsSource = apps.Where(a => a.Name.Contains(filter.Text ?? "",
                    StringComparison.CurrentCultureIgnoreCase)).ToList();

            DockPanel.SetDock(filter, Dock.Top);
            DockPanel.SetDock(open, Dock.Bottom);
            Content = new DockPanel { Margin = new Thickness(12), Children = { filter, open, list } };
        }
    }
}

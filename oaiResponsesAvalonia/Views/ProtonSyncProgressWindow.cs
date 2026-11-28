using Avalonia.Controls;
using Avalonia.Threading;
using System;

namespace oaiResponsesAvalonia.Views;

internal sealed class ProtonSyncProgressWindow : Window
{
    private readonly TextBlock _status;
    private readonly TextBox _output;

    public ProtonSyncProgressWindow(string title)
    {
        Title = title;
        Width = 720;
        Height = 420;
        MinWidth = 480;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = true;

        _status = new TextBlock
        {
            Text = title,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 10)
        };
        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Avalonia.Thickness(16),
            RowSpacing = 8
        };
        layout.Children.Add(_status);
        Grid.SetRow(_output, 1);
        layout.Children.Add(_output);
        Content = layout;
    }

    public void SetStatus(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetStatus(text));
            return;
        }

        _status.Text = text;
    }

    public void AppendOutput(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendOutput(text));
            return;
        }

        _output.Text += text;
        _output.CaretIndex = _output.Text?.Length ?? 0;
    }
}

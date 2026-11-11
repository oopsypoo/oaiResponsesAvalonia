using Avalonia.Controls;
using Avalonia.Interactivity;
using oaiResponsesAvalonia.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Views;

public partial class SaveAsWindow : Window
{
    private readonly ImageLibraryService _imageService;
    private readonly string _imagePath;
    private readonly string _targetDirectory;
    private readonly ImageProcessingOptions _options;

    public SaveAsWindow(
        ImageLibraryService imageService,
        string imagePath,
        string targetDirectory,
        string defaultBaseName,
        ImageProcessingOptions options,
        bool keepTransparency)
    {
        _imageService = imageService;
        _imagePath = imagePath;
        _targetDirectory = targetDirectory;
        _options = options;
        InitializeComponent();
        OutputNameBox.Text = defaultBaseName;
        KeepTransparencyCheckBox.IsChecked = keepTransparency;
        UpdateFormatOptions();
    }

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        DialogStatusText.IsVisible = false;
        try
        {
            string baseName = Path.GetFileNameWithoutExtension((OutputNameBox.Text ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(baseName))
                throw new ArgumentException("Enter a filename for the saved image.");

            string extension = SelectedTag(OutputFormatPicker, ".png").ToLowerInvariant();
            int[]? icoSizes = extension == ".ico" ? ParseIcoSizes(IcoSizesBox.Text ?? string.Empty) : null;
            bool keepTransparency = KeepTransparencyCheckBox.IsChecked == true;
            byte[] output = await Task.Run(() => _imageService.ConvertToBytes(
                _imagePath, _options, extension, keepTransparency, icoSizes));
            string savedPath = _imageService.SaveToUserImages(_targetDirectory, baseName + extension, output);
            Close(savedPath);
        }
        catch (Exception ex)
        {
            DialogStatusText.Text = $"Save failed: {ex.Message}";
            DialogStatusText.IsVisible = true;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void OutputFormatPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateFormatOptions();

    private void UpdateFormatOptions()
    {
        if (IcoSizesBox is null)
            return;

        string extension = SelectedTag(OutputFormatPicker, ".png");
        IcoSizesBox.IsVisible = extension == ".ico";
        KeepTransparencyCheckBox.IsVisible = extension is ".png" or ".webp" or ".bmp" or ".ico";
        TransparencyHelpText.Text = extension is ".jpg" or ".jpeg"
            ? "JPEG cannot store transparency; transparent pixels will be composited onto white."
            : "When unchecked, transparent pixels are composited onto white before saving.";
    }

    private static int[] ParseIcoSizes(string text)
    {
        int[] sizes = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out int size) ? size : throw new FormatException($"'{value}' is not an ICO size."))
            .Distinct().OrderByDescending(size => size).ToArray();
        if (sizes.Length == 0 || sizes.Any(size => size < 1 || size > 256))
            throw new ArgumentException("Enter ICO sizes from 1 to 256, separated by commas.");
        return sizes;
    }

    private static string SelectedTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
}

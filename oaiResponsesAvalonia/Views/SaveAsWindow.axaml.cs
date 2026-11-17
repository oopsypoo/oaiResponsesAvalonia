using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using oaiResponsesAvalonia.Services;
using SixLabors.ImageSharp.PixelFormats;
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
    private readonly bool _hasTransparency;
    private Bitmap? _previewBitmap;
    private SixLabors.ImageSharp.Image<Rgba32>? _previewPixels;
    private bool _isPickingColor;
    private bool _initializing = true;

    public SaveAsWindow(
        ImageLibraryService imageService,
        string imagePath,
        string targetDirectory,
        string defaultBaseName,
        ImageProcessingOptions options,
        byte[] previewPng)
    {
        _imageService = imageService;
        _imagePath = imagePath;
        _targetDirectory = targetDirectory;
        _options = options;
        _hasTransparency = _imageService.PreviewContainsTransparency(previewPng);
        InitializeComponent();
        OutputNameBox.Text = defaultBaseName;
        using (var stream = new MemoryStream(previewPng, writable: false))
            _previewBitmap = new Bitmap(stream);
        _previewPixels = SixLabors.ImageSharp.Image.Load<Rgba32>(previewPng);
        PreviewImage.Source = _previewBitmap;
        KeepTransparencyCheckBox.IsChecked = _hasTransparency;
        _initializing = false;
        UpdateFormatOptions();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        _previewPixels?.Dispose();
        _previewPixels = null;
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
            Color selectedColor = BackgroundColorPicker.Color;
            var background = new Rgba32(selectedColor.R, selectedColor.G, selectedColor.B, 255);
            byte[] output = await Task.Run(() => _imageService.ConvertToBytes(
                _imagePath, _options, extension, keepTransparency, icoSizes, background: background));
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

    private void PickFromImageButton_Click(object? sender, RoutedEventArgs e)
    {
        _isPickingColor = !_isPickingColor;
        PickFromImageButton.Content = _isPickingColor ? "Cancel picking" : "Pick from image";
        PickFromImageStatusText.Text = _isPickingColor
            ? "Click a point on the image preview to use its visible color as the background."
            : string.Empty;
        PickFromImageStatusText.IsVisible = _isPickingColor;
        PreviewImage.Cursor = _isPickingColor ? new Cursor(StandardCursorType.Cross) : null;
    }

    private void PreviewImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_isPickingColor || !e.GetCurrentPoint(PreviewImage).Properties.IsLeftButtonPressed || _previewPixels is null)
            return;

        e.Handled = true;
        var position = e.GetPosition(PreviewImage);
        double scale = Math.Min(
            PreviewImage.Bounds.Width / _previewPixels.Width,
            PreviewImage.Bounds.Height / _previewPixels.Height);
        if (scale <= 0)
            return;

        double displayedWidth = _previewPixels.Width * scale;
        double displayedHeight = _previewPixels.Height * scale;
        double offsetX = (PreviewImage.Bounds.Width - displayedWidth) / 2;
        double offsetY = (PreviewImage.Bounds.Height - displayedHeight) / 2;
        double imageX = position.X - offsetX;
        double imageY = position.Y - offsetY;
        if (imageX < 0 || imageY < 0 || imageX >= displayedWidth || imageY >= displayedHeight)
        {
            PickFromImageStatusText.Text = "Click inside the visible image area.";
            return;
        }

        int pixelX = Math.Clamp((int)(imageX / scale), 0, _previewPixels.Width - 1);
        int pixelY = Math.Clamp((int)(imageY / scale), 0, _previewPixels.Height - 1);
        Rgba32 pixel = _previewPixels[pixelX, pixelY];
        Color currentBackground = BackgroundColorPicker.Color;
        int alpha = pixel.A;
        byte red = (byte)((pixel.R * alpha + currentBackground.R * (255 - alpha) + 127) / 255);
        byte green = (byte)((pixel.G * alpha + currentBackground.G * (255 - alpha) + 127) / 255);
        byte blue = (byte)((pixel.B * alpha + currentBackground.B * (255 - alpha) + 127) / 255);

        // Keep the picker synchronized with the sampled color so the selected value is visible and editable.
        BackgroundColorPicker.Color = Color.FromArgb(255, red, green, blue);
        _isPickingColor = false;
        PickFromImageButton.Content = "Pick from image";
        PickFromImageStatusText.IsVisible = false;
        PreviewImage.Cursor = null;
    }

    private void OutputFormatPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateFormatOptions();

    private void KeepTransparencyCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_initializing)
            UpdateFormatOptions();
    }

    private void BackgroundColorPicker_ColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (!_initializing)
            UpdatePreviewBackground();
    }

    private void UpdateFormatOptions()
    {
        if (IcoSizesBox is null)
            return;

        string extension = SelectedTag(OutputFormatPicker, ".png").ToLowerInvariant();
        IcoSizesBox.IsVisible = extension == ".ico";
        bool transparencySupported = extension is ".png" or ".webp" or ".bmp" or ".ico";
        KeepTransparencyCheckBox.IsVisible = _hasTransparency && transparencySupported;
        bool keepTransparency = _hasTransparency && transparencySupported && KeepTransparencyCheckBox.IsChecked == true;
        bool mustFlatten = _hasTransparency && !keepTransparency;
        BackgroundColorPanel.IsVisible = mustFlatten;
        if (!mustFlatten && _isPickingColor)
        {
            _isPickingColor = false;
            PickFromImageButton.Content = "Pick from image";
            PickFromImageStatusText.IsVisible = false;
            PreviewImage.Cursor = null;
        }
        TransparencyHelpText.Text = !_hasTransparency
            ? "This preview contains no transparent pixels."
            : mustFlatten
                ? "The preview shows transparent pixels composited onto the selected background color."
                : "Transparency will be preserved; the background color will not be applied.";
        UpdatePreviewBackground();
    }

    private void UpdatePreviewBackground()
    {
        if (PreviewBackgroundBorder is null)
            return;

        string extension = SelectedTag(OutputFormatPicker, ".png").ToLowerInvariant();
        bool transparencySupported = extension is ".png" or ".webp" or ".bmp" or ".ico";
        bool flattenTransparency = _hasTransparency &&
            (!transparencySupported || KeepTransparencyCheckBox.IsChecked != true);
        Color color = flattenTransparency ? BackgroundColorPicker.Color : Colors.White;
        PreviewBackgroundBorder.Background = new SolidColorBrush(color);
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

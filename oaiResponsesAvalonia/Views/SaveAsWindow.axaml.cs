using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Runtime.InteropServices;
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
    private bool _closed;
    private bool _fullResolutionReady;
    private bool _fitToWindow = true;
    private double _zoom = 1;
    private int _zoomRevision;

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
        PreviewImage.Source = _previewBitmap;
        PickFromImageButton.IsEnabled = false;
        KeepTransparencyCheckBox.IsChecked = _hasTransparency;
        _initializing = false;
        UpdateFormatOptions();
        Opened += SaveAsWindow_Opened;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        PreviewImage.Source = null;
        base.OnClosed(e);
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        _previewPixels?.Dispose();
        _previewPixels = null;
    }

    private async void SaveAsWindow_Opened(object? sender, EventArgs e)
    {
        ApplyZoom();
        try
        {
            // Use the same processing path as Save As, not the resized editor preview.
            var result = await Task.Run(() =>
            {
                var image = _imageService.LoadProcessed(_imagePath, _options);
                try
                {
                    // This dialog holds both processed pixels and a display bitmap. Avoid unbounded
                    // additional allocations; the regular save operation remains available.
                    if ((long)image.Width * image.Height > 32_000_000)
                        throw new InvalidOperationException("Image exceeds the 32-megapixel inspection limit. Saving is still available.");
                    var pixels = new byte[checked(image.Width * image.Height * 4)];
                    image.CopyPixelDataTo(pixels);
                    return (Image: image, Pixels: pixels);
                }
                catch
                {
                    image.Dispose();
                    throw;
                }
            });

            if (_closed)
            {
                result.Image.Dispose();
                return;
            }

            Bitmap bitmap;
            try
            {
                var handle = GCHandle.Alloc(result.Pixels, GCHandleType.Pinned);
                try
                {
                    bitmap = new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(),
                        new PixelSize(result.Image.Width, result.Image.Height), new Vector(96, 96),
                        checked(result.Image.Width * 4));
                }
                finally
                {
                    handle.Free();
                }
            }
            catch
            {
                result.Image.Dispose();
                throw;
            }

            var previous = _previewBitmap;
            _previewBitmap = bitmap;
            _previewPixels = result.Image;
            PreviewImage.Source = bitmap;
            previous?.Dispose();
            _fullResolutionReady = true;
            ZoomControls.IsEnabled = true;
            PickFromImageButton.IsEnabled = true;
            PixelInfoText.Text = "Move over the image to inspect its RGB and alpha. Use the scrollbars to pan.";
            ApplyZoom();
        }
        catch (Exception ex)
        {
            if (!_closed)
                PixelInfoText.Text = $"Full-resolution inspection unavailable: {ex.Message}";
        }
    }

    private void ApplyZoom()
    {
        if (_initializing || _previewBitmap is null || PreviewScrollViewer is null)
            return;
        var viewport = PreviewScrollViewer.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0)
            return;
        int width = _previewBitmap.PixelSize.Width;
        int height = _previewBitmap.PixelSize.Height;
        double scale = _fitToWindow ? ImageInspectorGeometry.FitScale(width, height, viewport) : _zoom;
        PreviewImage.Width = width * scale;
        PreviewImage.Height = height * scale;
        ZoomText.Text = $"{scale * 100:0.#}%";
    }

    // Follow the inspector's fit/zoom/scroll pattern; keep the viewport centre stable on zoom.
    private void SetZoom(double scale)
    {
        if (_previewBitmap is null || PreviewScrollViewer.Viewport.Width <= 0 || PreviewImage.Width <= 0)
            return;
        var viewport = PreviewScrollViewer.Viewport;
        var offset = PreviewScrollViewer.Offset;
        double oldWidth = PreviewImage.Width;
        double oldHeight = PreviewImage.Height;
        double centreX = (offset.X + viewport.Width / 2 - Math.Max(0, (viewport.Width - oldWidth) / 2)) / oldWidth;
        double centreY = (offset.Y + viewport.Height / 2 - Math.Max(0, (viewport.Height - oldHeight) / 2)) / oldHeight;
        _fitToWindow = false;
        _zoom = Math.Clamp(scale, 0.0001, 32);
        ApplyZoom();
        double x = centreX * PreviewImage.Width + Math.Max(0, (viewport.Width - PreviewImage.Width) / 2) - viewport.Width / 2;
        double y = centreY * PreviewImage.Height + Math.Max(0, (viewport.Height - PreviewImage.Height) / 2) - viewport.Height / 2;
        int revision = ++_zoomRevision;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && revision == _zoomRevision)
                PreviewScrollViewer.Offset = new Vector(Math.Max(0, x), Math.Max(0, y));
        }, DispatcherPriority.Loaded);
    }

    private double CurrentScale => _previewBitmap is null ? 1 : PreviewImage.Width / _previewBitmap.PixelSize.Width;
    private void ZoomOutButton_Click(object? sender, RoutedEventArgs e) => SetZoom(CurrentScale / 1.5);
    private void ZoomInButton_Click(object? sender, RoutedEventArgs e) => SetZoom(CurrentScale * 1.5);
    private void ActualSizeButton_Click(object? sender, RoutedEventArgs e) => SetZoom(1);
    private void FitButton_Click(object? sender, RoutedEventArgs e)
    {
        ++_zoomRevision;
        _fitToWindow = true;
        ApplyZoom();
        PreviewScrollViewer.Offset = default;
    }
    private void PreviewScrollViewer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_fitToWindow)
            ApplyZoom();
    }

    private bool TryGetPixel(Point position, out Rgba32 pixel, out int x, out int y)
    {
        pixel = default;
        x = y = 0;
        if (!_fullResolutionReady || _previewPixels is null || PreviewImage.Bounds.Width <= 0 || PreviewImage.Bounds.Height <= 0 ||
            position.X < 0 || position.Y < 0 || position.X >= PreviewImage.Bounds.Width || position.Y >= PreviewImage.Bounds.Height)
            return false;
        x = Math.Min((int)(position.X * _previewPixels.Width / PreviewImage.Bounds.Width), _previewPixels.Width - 1);
        y = Math.Min((int)(position.Y * _previewPixels.Height / PreviewImage.Bounds.Height), _previewPixels.Height - 1);
        pixel = _previewPixels[x, y];
        return true;
    }

    private void PreviewImage_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (TryGetPixel(e.GetPosition(PreviewImage), out var pixel, out int x, out int y))
        {
            PixelInfoText.Text = pixel.A == 0
                ? $"Pixel ({x}, {y})  Fully transparent — no usable color"
                : $"Pixel ({x}, {y})  Raw RGB #{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}  Alpha {pixel.A}/255";
            PixelColorSwatch.IsVisible = pixel.A != 0;
            if (pixel.A != 0)
                PixelColorSwatch.Background = new SolidColorBrush(Color.FromRgb(pixel.R, pixel.G, pixel.B));
        }
        else
        {
            PixelColorSwatch.IsVisible = false;
        }
    }

    private void PreviewImage_PointerExited(object? sender, PointerEventArgs e)
    {
        PixelColorSwatch.IsVisible = false;
        if (_fullResolutionReady)
            PixelInfoText.Text = "Move over the image to inspect its RGB and alpha. Use the scrollbars to pan.";
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

    private void ResetBackgroundButton_Click(object? sender, RoutedEventArgs e)
    {
        BackgroundColorPicker.Color = Colors.White;
        UpdatePreviewBackground();
    }

    private void PickFromImageButton_Click(object? sender, RoutedEventArgs e)
    {
        _isPickingColor = !_isPickingColor;
        PickFromImageButton.Content = _isPickingColor ? "Cancel picking" : "Pick from image";
        PickFromImageStatusText.Text = _isPickingColor
            ? "Click a visible pixel in the image. Partially transparent pixels can be sampled; fully transparent pixels have no usable color."
            : string.Empty;
        PickFromImageStatusText.IsVisible = _isPickingColor;
        PreviewImage.Cursor = _isPickingColor ? new Cursor(StandardCursorType.Cross) : null;
    }

    private void PreviewImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_isPickingColor || !e.GetCurrentPoint(PreviewImage).Properties.IsLeftButtonPressed || _previewPixels is null)
            return;

        e.Handled = true;
        if (!TryGetPixel(e.GetPosition(PreviewImage), out Rgba32 pixel, out _, out _))
        {
            PickFromImageStatusText.Text = "Click inside the visible image area.";
            return;
        }
        if (pixel.A == 0)
        {
            PickFromImageStatusText.Text = "That pixel is fully transparent and has no usable color. Pick a visible pixel instead.";
            return;
        }

        // Sample the processed image's stored RGB color without compositing it over the current background.
        // This also accepts partially transparent edge pixels, whose RGB can provide a useful background match.
        BackgroundColorPicker.Color = Color.FromArgb(255, pixel.R, pixel.G, pixel.B);
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

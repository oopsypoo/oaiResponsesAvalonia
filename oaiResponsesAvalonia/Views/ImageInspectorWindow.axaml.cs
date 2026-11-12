using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using oaiResponsesAvalonia.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Views;

public sealed partial class ImageInspectorWindow : Window
{
    private readonly IReadOnlyList<string> _imagePaths;
    private readonly ImageLibraryService _imageLibrary;
    private int _currentIndex;
    private Bitmap? _bitmap;
    private bool _fitToWindow = true;
    private bool _fitPending;
    private double _zoom = 1;

    public ImageInspectorWindow(IReadOnlyList<string> imagePaths, ImageLibraryService imageLibrary)
    {
        if (imagePaths.Count == 0)
            throw new ArgumentException("At least one image is required.", nameof(imagePaths));

        _imagePaths = imagePaths;
        _imageLibrary = imageLibrary;
        InitializeComponent();
        PreviewScrollViewer.LayoutUpdated += PreviewScrollViewer_LayoutUpdated;
        Opened += ImageInspectorWindow_Opened;
        Closed += (_, _) => _bitmap?.Dispose();
    }

    private void ImageInspectorWindow_Opened(object? sender, EventArgs e)
    {
        ConfigureWindowBounds();
        LoadCurrentImage();
    }

    private void ConfigureWindowBounds()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        double maxWidth = Math.Max(1, screen.WorkingArea.Width / scale - 32);
        double maxHeight = Math.Max(1, screen.WorkingArea.Height / scale - 48);

        MaxWidth = maxWidth;
        MaxHeight = maxHeight;
        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);
        Width = Math.Min(Width, maxWidth);
        Height = Math.Min(Height, maxHeight);
    }

    private void LoadCurrentImage()
    {
        try
        {
            _bitmap?.Dispose();
            ImageAssetPreview preview = _imageLibrary.CreatePreview(_imagePaths[_currentIndex], maxSize: 4096);
            _bitmap = new Bitmap(new MemoryStream(preview.PngBytes));
            PreviewImage.Source = _bitmap;
            _fitToWindow = true;
            _zoom = 1;
            ImageNameText.Text = $"{Path.GetFileName(_imagePaths[_currentIndex])}  ({_currentIndex + 1}/{_imagePaths.Count})  ·  {preview.Width} × {preview.Height}";
            StatusText.Text = "Mouse wheel zooms; scroll to pan when zoomed. Save As copies the original image.";
            PreviousButton.IsEnabled = _currentIndex > 0;
            NextButton.IsEnabled = _currentIndex < _imagePaths.Count - 1;
            Title = $"Image Inspector — {Path.GetFileName(_imagePaths[_currentIndex])}";
            ApplyImageScale();
        }
        catch (Exception ex)
        {
            PreviewImage.Source = null;
            ImageNameText.Text = Path.GetFileName(_imagePaths[_currentIndex]);
            StatusText.Text = $"Could not open image: {ex.Message}";
        }
    }

    private void PreviewScrollViewer_LayoutUpdated(object? sender, EventArgs e)
    {
        if (_fitPending)
            ApplyImageScale();
    }

    private void ApplyImageScale()
    {
        if (_bitmap is null)
            return;

        double viewportWidth = PreviewScrollViewer.Viewport.Width;
        double viewportHeight = PreviewScrollViewer.Viewport.Height;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            _fitPending = true;
            return;
        }

        _fitPending = false;
        double scale = _fitToWindow
            ? Math.Min(1, Math.Min(viewportWidth / _bitmap.PixelSize.Width, viewportHeight / _bitmap.PixelSize.Height))
            : _zoom;
        double imageWidth = Math.Max(1, _bitmap.PixelSize.Width * scale);
        double imageHeight = Math.Max(1, _bitmap.PixelSize.Height * scale);
        PreviewImage.Width = imageWidth;
        PreviewImage.Height = imageHeight;
        ImageCanvas.Width = Math.Max(viewportWidth, imageWidth);
        ImageCanvas.Height = Math.Max(viewportHeight, imageHeight);
        ZoomText.Text = $"{Math.Round(scale * 100)}%";
    }

    private void ChangeZoom(double factor)
    {
        if (_bitmap is null)
            return;
        if (_fitToWindow)
        {
            double viewportWidth = PreviewScrollViewer.Viewport.Width;
            double viewportHeight = PreviewScrollViewer.Viewport.Height;
            _zoom = viewportWidth > 0 && viewportHeight > 0
                ? Math.Min(1, Math.Min(viewportWidth / _bitmap.PixelSize.Width, viewportHeight / _bitmap.PixelSize.Height))
                : 1;
            _fitToWindow = false;
        }
        _zoom = Math.Clamp(_zoom * factor, 0.1, 8.0);
        ApplyImageScale();
    }

    private void PreviousButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_currentIndex <= 0)
            return;
        _currentIndex--;
        LoadCurrentImage();
    }

    private void NextButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_currentIndex >= _imagePaths.Count - 1)
            return;
        _currentIndex++;
        LoadCurrentImage();
    }

    private void ZoomOutButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ChangeZoom(1 / 1.25);

    private void ZoomInButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ChangeZoom(1.25);

    private void FitButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _fitToWindow = true;
        ApplyImageScale();
    }

    private void ActualSizeButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _fitToWindow = false;
        _zoom = 1;
        ApplyImageScale();
    }

    private void PreviewScrollViewer_SizeChanged(object? sender, SizeChangedEventArgs e) => ApplyImageScale();

    private void PreviewScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        ChangeZoom(e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private async void SaveAsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string sourcePath = _imagePaths[_currentIndex];
        string fileName = Path.GetFileName(sourcePath);
        string extension = Path.GetExtension(sourcePath);
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save image copy as",
            SuggestedFileName = fileName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(string.IsNullOrWhiteSpace(extension) ? "Image" : $"{extension.TrimStart('.').ToUpperInvariant()} image")
                {
                    Patterns = string.IsNullOrWhiteSpace(extension) ? new[] { "*" } : new[] { $"*{extension}" }
                }
            }
        });

        if (destination is null)
            return;

        try
        {
            string? destinationPath = destination.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(destinationPath) &&
                string.Equals(Path.GetFullPath(destinationPath), Path.GetFullPath(sourcePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Choose a different destination; the original image cannot be overwritten.");

            await using Stream source = File.OpenRead(sourcePath);
            await using Stream target = await destination.OpenWriteAsync();
            await source.CopyToAsync(target);
            StatusText.Text = $"Saved an unchanged copy to {destination.Name}. The original was not modified.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save image copy: {ex.Message}";
        }
    }
}

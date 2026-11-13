using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using oaiResponsesAvalonia.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp.Processing;
using SharpImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace oaiResponsesAvalonia.Views;

public sealed partial class ImageInspectorWindow : Window
{
    private readonly IReadOnlyList<string> _imagePaths;
    private readonly ImageLibraryService _imageLibrary;
    private readonly SemaphoreSlim _workGate = new(1, 1);
    private readonly DispatcherTimer _renderTimer;
    private SharpImage? _image;
    private int _currentIndex;
    private Bitmap? _bitmap;
    private bool _fitToWindow = true;
    private bool _fitPending;
    private bool _closed;
    private double _zoom = 1;
    private double _layoutRenderScale;
    private int _imageRevision;
    private int _renderRevision;
    private InspectorRenderRegion? _requestedRegion;

    public ImageInspectorWindow(IReadOnlyList<string> imagePaths, ImageLibraryService imageLibrary)
    {
        if (imagePaths.Count == 0)
            throw new ArgumentException("At least one image is required.", nameof(imagePaths));

        _imagePaths = imagePaths;
        _imageLibrary = imageLibrary;
        InitializeComponent();
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
        _renderTimer.Tick += RenderTimer_Tick;
        PreviewScrollViewer.LayoutUpdated += PreviewScrollViewer_LayoutUpdated;
        Opened += ImageInspectorWindow_Opened;
        Closed += ImageInspectorWindow_Closed;
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

    private async void LoadCurrentImage()
    {
        int imageRevision = Interlocked.Increment(ref _imageRevision);
        Interlocked.Increment(ref _renderRevision);
        _renderTimer.Stop();
        _requestedRegion = null;
        var oldImage = _image;
        _image = null;
        DisposeImageLater(oldImage);
        PreviewImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _fitToWindow = true;
        _zoom = 1;
        _fitPending = true;
        PreviewScrollViewer.Offset = default;
        string path = _imagePaths[_currentIndex];
        ImageNameText.Text = Path.GetFileName(path);
        StatusText.Text = "Loading image…";
        PreviousButton.IsEnabled = _currentIndex > 0;
        NextButton.IsEnabled = _currentIndex < _imagePaths.Count - 1;
        Title = $"Image Inspector — {Path.GetFileName(path)}";

        try
        {
            await _workGate.WaitAsync();
            SharpImage image;
            try
            {
                if (imageRevision != Volatile.Read(ref _imageRevision))
                    return;
                image = await Task.Run(() => _imageLibrary.LoadInspectorImage(path));
            }
            finally
            {
                _workGate.Release();
            }

            if (_closed || imageRevision != _imageRevision)
            {
                image.Dispose();
                return;
            }
            _image = image;
            ImageNameText.Text = $"{Path.GetFileName(path)}  ({_currentIndex + 1}/{_imagePaths.Count})  ·  {image.Width} × {image.Height}";
            ApplyImageScale();
        }
        catch (Exception ex)
        {
            if (!_closed && imageRevision == _imageRevision)
                StatusText.Text = $"Could not open image: {ex.Message}";
        }
    }

    private void PreviewScrollViewer_LayoutUpdated(object? sender, EventArgs e)
    {
        if (_fitPending || _layoutRenderScale != RenderScaling)
            ApplyImageScale();
    }

    private void ApplyImageScale()
    {
        if (_image is null || _closed)
            return;

        Size viewport = PreviewScrollViewer.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            _fitPending = true;
            return;
        }

        _fitPending = false;
        _layoutRenderScale = RenderScaling;
        double scale = _fitToWindow
            ? ImageInspectorGeometry.FitScale(_image.Width, _image.Height, viewport)
            : _zoom;
        ImageCanvas.Width = Math.Max(viewport.Width, _image.Width * scale);
        ImageCanvas.Height = Math.Max(viewport.Height, _image.Height * scale);
        ZoomText.Text = $"{Math.Round(scale * 100)}%";
        var region = ImageInspectorGeometry.VisibleRegion(
            _image.Width, _image.Height, viewport, PreviewScrollViewer.Offset, scale, RenderScaling);
        if (region is null || region == _requestedRegion)
            return;

        _requestedRegion = region;
        Interlocked.Increment(ref _renderRevision);
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private void ChangeZoom(double factor)
    {
        if (_image is null)
            return;
        if (_fitToWindow)
        {
            Size viewport = PreviewScrollViewer.Viewport;
            _zoom = viewport.Width > 0 && viewport.Height > 0
                ? ImageInspectorGeometry.FitScale(_image.Width, _image.Height, viewport)
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

    private void PreviewScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e) => ApplyImageScale();

    private void PreviewScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        ChangeZoom(e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private async void RenderTimer_Tick(object? sender, EventArgs e)
    {
        _renderTimer.Stop();
        if (_closed || _image is not { } image || _requestedRegion is not { } region)
            return;

        int imageRevision = _imageRevision;
        int renderRevision = _renderRevision;
        try
        {
            await _workGate.WaitAsync();
            byte[] pixels;
            try
            {
                if (imageRevision != Volatile.Read(ref _imageRevision) ||
                    renderRevision != Volatile.Read(ref _renderRevision))
                    return;
                pixels = await Task.Run(() => RenderRegion(image, region));
            }
            finally
            {
                _workGate.Release();
            }

            if (_closed || imageRevision != _imageRevision || renderRevision != _renderRevision)
                return;
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            Bitmap bitmap;
            try
            {
                bitmap = new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(),
                    region.Raster, new Vector(96, 96), checked(region.Raster.Width * 4));
            }
            finally
            {
                handle.Free();
            }

            var previous = _bitmap;
            _bitmap = bitmap;
            PreviewImage.Source = bitmap;
            Canvas.SetLeft(PreviewImage, region.Left);
            Canvas.SetTop(PreviewImage, region.Top);
            PreviewImage.Width = region.Width;
            PreviewImage.Height = region.Height;
            previous?.Dispose();
            StatusText.Text = "Mouse wheel zooms; scroll to pan when zoomed. Save As copies the original image.";
        }
        catch (Exception ex)
        {
            if (!_closed && imageRevision == _imageRevision && renderRevision == _renderRevision)
            {
                _requestedRegion = null;
                StatusText.Text = $"Could not render image: {ex.Message}";
            }
        }
    }

    private static byte[] RenderRegion(SharpImage image, InspectorRenderRegion region)
    {
        var rect = region.Source;
        using var rendered = new SharpImage(rect.Width, rect.Height);
        image.ProcessPixelRows(rendered, (source, target) =>
        {
            for (int y = 0; y < rect.Height; y++)
                source.GetRowSpan(rect.Y + y).Slice(rect.X, rect.Width).CopyTo(target.GetRowSpan(y));
        });
        if (rect.Width != region.Raster.Width || rect.Height != region.Raster.Height)
            rendered.Mutate(context => context.Resize(region.Raster.Width, region.Raster.Height));
        var pixels = new byte[checked(region.Raster.Width * region.Raster.Height * 4)];
        rendered.CopyPixelDataTo(pixels);
        return pixels;
    }

    private async void DisposeImageLater(SharpImage? image)
    {
        if (image is null)
            return;
        await _workGate.WaitAsync();
        try
        {
            image.Dispose();
        }
        finally
        {
            _workGate.Release();
        }
    }

    private void ImageInspectorWindow_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        Interlocked.Increment(ref _imageRevision);
        Interlocked.Increment(ref _renderRevision);
        _renderTimer.Stop();
        PreviewImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        var image = _image;
        _image = null;
        DisposeImageLater(image);
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

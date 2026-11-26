using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using oaiResponsesAvalonia;
using oaiResponsesAvalonia.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpImage = SixLabors.ImageSharp.Image;

namespace oaiResponsesAvalonia.Views;

public sealed class WebpAnimationSourceItem : IDisposable, System.ComponentModel.INotifyPropertyChanged
{
    private int _durationMilliseconds = 100;
    private int _frameWidth;
    private int _frameHeight;
    private int _xOffset;
    private int _yOffset;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string? FullPath { get; }
    public string Name { get; }
    public string Dimensions { get; }
    public Bitmap Thumbnail { get; }
    public byte[]? ImageBytes { get; }
    public int DurationMilliseconds { get => _durationMilliseconds; set { _durationMilliseconds = value; NotifyFrameSummary(); } }
    public int FrameWidth { get => _frameWidth; set { _frameWidth = value; NotifyFrameSummary(); } }
    public int FrameHeight { get => _frameHeight; set { _frameHeight = value; NotifyFrameSummary(); } }
    public int XOffset { get => _xOffset; set { _xOffset = value; NotifyFrameSummary(); } }
    public int YOffset { get => _yOffset; set { _yOffset = value; NotifyFrameSummary(); } }
    public bool AlphaBlend { get; set; }
    public bool DisposeAfterFrame { get; set; } = true;
    public string FrameSummary => $"{FrameWidth} × {FrameHeight} at {XOffset}, {YOffset} · {DurationMilliseconds} ms";

    public WebpAnimationSourceItem(string fullPath, Bitmap thumbnail, string dimensions, int frameWidth, int frameHeight)
    {
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        Thumbnail = thumbnail;
        Dimensions = dimensions;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
    }

    public WebpAnimationSourceItem(string name, byte[] imageBytes, Bitmap thumbnail, string dimensions,
        int frameWidth, int frameHeight, int durationMilliseconds, bool alphaBlend, bool disposeAfterFrame)
    {
        Name = name;
        ImageBytes = imageBytes;
        Thumbnail = thumbnail;
        Dimensions = dimensions;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        DurationMilliseconds = durationMilliseconds;
        AlphaBlend = alphaBlend;
        DisposeAfterFrame = disposeAfterFrame;
    }

    public WebpAnimationFrameInput ToFrameInput() => new(
        FullPath, ImageBytes, DurationMilliseconds, FrameWidth, FrameHeight,
        XOffset, YOffset, AlphaBlend, DisposeAfterFrame);

    private void NotifyFrameSummary() => PropertyChanged?.Invoke(this,
        new System.ComponentModel.PropertyChangedEventArgs(nameof(FrameSummary)));

    public void Dispose() => Thumbnail.Dispose();
}

public partial class WebpAnimationCreatorWindow : Window
{
    private static readonly string[] SupportedExtensions = [".png", ".webp", ".jpg", ".jpeg", ".bmp", ".ico"];
    private static readonly FilePickerFileType SupportedImageFiles = new("PNG, WebP, JPEG, BMP, and ICO")
    {
        Patterns = ["*.png", "*.webp", "*.jpg", "*.jpeg", "*.bmp", "*.ico"]
    };

    private readonly ImageLibraryService _imageService;
    private readonly IDialogService _dialogs = new AvaloniaDialogService();
    private readonly string _targetDirectory;
    private readonly ObservableCollection<WebpAnimationSourceItem> _sources = new();
    private readonly DispatcherTimer _previewTimer = new();
    private ImageAssetPreview? _preview;
    private Bitmap? _previewBitmap;
    private int _previewFrameIndex;
    private int _previewCompletedPlays;
    private int _previewRepeatCount = 1;
    private int? _previewTotalPlays;
    private bool _updatingFrameEditor;
    private bool _isInitializingXaml = true;
    private double _lockedFrameAspectRatio = 1d;

    public string? CreatedFilePath { get; private set; }

    public WebpAnimationCreatorWindow() : this(CreateDefaultContext()) { }

    private WebpAnimationCreatorWindow((ImageLibraryService Service, string TargetDirectory) context)
        : this(context.Service, context.TargetDirectory) { }

    private static (ImageLibraryService Service, string TargetDirectory) CreateDefaultContext()
    {
        AppSettings settings = AppSettings.LoadSettings();
        string applicationImages = Path.Combine(settings.AppRoot, settings.ImagesFolder);
        string userImages = settings.GetSavedImagesDirectory(applicationImages);
        return (new ImageLibraryService(applicationImages, userImages), userImages);
    }

    public WebpAnimationCreatorWindow(ImageLibraryService imageService, string targetDirectory)
    {
        _imageService = imageService;
        _targetDirectory = targetDirectory;

        
        InitializeComponent();
        _isInitializingXaml = false;
        SourcesList.ItemsSource = _sources;
        _previewTimer.Tick += PreviewTimer_Tick;
        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            ClearPreview();
            var canvasBitmap = CanvasPreviewImage.Source as Bitmap;
            CanvasPreviewImage.Source = null;
            canvasBitmap?.Dispose();
            foreach (var source in _sources)
                source.Dispose();
        };
    }

    public static WebpAnimationCreatorWindow CreateForEditing(
        ImageLibraryService imageService, string targetDirectory, string sourcePath, WebpAnimationEditDocument document)
    {
        var window = new WebpAnimationCreatorWindow(imageService, targetDirectory);
        window.InitializeForEditing(sourcePath, document);
        return window;
    }

    private void InitializeForEditing(string sourcePath, WebpAnimationEditDocument document)
    {
        Title = "Edit animated WebP frames";
        IntroText.Text = $"Edit each frame from {Path.GetFileName(sourcePath)}. Saving creates a new WebP and leaves the source unchanged.";
        EditModeNotice.IsVisible = true;
        WidthBox.Text = document.Width.ToString();
        HeightBox.Text = document.Height.ToString();
        LoopForeverCheckBox.IsChecked = document.RepeatCount == 0;
        LoopCountBox.Text = Math.Max(1, (int)document.RepeatCount).ToString();
        LoopCountBox.IsEnabled = document.RepeatCount != 0;
        MethodBox.Text = "4";
        QualityBox.Text = "100";
        OutputNameBox.Text = Path.GetFileNameWithoutExtension(sourcePath) + "-edited.webp";
        CreateButton.Content = "Save edited copy";

        for (int i = 0; i < document.Frames.Count; i++)
        {
            WebpAnimationEditFrame frame = document.Frames[i];
            using var stream = new MemoryStream(frame.PngBytes);
            var thumbnail = new Bitmap(stream);
            _sources.Add(new WebpAnimationSourceItem(
                $"{Path.GetFileName(sourcePath)} — Frame {i + 1}", frame.PngBytes, thumbnail,
                $"{document.Width} × {document.Height}", document.Width, document.Height,
                frame.DurationMilliseconds, frame.AlphaBlend, frame.DisposeAfterFrame));
        }

        if (_sources.Count > 0)
            SourcesList.SelectedIndex = 0;
        StatusText.Text = $"Loaded {document.Frames.Count} frame(s).";
    }

    private async Task<IStorageFolder?> GetInitialPickerFolderAsync()
    {
        string userImagesDirectory = _imageService.GetRoot(ImageBrowserLocation.UserImages);

        try
        {
            if (Directory.Exists(userImagesDirectory))
            {
                IStorageFolder? userImages = await StorageProvider.TryGetFolderFromPathAsync(userImagesDirectory);
                if (userImages is not null)
                    return userImages;
            }
        }
        catch
        {
            // Fall back to Pictures if the configured User Images folder cannot be opened.
        }

        try
        {
            return await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Pictures);
        }
        catch
        {
            // Use the picker default if the Pictures folder is unavailable too.
            return null;
        }
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add animation frames",
            SuggestedStartLocation = await GetInitialPickerFolderAsync(),
            AllowMultiple = true,
            FileTypeFilter = [SupportedImageFiles]
        });
        AddPaths(files.Select(file => file.TryGetLocalPath()).Where(path => !string.IsNullOrWhiteSpace(path))!);
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        string initialDirectory = _imageService.GetRoot(ImageBrowserLocation.UserImages);
        string? folder = await _dialogs.PickFolderAsync(this, "Select image folder", initialDirectory);
        if (string.IsNullOrWhiteSpace(folder))
            return;

        try
        {
            string[] paths = Directory.EnumerateFiles(folder)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            AddPaths(paths);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not read that folder: {ex.Message}";
        }
    }

    private void AddPaths(System.Collections.Generic.IEnumerable<string?> paths)
    {
        int added = 0;
        int skipped = 0;
        foreach (string? candidate in paths)
        {
            if (string.IsNullOrWhiteSpace(candidate) || _sources.Count >= 300)
            {
                skipped++;
                continue;
            }

            string path = Path.GetFullPath(candidate);
            if (_sources.Any(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var imageInfo = SharpImage.Identify(path) ?? throw new InvalidDataException("Image metadata could not be read.");
                byte[] png = _imageService.CreateExternalThumbnail(path, 64);
                using var stream = new MemoryStream(png);
                if (_sources.Count == 0)
                    InitializeCanvasForFirstImage(imageInfo.Width, imageInfo.Height);
                var size = GetInitialFrameSize(imageInfo.Width, imageInfo.Height);
                var item = new WebpAnimationSourceItem(path, new Bitmap(stream),
                    $"{imageInfo.Width} × {imageInfo.Height}", size.Width, size.Height);
                if (int.TryParse(DefaultDurationBox.Text, out int defaultDuration) && defaultDuration is >= 1 and <= 60000)
                    item.DurationMilliseconds = defaultDuration;
                _sources.Add(item);
                added++;
            }
            catch
            {
                skipped++;
            }
        }

        if (_sources.Count > 0 && SourcesList.SelectedIndex < 0)
            SourcesList.SelectedIndex = 0;
        ClearPreview();
        StatusText.Text = skipped > 0
            ? $"Added {added} image(s); skipped {skipped} unsupported, unreadable, or excess file(s)."
            : $"Added {added} image(s). Sequence order is shown in the list.";
    }

    private void DuplicateSelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedSource;
        if (selected is null || _sources.Count >= 300)
            return;
        using var thumbnailStream = new MemoryStream();
        selected.Thumbnail.Save(thumbnailStream, new PngBitmapEncoderOptions());
        using var cloneStream = new MemoryStream(thumbnailStream.ToArray());
        var clone = new WebpAnimationSourceItem(
            selected.Name + " (copy)", selected.ImageBytes ?? (selected.FullPath is { } path ? File.ReadAllBytes(path) : Array.Empty<byte>()),
            new Bitmap(cloneStream), selected.Dimensions, selected.FrameWidth, selected.FrameHeight,
            selected.DurationMilliseconds, selected.AlphaBlend, selected.DisposeAfterFrame)
        {
            XOffset = selected.XOffset,
            YOffset = selected.YOffset
        };
        _sources.Insert(SourcesList.SelectedIndex + 1, clone);
        SourcesList.SelectedIndex++;
        StatusText.Text = "Duplicated the selected frame and its settings.";
    }

    private void MoveUp_Click(object? sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object? sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int offset)
    {
        int index = SourcesList.SelectedIndex;
        int next = index + offset;
        if (index < 0 || next < 0 || next >= _sources.Count)
            return;
        _sources.Move(index, next);
        SourcesList.SelectedIndex = next;
        ClearPreview();
        StatusText.Text = "Frame order changed. Build preview to review the animation.";
    }

    private void RemoveSelected_Click(object? sender, RoutedEventArgs e)
    {
        int index = SourcesList.SelectedIndex;
        if (index < 0)
            return;
        _sources[index].Dispose();
        _sources.RemoveAt(index);
        if (_sources.Count > 0)
            SourcesList.SelectedIndex = Math.Min(index, _sources.Count - 1);
        ClearPreview();
        StatusText.Text = $"{_sources.Count} frame(s) in sequence.";
    }

    private void InitializeCanvasForFirstImage(int imageWidth, int imageHeight)
    {
        double scale = 480d / Math.Max(imageWidth, imageHeight);
        WidthBox.Text = Math.Max(1, (int)Math.Round(imageWidth * scale)).ToString();
        HeightBox.Text = Math.Max(1, (int)Math.Round(imageHeight * scale)).ToString();
    }

    private (int Width, int Height) GetInitialFrameSize(int imageWidth, int imageHeight)
    {
        int canvasWidth = int.TryParse(WidthBox.Text, out int parsedWidth) ? Math.Clamp(parsedWidth, 1, 8192) : 480;
        int canvasHeight = int.TryParse(HeightBox.Text, out int parsedHeight) ? Math.Clamp(parsedHeight, 1, 8192) : 480;
        double scale = FitNewImagesCheckBox.IsChecked == true
            ? Math.Min((double)canvasWidth / imageWidth, (double)canvasHeight / imageHeight)
            : Math.Min(1d, Math.Min((double)canvasWidth / imageWidth, (double)canvasHeight / imageHeight));
        return (Math.Max(1, (int)Math.Round(imageWidth * scale)), Math.Max(1, (int)Math.Round(imageHeight * scale)));
    }

    private void CanvasSize_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isInitializingXaml)
            return;
        RefreshCanvasPreview();
    }

    private void KeepFrameAspectCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_isInitializingXaml)
            return;
        if (KeepFrameAspectCheckBox.IsChecked == true &&
            int.TryParse(FrameWidthBox.Text, out int width) && width > 0 &&
            int.TryParse(FrameHeightBox.Text, out int height) && height > 0)
            _lockedFrameAspectRatio = (double)width / height;
    }

    private void FrameGeometry_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isInitializingXaml || _updatingFrameEditor)
            return;
        var selected = SelectedSource;
        if (selected is null)
            return;

        if (ReferenceEquals(sender, FrameWidthBox) && KeepFrameAspectCheckBox.IsChecked == true &&
            int.TryParse(FrameWidthBox.Text, out int width) && width > 0 && _lockedFrameAspectRatio > 0)
        {
            _updatingFrameEditor = true;
            FrameHeightBox.Text = Math.Max(1, (int)Math.Round(width / _lockedFrameAspectRatio)).ToString();
            _updatingFrameEditor = false;
        }
        else if (ReferenceEquals(sender, FrameHeightBox) && KeepFrameAspectCheckBox.IsChecked == true &&
                 int.TryParse(FrameHeightBox.Text, out int height) && height > 0 && _lockedFrameAspectRatio > 0)
        {
            _updatingFrameEditor = true;
            FrameWidthBox.Text = Math.Max(1, (int)Math.Round(height * _lockedFrameAspectRatio)).ToString();
            _updatingFrameEditor = false;
        }

        if (int.TryParse(FrameWidthBox.Text, out int fw)) selected.FrameWidth = fw;
        if (int.TryParse(FrameHeightBox.Text, out int fh)) selected.FrameHeight = fh;
        if (int.TryParse(FrameXBox.Text, out int x)) selected.XOffset = x;
        if (int.TryParse(FrameYBox.Text, out int y)) selected.YOffset = y;
        RefreshCanvasPreview();
    }

    private WebpAnimationSourceItem? SelectedSource => SourcesList.SelectedItem as WebpAnimationSourceItem;

    private void FrameSettings_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isInitializingXaml || _updatingFrameEditor || SelectedSource is not { } selected)
            return;
        if (int.TryParse(FrameDurationBox.Text, out int duration))
            selected.DurationMilliseconds = duration;
    }

    private void FrameSettings_SelectionChanged(object? sender, RoutedEventArgs e)
    {
        if (_isInitializingXaml || _updatingFrameEditor || SelectedSource is not { } selected)
            return;
        selected.AlphaBlend = BlendModeBox.SelectedIndex == 1;
        selected.DisposeAfterFrame = DisposeAfterFrameCheckBox.IsChecked == true;
    }

    private void SourcesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingXaml)
            return;
        LoadSelectedFrameIntoEditor();
    }

    private void LoadSelectedFrameIntoEditor()
    {
        var selected = SelectedSource;
        bool enabled = selected is not null;
        FrameWidthBox.IsEnabled = FrameHeightBox.IsEnabled = FrameXBox.IsEnabled = FrameYBox.IsEnabled = enabled;
        FrameDurationBox.IsEnabled = BlendModeBox.IsEnabled = DisposeAfterFrameCheckBox.IsEnabled = enabled;
        if (selected is null)
        {
            var previous = CanvasPreviewImage.Source as Bitmap;
            CanvasPreviewImage.Source = null;
            previous?.Dispose();
            CanvasPreviewInfo.Text = "Select a frame to preview its canvas placement.";
            return;
        }

        _updatingFrameEditor = true;
        FrameWidthBox.Text = selected.FrameWidth.ToString();
        FrameHeightBox.Text = selected.FrameHeight.ToString();
        FrameXBox.Text = selected.XOffset.ToString();
        FrameYBox.Text = selected.YOffset.ToString();
        FrameDurationBox.Text = selected.DurationMilliseconds.ToString();
        BlendModeBox.SelectedIndex = selected.AlphaBlend ? 1 : 0;
        DisposeAfterFrameCheckBox.IsChecked = selected.DisposeAfterFrame;
        _lockedFrameAspectRatio = selected.FrameHeight == 0 ? 1 : (double)selected.FrameWidth / selected.FrameHeight;
        _updatingFrameEditor = false;
        RefreshCanvasPreview();
    }

    private void RefreshCanvasPreview()
    {
        var selected = SelectedSource;
        if (selected is null || !int.TryParse(WidthBox.Text, out int canvasWidth) ||
            !int.TryParse(HeightBox.Text, out int canvasHeight) || canvasWidth < 1 || canvasHeight < 1)
            return;
        try
        {
            byte[] png = _imageService.CreateWebpAnimationFramePreview(selected.ToFrameInput(), canvasWidth, canvasHeight);
            using var stream = new MemoryStream(png);
            var bitmap = new Bitmap(stream);
            var previous = CanvasPreviewImage.Source as Bitmap;
            CanvasPreviewImage.Source = bitmap;
            if (!ReferenceEquals(previous, _previewBitmap))
                previous?.Dispose();
            CanvasPreviewInfo.Text = $"Canvas {canvasWidth} × {canvasHeight} · frame at {selected.XOffset}, {selected.YOffset}";
        }
        catch
        {
            CanvasPreviewInfo.Text = "Frame size and offset must fit inside the canvas.";
        }
    }

    private void LoopForeverCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_isInitializingXaml)
            return;
        LoopCountBox.IsEnabled = LoopForeverCheckBox.IsChecked != true;
    }

    private async void BuildPreview_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryReadOptions(out var settings))
            return;
        SetBusy(true);
        try
        {
            byte[] encoded = await Task.Run(() => _imageService.CreateWebpAnimationFromFrames(
                _sources.Select(source => source.ToFrameInput()).ToArray(), settings.Width, settings.Height,
                settings.LoopForever, settings.LoopCount, settings.Method, settings.Quality, settings.Duration));
            ImageAssetPreview preview = await Task.Run(() => _imageService.CreateWebpAnimationPreview(encoded));
            SetPreview(preview, settings.LoopCount);
            StatusText.Text = $"Preview ready: {preview.FrameCount} frames at {preview.Width} × {preview.Height}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not build preview: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CreateButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryReadOptions(out var settings))
            return;
        string fileName = OutputNameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            StatusText.Text = "Enter an output filename.";
            return;
        }
        if (!fileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            fileName += ".webp";
        fileName = Path.GetFileName(fileName);

        SetBusy(true);
        try
        {
            byte[] bytes = await Task.Run(() => _imageService.CreateWebpAnimationFromFrames(
                _sources.Select(source => source.ToFrameInput()).ToArray(), settings.Width, settings.Height,
                settings.LoopForever, settings.LoopCount, settings.Method, settings.Quality, settings.Duration));
            string savedPath = await Task.Run(() => _imageService.SaveToUserImages(_targetDirectory, fileName, bytes));
            CreatedFilePath = savedPath;
            StatusText.Text = $"Created {Path.GetFileName(savedPath)} in User Images.";
            Close(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not create animation: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool TryReadOptions(out (int Width, int Height, bool LoopForever, int LoopCount, int Method, int Quality, int Duration) settings)
    {
        settings = default;
        if (_sources.Count < 2)
        {
            StatusText.Text = "Add at least two readable image files.";
            return false;
        }
        if (!TryParse(WidthBox.Text, 1, 8192, "Canvas width", out int width) ||
            !TryParse(HeightBox.Text, 1, 8192, "Canvas height", out int height) ||
            !TryParse(DefaultDurationBox.Text, 1, 60000, "Default frame duration", out int duration) ||
            !TryParse(MethodBox.Text, 0, 6, "Method", out int method) ||
            !TryParse(QualityBox.Text, 1, 100, "Quality", out int quality))
            return false;

        foreach (var frame in _sources)
        {
            if (frame.FrameWidth is < 1 || frame.FrameHeight is < 1 || frame.FrameWidth > width || frame.FrameHeight > height ||
                frame.XOffset < 0 || frame.YOffset < 0 || frame.XOffset + frame.FrameWidth > width || frame.YOffset + frame.FrameHeight > height)
            {
                StatusText.Text = $"Frame '{frame.Name}' must have a positive size and fit within the canvas at its current offset.";
                return false;
            }
            if (frame.DurationMilliseconds is < 1 or > 60000)
            {
                StatusText.Text = $"Frame '{frame.Name}' duration must be between 1 and 60,000 milliseconds.";
                return false;
            }
        }

        bool loopForever = LoopForeverCheckBox.IsChecked == true;
        int loopCount = 1;
        if (!loopForever && !TryParse(LoopCountBox.Text, 1, 10000, "Repeat count", out loopCount))
            return false;
        if ((long)width * height * _sources.Count > 50_000_000)
        {
            StatusText.Text = "The animation exceeds the limit of 50 million total output frame-pixels. Reduce the dimensions or number of frames.";
            return false;
        }

        settings = (width, height, loopForever, loopCount, method, quality, duration);
        return true;
    }

    private bool TryParse(string? text, int minimum, int maximum, string label, out int value)
    {
        if (int.TryParse(text, out value) && value >= minimum && value <= maximum)
            return true;
        StatusText.Text = $"{label} must be a whole number from {minimum} to {maximum}.";
        return false;
    }

    private void SetPreview(ImageAssetPreview preview, int loopCount)
    {
        StopPreview();
        _preview = preview;
        _previewRepeatCount = loopCount;
        _previewFrameIndex = 0;
        _previewCompletedPlays = 0;
        UpdatePreviewLoopMode();
        PreviewControls.IsVisible = preview.AnimationFrames.Count > 1;
        ShowPreviewFrame();
    }

    private void PreviewLoopForeverCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_isInitializingXaml)
            return;

        _previewCompletedPlays = 0;
        _previewFrameIndex = 0;
        UpdatePreviewLoopMode();
        ShowPreviewFrame();
    }

    private void UpdatePreviewLoopMode()
    {
        _previewTotalPlays = PreviewLoopForeverCheckBox.IsChecked == true
            ? null
            : _previewRepeatCount + 1;
    }

    private void ShowPreviewFrame()
    {
        if (_preview is not { AnimationFrames.Count: > 0 } preview)
            return;
        var frame = preview.AnimationFrames[_previewFrameIndex];
        _previewBitmap?.Dispose();
        using var stream = new MemoryStream(frame.PngBytes);
        _previewBitmap = new Bitmap(stream);
        AnimationPreviewImage.Source = _previewBitmap;
        PreviewFrameText.Text = $"{_previewFrameIndex + 1} / {preview.AnimationFrames.Count}";
        _previewTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(frame.DurationMilliseconds <= 0 ? 100 : frame.DurationMilliseconds, 20, 10000));
    }

    private void MovePreviewFrame(int offset)
    {
        if (_preview is not { AnimationFrames.Count: > 1 } preview)
            return;
        _previewFrameIndex = (_previewFrameIndex + offset + preview.AnimationFrames.Count) % preview.AnimationFrames.Count;
        ShowPreviewFrame();
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (_preview is not { AnimationFrames.Count: > 1 } preview)
            return;
        if (_previewFrameIndex == preview.AnimationFrames.Count - 1 && _previewTotalPlays is int totalPlays)
        {
            _previewCompletedPlays++;
            if (_previewCompletedPlays >= totalPlays)
            {
                StopPreview();
                return;
            }
        }
        MovePreviewFrame(1);
    }
    private void PreviousPreviewFrame_Click(object? sender, RoutedEventArgs e) => MovePreviewFrame(-1);
    private void NextPreviewFrame_Click(object? sender, RoutedEventArgs e) => MovePreviewFrame(1);

    private void PreviewPlayButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_preview is not { AnimationFrames.Count: > 1 })
            return;
        if (_previewTimer.IsEnabled)
        {
            StopPreview();
            return;
        }
        if (_previewTotalPlays is int totalPlays && _previewCompletedPlays >= totalPlays)
        {
            _previewCompletedPlays = 0;
            _previewFrameIndex = 0;
            ShowPreviewFrame();
        }
        PreviewPlayButton.Content = "Pause";
        _previewTimer.Start();
    }

    private void StopPreview()
    {
        _previewTimer.Stop();
        if (PreviewPlayButton is not null)
            PreviewPlayButton.Content = "Play";
    }

    private void ClearPreview()
    {
        StopPreview();
        _preview = null;
        PreviewControls.IsVisible = false;
        AnimationPreviewImage.Source = null;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
    }

    private void SetBusy(bool busy)
    {
        foreach (Control control in new Control[]
        {
            SourcesList, AddFilesButton, AddFolderButton, DuplicateButton, MoveUpButton, MoveDownButton, RemoveButton,
            WidthBox, HeightBox, FitNewImagesCheckBox, DefaultDurationBox, LoopForeverCheckBox, LoopCountBox,
            MethodBox, QualityBox, OutputNameBox, BuildPreviewButton, CreateButton
        })
            control.IsEnabled = !busy;
        bool hasFrame = SelectedSource is not null;
        FrameWidthBox.IsEnabled = FrameHeightBox.IsEnabled = FrameXBox.IsEnabled = FrameYBox.IsEnabled = !busy && hasFrame;
        FrameDurationBox.IsEnabled = BlendModeBox.IsEnabled = DisposeAfterFrameCheckBox.IsEnabled = !busy && hasFrame;
        LoopCountBox.IsEnabled = !busy && LoopForeverCheckBox.IsChecked != true;
        StatusText.Text = busy ? "Working…" : StatusText.Text;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}

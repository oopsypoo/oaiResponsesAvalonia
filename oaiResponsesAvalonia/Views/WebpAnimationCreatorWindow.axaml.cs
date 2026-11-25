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

public sealed class WebpAnimationSourceItem : IDisposable
{
    public string? FullPath { get; }
    public string Name { get; }
    public string Dimensions { get; }
    public Bitmap Thumbnail { get; }
    public byte[]? ImageBytes { get; }
    public int? DurationMilliseconds { get; }

    public WebpAnimationSourceItem(string fullPath, Bitmap thumbnail, string dimensions)
    {
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        Thumbnail = thumbnail;
        Dimensions = dimensions;
    }

    public WebpAnimationSourceItem(string name, byte[] imageBytes, Bitmap thumbnail, string dimensions, int durationMilliseconds)
    {
        Name = name;
        ImageBytes = imageBytes;
        Thumbnail = thumbnail;
        Dimensions = dimensions;
        DurationMilliseconds = durationMilliseconds;
    }

    public WebpAnimationFrameInput ToFrameInput() => new(FullPath, ImageBytes, DurationMilliseconds);

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
    private readonly string _targetDirectory;
    private readonly ObservableCollection<WebpAnimationSourceItem> _sources = new();
    private readonly DispatcherTimer _previewTimer = new();
    private ImageAssetPreview? _preview;
    private Bitmap? _previewBitmap;
    private int _previewFrameIndex;
    private int _previewCompletedPlays;
    private int? _previewTotalPlays;
    private bool _updatingAspectRatio;
    private bool _isEditingAnimation;

    private double _lockedAspectRatio = 1d;

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
        InitializeComponent();
        _imageService = imageService;
        _targetDirectory = targetDirectory;
        SourcesList.ItemsSource = _sources;
        _previewTimer.Tick += PreviewTimer_Tick;
        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            ClearPreview();
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
        IntroText.Text = $"Edit the frame sequence from {Path.GetFileName(sourcePath)}. Existing frame timings are preserved; new images use the shared duration below.";
        EditModeNotice.IsVisible = true;
        _isEditingAnimation = true;
        FrameDurationBox.IsEnabled = false;

        _lockedAspectRatio = (double)document.Width / document.Height;
        WidthBox.Text = document.Width.ToString();
        HeightBox.Text = document.Height.ToString();
        KeepAspectRatioCheckBox.IsChecked = true;
        LoopForeverCheckBox.IsChecked = document.RepeatCount == 0;
        LoopCountBox.Text = Math.Max(1, (int)document.RepeatCount).ToString();
        LoopCountBox.IsEnabled = document.RepeatCount != 0;
        FrameDurationBox.Text = "100";
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
                $"{Path.GetFileName(sourcePath)} — Frame {i + 1} ({frame.DurationMilliseconds} ms)",
                frame.PngBytes, thumbnail, $"{document.Width} × {document.Height}", frame.DurationMilliseconds));
        }

        if (_sources.Count > 0)
            SourcesList.SelectedIndex = 0;
        StatusText.Text = $"Loaded {document.Frames.Count} frame(s). Saving creates a new WebP and leaves the source unchanged.";
    }

    private async Task<IStorageFolder?> GetInitialPickerFolderAsync()
    {
        try
        {
            if (Directory.Exists(_targetDirectory))
            {
                IStorageFolder? userImages = await StorageProvider.TryGetFolderFromPathAsync(_targetDirectory);
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
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add images from folder",
            SuggestedStartLocation = await GetInitialPickerFolderAsync(),
            AllowMultiple = false
        });
        string? folder = folders.FirstOrDefault()?.TryGetLocalPath();
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
                _sources.Add(new WebpAnimationSourceItem(path, new Bitmap(stream), $"{imageInfo.Width} × {imageInfo.Height}"));
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
        int canvasWidth = Math.Max(1, (int)Math.Round(imageWidth * scale));
        int canvasHeight = Math.Max(1, (int)Math.Round(imageHeight * scale));

        _lockedAspectRatio = (double)imageWidth / imageHeight;
        _updatingAspectRatio = true;
        try
        {
            WidthBox.Text = canvasWidth.ToString();
            HeightBox.Text = canvasHeight.ToString();
            KeepAspectRatioCheckBox.IsChecked = true;
        }
        finally
        {
            _updatingAspectRatio = false;
        }
    }

    private void KeepAspectRatioCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_updatingAspectRatio && KeepAspectRatioCheckBox.IsChecked == true &&
            int.TryParse(WidthBox.Text, out int width) && width > 0 &&
            int.TryParse(HeightBox.Text, out int height) && height > 0)
        {
            _lockedAspectRatio = (double)width / height;
        }
    }

    private void SizeBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingAspectRatio || KeepAspectRatioCheckBox?.IsChecked != true ||
            !double.IsFinite(_lockedAspectRatio) || _lockedAspectRatio <= 0)
            return;

        if (!int.TryParse(WidthBox.Text, out int width) || width <= 0 ||
            !int.TryParse(HeightBox.Text, out int height) || height <= 0)
            return;

        _updatingAspectRatio = true;
        try
        {
            if (ReferenceEquals(sender, WidthBox))
                HeightBox.Text = Math.Clamp((int)Math.Round(width / _lockedAspectRatio), 1, 8192).ToString();
            else if (ReferenceEquals(sender, HeightBox))
                WidthBox.Text = Math.Clamp((int)Math.Round(height * _lockedAspectRatio), 1, 8192).ToString();
        }
        finally
        {
            _updatingAspectRatio = false;
        }
    }

    private void LoopForeverCheckBox_Changed(object? sender, RoutedEventArgs e) =>
        LoopCountBox.IsEnabled = LoopForeverCheckBox.IsChecked != true;

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
            SetPreview(preview, settings.LoopForever, settings.LoopCount);
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
        if (!TryParse(WidthBox.Text, 1, 8192, "Width", out int width) ||
            !TryParse(HeightBox.Text, 1, 8192, "Height", out int height) ||
            !TryParse(FrameDurationBox.Text, 1, 60000, "Frame duration", out int duration) ||
            !TryParse(MethodBox.Text, 0, 6, "Method", out int method) ||
            !TryParse(QualityBox.Text, 1, 100, "Quality", out int quality))
            return false;

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

    private void SetPreview(ImageAssetPreview preview, bool loopForever, int loopCount)
    {
        StopPreview();
        _preview = preview;
        _previewFrameIndex = 0;
        _previewCompletedPlays = 0;
        _previewTotalPlays = loopForever ? null : loopCount + 1;
        PreviewControls.IsVisible = preview.AnimationFrames.Count > 1;
        ShowPreviewFrame();
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

    private void SourcesList_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

    private void SetBusy(bool busy)
    {
        foreach (Control control in new Control[]
        {
            SourcesList, AddFilesButton, AddFolderButton, MoveUpButton, MoveDownButton, RemoveButton,
            WidthBox, HeightBox, KeepAspectRatioCheckBox, LoopForeverCheckBox, MethodBox, QualityBox,
            OutputNameBox, BuildPreviewButton, CreateButton
        })
            control.IsEnabled = !busy;
        LoopCountBox.IsEnabled = !busy && LoopForeverCheckBox.IsChecked != true;
        FrameDurationBox.IsEnabled = !busy && !_isEditingAnimation;
        StatusText.Text = busy ? "Working…" : StatusText.Text;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}

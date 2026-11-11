using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using oaiResponsesAvalonia;
using oaiResponsesAvalonia.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Views;

public partial class ImageGalleryWindow : Window
{
    private static readonly FilePickerFileType SupportedImageFiles = new("PNG, WebP, JPEG, BMP, and ICO")
    {
        Patterns = ["*.png", "*.webp", "*.jpg", "*.jpeg", "*.bmp", "*.ico"]
    };

    private readonly ImageLibraryService _imageService;
    private readonly ObservableCollection<ImageGalleryItem> _entries = new();
    private ImageBrowserLocation _location = ImageBrowserLocation.ApplicationImages;
    private string _currentDirectory;
    private string? _selectedImagePath;
    private string _saveDirectory;
    private ImageAssetPreview? _selectedPreview;
    private Bitmap? _previewBitmap;
    private Bitmap? _editorBitmap;
    private bool _initializing;
    private Rect? _cropSelection;
    private CropDragMode _cropDragMode;
    private Point _cropDragStart;
    private Point _cropDragImageStart;
    private Rect _cropDragSelectionStart;
    private Point _imageDisplayOrigin;
    private double _imageDisplayScaleX;
    private double _imageDisplayScaleY;
    private bool _updatingCropFields;
    private bool _showingProcessedPreview;
    private bool _cropApplied;
    private int _previewUpdateVersion;
    private Border? _cropSelectionVisual;
    private readonly System.Collections.Generic.List<Border> _cropHandles = new();

    public ImageGalleryWindow() : this(GetConfiguredPaths()) { }

    private ImageGalleryWindow((string ApplicationImages, string UserImages) paths)
        : this(paths.ApplicationImages, paths.UserImages) { }

    private static (string ApplicationImages, string UserImages) GetConfiguredPaths()
    {
        AppSettings settings = AppSettings.LoadSettings();
        string applicationImages = Path.Combine(settings.AppRoot, settings.ImagesFolder);
        return (applicationImages, settings.GetSavedImagesDirectory(applicationImages));
    }

    public ImageGalleryWindow(string applicationImagesDirectory, string userImagesDirectory)
    {
        _initializing = true;
        InitializeComponent();
        InitializeCropOverlay();
        _imageService = new ImageLibraryService(applicationImagesDirectory, userImagesDirectory);
        _currentDirectory = _imageService.GetRoot(_location);
        _saveDirectory = _imageService.GetRoot(ImageBrowserLocation.UserImages);
        EntriesList.ItemsSource = _entries;
        _initializing = true;
        LocationPicker.SelectedIndex = 0;
        _initializing = false;
        RefreshEntries();
    }

    public void UpdateRoots(string applicationImagesDirectory, string userImagesDirectory)
    {
        _imageService.SetRoots(applicationImagesDirectory, userImagesDirectory);
        _currentDirectory = _imageService.GetRoot(_location);
        _saveDirectory = _imageService.GetRoot(ImageBrowserLocation.UserImages);
        _selectedImagePath = null;
        _selectedPreview = null;
        RefreshEntries();
    }

    private void RefreshEntries() => _ = RefreshEntriesAsync();

    private async Task RefreshEntriesAsync()
    {
        try
        {
            string currentPath = _imageService.GetSafeCurrentDirectory(_location, _currentDirectory);
            _currentDirectory = currentPath;
            UpdateWindowTitle(currentPath);
            UpdateMenuAvailability();
            UpButton.IsEnabled = !PathsEqual(currentPath, _imageService.GetRoot(_location));
            StatusText.Text = "Loading images…";

            string category = SelectedTag(CategoryPicker, "responses");
            var loaded = await Task.Run(() => _imageService.ListEntries(_location, currentPath)
                .Where(entry => entry.IsDirectory || MatchesCategory(entry.Name, category))
                .Select(entry => new LoadedImageGalleryItem(entry,
                    entry.IsDirectory ? null : TryCreateThumbnail(entry.FullPath)))
                .ToArray());

            DisposeThumbnails();
            _entries.Clear();
            EntriesList.SelectedItem = null;
            if (!EditorPanel.IsVisible)
            {
                _selectedImagePath = null;
                _selectedPreview = null;
            }
            UpdateMenuAvailability();
            foreach (var item in loaded)
            {
                Bitmap? thumbnail = item.ThumbnailPng is null ? null : BitmapFromBytes(item.ThumbnailPng);
                _entries.Add(new ImageGalleryItem(item.Entry, thumbnail));
            }

            PreviewImage.Source = null;
            _previewBitmap?.Dispose();
            _previewBitmap = null;
            PreviewDetailsText.Text = _entries.Count == 0 ? "This folder contains no supported images or subfolders." : "Select an image to preview it.";
            EditButton.IsEnabled = false;
            StatusText.Text = $"{_entries.Count} item(s). Supported formats: PNG, WebP, JPEG, BMP, ICO.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not browse this folder: {ex.Message}";
        }
    }

    private void UpdateWindowTitle(string currentPath)
    {
        string relativePath = Path.GetRelativePath(_imageService.GetRoot(_location), currentPath);
        string logicalRoot = _location == ImageBrowserLocation.UserImages
            ? "$AppRoot/images/user"
            : "$AppRoot/images";
        string displayPath = relativePath == "."
            ? logicalRoot
            : $"{logicalRoot}/{relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/')}";
        Title = $"Images — {displayPath}";
    }

    private void UpdateMenuAvailability()
    {
        if (NewFolderMenuItem is null)
            return;

        bool inUserImages = _location == ImageBrowserLocation.UserImages;
        SaveAsMenuItem.IsEnabled = EditorPanel.IsVisible && _selectedImagePath is not null;
        NewFolderMenuItem.IsEnabled = inUserImages;
        DeleteFileMenuItem.IsEnabled = inUserImages && EntriesList.SelectedItem is ImageGalleryItem file && !file.IsDirectory;
        DeleteFolderMenuItem.IsEnabled = inUserImages && EntriesList.SelectedItem is ImageGalleryItem folder && folder.IsDirectory;
    }

    private byte[]? TryCreateThumbnail(string path)
    {
        try { return _imageService.CreateThumbnail(path); }
        catch { return null; }
    }

    private void LocationPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LocationPicker.SelectedIndex < 0)
            return;
        _location = LocationPicker.SelectedIndex == 0 ? ImageBrowserLocation.ApplicationImages : ImageBrowserLocation.UserImages;
        _currentDirectory = _imageService.GetRoot(_location);
        _initializing = true;
        CategoryPicker.SelectedIndex = _location == ImageBrowserLocation.ApplicationImages ? 0 : 2;
        _initializing = false;
        RefreshEntries();
    }

    private void CategoryPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
            RefreshEntries();
    }

    private static bool MatchesCategory(string fileName, string category) => category switch
    {
        "responses" => fileName.StartsWith("resp_", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("resp-", StringComparison.OrdinalIgnoreCase),
        "user" => fileName.StartsWith("user_", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("user-", StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    private async void OpenUserImagesFolderMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                StatusText.Text = "Could not open the user images folder.";
                return;
            }

            var folder = new DirectoryInfo(_imageService.GetRoot(ImageBrowserLocation.UserImages));
            bool opened = await topLevel.Launcher.LaunchDirectoryInfoAsync(folder);
            if (!opened)
                StatusText.Text = "The system could not open the user images folder.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open the user images folder: {ex.Message}";
        }
    }

    private void UpButton_Click(object? sender, RoutedEventArgs e)
    {
        string root = _imageService.GetRoot(_location);
        string? parent = Directory.GetParent(_currentDirectory)?.FullName;
        if (parent is not null && IsWithin(root, parent))
            _currentDirectory = parent;
        else
            _currentDirectory = root;
        RefreshEntries();
    }

    private async void EntriesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateMenuAvailability();
        if (EditorPanel.IsVisible)
            return;

        if (EntriesList.SelectedItem is not ImageGalleryItem item || item.IsDirectory)
        {
            _selectedImagePath = null;
            _selectedPreview = null;
            EditButton.IsEnabled = false;
            PreviewImage.Source = null;
            PreviewDetailsText.Text = EntriesList.SelectedItem is ImageGalleryItem folder
                ? $"Folder: {folder.Name}. Open the selected folder to browse it."
                : "Select an image to preview it.";
            return;
        }

        _selectedImagePath = item.FullPath;
        EditButton.IsEnabled = true;
        PreviewDetailsText.Text = "Loading preview…";
        try
        {
            var result = await Task.Run(() => _imageService.CreatePreview(item.FullPath));
            if (!string.Equals(_selectedImagePath, item.FullPath, StringComparison.Ordinal))
                return;
            _selectedPreview = result;
            SetPreviewBitmap(result.PngBytes, PreviewImage, ref _previewBitmap);
            PreviewDetailsText.Text = $"{item.Name}\n{result.Width} × {result.Height} · {result.Format} · {result.FrameCount} frame(s)\nTransparency: {(result.HasTransparency ? "present" : "not detected")}";
        }
        catch (Exception ex)
        {
            PreviewDetailsText.Text = $"Could not preview image: {ex.Message}";
        }
    }

    private void EntriesList_DoubleTapped(object? sender, TappedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (EntriesList.SelectedItem is not ImageGalleryItem item)
            return;
        if (item.IsDirectory)
        {
            try
            {
                _currentDirectory = _imageService.GetSafeCurrentDirectory(_location, item.FullPath);
                RefreshEntries();
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            return;
        }
        OpenEditor(item);
    }

    private void EditButton_Click(object? sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is ImageGalleryItem item && !item.IsDirectory)
            OpenEditor(item);
    }

    private async void ImportButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import an image into User Images",
                AllowMultiple = true,
                FileTypeFilter = [SupportedImageFiles]
            });
            if (files.Count == 0)
                return;

            string destinationDirectory = _location == ImageBrowserLocation.UserImages
                ? _imageService.GetSafeCurrentDirectory(_location, _currentDirectory)
                : _imageService.GetRoot(ImageBrowserLocation.UserImages);
            foreach (var file in files)
            {
                string? sourcePath = file.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(sourcePath))
                    _imageService.ImportToUserImages(sourcePath, destinationDirectory);
            }

            if (_location == ImageBrowserLocation.UserImages)
                RefreshEntries();
            StatusText.Text = $"Imported {files.Count} image(s) into User Images.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
        }
    }

    private async void NewFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_location != ImageBrowserLocation.UserImages)
            return;
        var nameBox = new TextBox { PlaceholderText = "Folder name", MinWidth = 280, Margin = new Avalonia.Thickness(0, 8, 0, 12) };
        var createButton = new Button { Content = "Create", IsDefault = true, MinWidth = 80 };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var prompt = new Window
        {
            Title = "Create folder in User Images",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Children =
                {
                    new TextBlock { Text = "Create a subfolder in the current User Images folder." },
                    nameBox,
                    buttons
                }
            }
        };
        bool create = false;
        createButton.Click += (_, _) => { create = true; prompt.Close(); };
        cancelButton.Click += (_, _) => prompt.Close();
        buttons.Children.Add(createButton);
        buttons.Children.Add(cancelButton);
        await prompt.ShowDialog(this);
        if (!create)
            return;
        try
        {
            _imageService.CreateUserFolder(_currentDirectory, nameBox.Text ?? string.Empty);
            RefreshEntries();
            StatusText.Text = "Folder created.";
        }
        catch (Exception ex) { StatusText.Text = $"Could not create folder: {ex.Message}"; }
    }

    private async void DeleteFileMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (_location != ImageBrowserLocation.UserImages || EntriesList.SelectedItem is not ImageGalleryItem item || item.IsDirectory)
            return;
        if (!await ConfirmActionAsync("Delete image", $"Permanently delete '{item.Name}'?", "Delete"))
            return;

        try
        {
            await Task.Run(() => _imageService.DeleteUserFile(item.FullPath));
            RefreshEntries();
            StatusText.Text = $"Deleted image: {item.Name}";
        }
        catch (Exception ex) { StatusText.Text = $"Could not delete image: {ex.Message}"; }
    }

    private async void DeleteFolderMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (_location != ImageBrowserLocation.UserImages || EntriesList.SelectedItem is not ImageGalleryItem item || !item.IsDirectory)
            return;
        if (!await ConfirmActionAsync("Delete folder", $"Permanently delete '{item.Name}' and everything inside it?", "Delete folder"))
            return;

        try
        {
            await Task.Run(() => _imageService.DeleteUserFolder(item.FullPath));
            RefreshEntries();
            StatusText.Text = $"Deleted folder: {item.Name}";
        }
        catch (Exception ex) { StatusText.Text = $"Could not delete folder: {ex.Message}"; }
    }

    private async Task<bool> ConfirmActionAsync(string title, string message, string confirmLabel)
    {
        bool confirmed = false;
        var confirmButton = new Button { Content = confirmLabel, IsDefault = true, MinWidth = 90 };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    buttons
                }
            }
        };
        confirmButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancelButton.Click += (_, _) => dialog.Close();
        buttons.Children.Add(confirmButton);
        buttons.Children.Add(cancelButton);
        await dialog.ShowDialog(this);
        return confirmed;
    }

    private void RefreshMenuItem_Click(object? sender, RoutedEventArgs e) => RefreshEntries();

    private void ExitMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void SaveAsMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (!EditorPanel.IsVisible || _selectedImagePath is null || _selectedPreview is null)
            return;

        try
        {
            var dialog = new SaveAsWindow(
                _imageService,
                _selectedImagePath,
                _saveDirectory,
                Path.GetFileNameWithoutExtension(_selectedImagePath),
                ReadProcessingOptions(),
                _selectedPreview.HasTransparency);
            string? savedPath = await dialog.ShowDialog<string?>(this);
            if (savedPath is null)
                return;

            if (_location == ImageBrowserLocation.UserImages)
                await RefreshEntriesAsync();
            StatusText.Text = $"Saved copy: {savedPath}";
        }
        catch (Exception ex) { StatusText.Text = $"Save failed: {ex.Message}"; }
    }

    private void OpenEditor(ImageGalleryItem item)
    {
        if (_selectedPreview is null || !string.Equals(item.FullPath, _selectedImagePath, StringComparison.Ordinal))
        {
            _selectedImagePath = item.FullPath;
            _ = LoadPreviewThenOpenEditorAsync(item);
            return;
        }
        ShowEditor(item);
    }

    private async Task LoadPreviewThenOpenEditorAsync(ImageGalleryItem item)
    {
        try
        {
            _selectedPreview = await Task.Run(() => _imageService.CreatePreview(item.FullPath));
            _selectedImagePath = item.FullPath;
            ShowEditor(item);
        }
        catch (Exception ex) { StatusText.Text = $"Could not open editor: {ex.Message}"; }
    }

    private void InitializeCropOverlay()
    {
        _cropSelectionVisual = new Border
        {
            BorderBrush = Brushes.Gold,
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(28, 255, 213, 79)),
            IsHitTestVisible = false
        };
        CropOverlayCanvas.Children.Add(_cropSelectionVisual);

        for (int i = 0; i < 8; i++)
        {
            var handle = new Border
            {
                Width = 10,
                Height = 10,
                Background = Brushes.White,
                BorderBrush = Brushes.Gold,
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };
            _cropHandles.Add(handle);
            CropOverlayCanvas.Children.Add(handle);
        }
    }

    private void CropOverlayCanvas_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateCropOverlay();

    private void UpdateCropOverlay()
    {
        if (_selectedPreview is null || CropOverlayCanvas.Bounds.Width <= 0 || CropOverlayCanvas.Bounds.Height <= 0)
            return;

        Size bitmapSize = _editorBitmap?.Size ?? new Size(_selectedPreview.Width, _selectedPreview.Height);
        double bitmapScale = Math.Min(CropOverlayCanvas.Bounds.Width / bitmapSize.Width,
            CropOverlayCanvas.Bounds.Height / bitmapSize.Height);
        double displayedWidth = bitmapSize.Width * bitmapScale;
        double displayedHeight = bitmapSize.Height * bitmapScale;
        _imageDisplayOrigin = new Point((CropOverlayCanvas.Bounds.Width - displayedWidth) / 2,
            (CropOverlayCanvas.Bounds.Height - displayedHeight) / 2);
        _imageDisplayScaleX = displayedWidth / _selectedPreview.Width;
        _imageDisplayScaleY = displayedHeight / _selectedPreview.Height;

        bool hasSelection = _cropSelection is Rect selection;
        if (_cropSelectionVisual is null)
            return;
        _cropSelectionVisual.IsVisible = hasSelection;
        foreach (Border handle in _cropHandles)
            handle.IsVisible = hasSelection;
        if (!hasSelection)
            return;

        Rect crop = _cropSelection!.Value;
        double left = _imageDisplayOrigin.X + crop.X * _imageDisplayScaleX;
        double top = _imageDisplayOrigin.Y + crop.Y * _imageDisplayScaleY;
        double width = crop.Width * _imageDisplayScaleX;
        double height = crop.Height * _imageDisplayScaleY;
        _cropSelectionVisual.Width = width;
        _cropSelectionVisual.Height = height;
        Canvas.SetLeft(_cropSelectionVisual, left);
        Canvas.SetTop(_cropSelectionVisual, top);

        Point[] handleCenters =
        [
            new(left, top), new(left + width / 2, top), new(left + width, top),
            new(left, top + height / 2), new(left + width, top + height / 2),
            new(left, top + height), new(left + width / 2, top + height), new(left + width, top + height)
        ];
        for (int i = 0; i < _cropHandles.Count; i++)
        {
            Border handle = _cropHandles[i];
            Canvas.SetLeft(handle, handleCenters[i].X - handle.Width / 2);
            Canvas.SetTop(handle, handleCenters[i].Y - handle.Height / 2);
        }
    }

    private Point ToImagePoint(Point canvasPoint)
    {
        if (_selectedPreview is null || _imageDisplayScaleX <= 0 || _imageDisplayScaleY <= 0)
            return default;
        int x = (int)Math.Round(Math.Clamp((canvasPoint.X - _imageDisplayOrigin.X) / _imageDisplayScaleX, 0, _selectedPreview.Width), MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(Math.Clamp((canvasPoint.Y - _imageDisplayOrigin.Y) / _imageDisplayScaleY, 0, _selectedPreview.Height), MidpointRounding.AwayFromZero);
        return new Point(Math.Clamp(x, 0, _selectedPreview.Width), Math.Clamp(y, 0, _selectedPreview.Height));
    }

    private void CropOverlayCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_selectedPreview is null || !e.GetCurrentPoint(CropOverlayCanvas).Properties.IsLeftButtonPressed)
            return;

        EnsureSourcePreview();
        _cropDragStart = e.GetPosition(CropOverlayCanvas);
        _cropDragImageStart = ToImagePoint(_cropDragStart);
        _cropDragSelectionStart = _cropSelection ?? default;
        _cropDragMode = GetCropDragMode(_cropDragStart);
        if (_cropDragMode == CropDragMode.Draw)
            SetCropSelection(new Rect(_cropDragImageStart.X, _cropDragImageStart.Y, 1, 1));
        e.Pointer.Capture(CropOverlayCanvas);
        e.Handled = true;
    }

    private CropDragMode GetCropDragMode(Point point)
    {
        if (_cropSelection is not Rect crop)
            return CropDragMode.Draw;

        double left = _imageDisplayOrigin.X + crop.X * _imageDisplayScaleX;
        double top = _imageDisplayOrigin.Y + crop.Y * _imageDisplayScaleY;
        double right = left + crop.Width * _imageDisplayScaleX;
        double bottom = top + crop.Height * _imageDisplayScaleY;
        const double tolerance = 10;
        bool nearLeft = Math.Abs(point.X - left) <= tolerance && point.Y >= top - tolerance && point.Y <= bottom + tolerance;
        bool nearRight = Math.Abs(point.X - right) <= tolerance && point.Y >= top - tolerance && point.Y <= bottom + tolerance;
        bool nearTop = Math.Abs(point.Y - top) <= tolerance && point.X >= left - tolerance && point.X <= right + tolerance;
        bool nearBottom = Math.Abs(point.Y - bottom) <= tolerance && point.X >= left - tolerance && point.X <= right + tolerance;
        CropDragMode edges = CropDragMode.None;
        if (nearLeft) edges |= CropDragMode.Left;
        if (nearRight) edges |= CropDragMode.Right;
        if (nearTop) edges |= CropDragMode.Top;
        if (nearBottom) edges |= CropDragMode.Bottom;
        if (edges != CropDragMode.None)
            return edges;
        return new Rect(left, top, right - left, bottom - top).Contains(point) ? CropDragMode.Move : CropDragMode.Draw;
    }

    private void CropOverlayCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_cropDragMode == CropDragMode.None || _selectedPreview is null)
            return;
        Point current = ToImagePoint(e.GetPosition(CropOverlayCanvas));
        int imageWidth = _selectedPreview.Width;
        int imageHeight = _selectedPreview.Height;

        if (_cropDragMode == CropDragMode.Draw)
        {
            int x1 = (int)_cropDragImageStart.X;
            int y1 = (int)_cropDragImageStart.Y;
            int x2 = (int)current.X;
            int y2 = (int)current.Y;
            int left = Math.Min(x1, x2);
            int top = Math.Min(y1, y2);
            int right = Math.Max(x1, x2);
            int bottom = Math.Max(y1, y2);
            SetCropSelection(new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)));
            return;
        }

        int deltaX = (int)current.X - (int)_cropDragImageStart.X;
        int deltaY = (int)current.Y - (int)_cropDragImageStart.Y;
        int leftEdge = (int)_cropDragSelectionStart.X;
        int topEdge = (int)_cropDragSelectionStart.Y;
        int rightEdge = leftEdge + (int)_cropDragSelectionStart.Width;
        int bottomEdge = topEdge + (int)_cropDragSelectionStart.Height;

        if (_cropDragMode == CropDragMode.Move)
        {
            int width = rightEdge - leftEdge;
            int height = bottomEdge - topEdge;
            leftEdge = Math.Clamp(leftEdge + deltaX, 0, imageWidth - width);
            topEdge = Math.Clamp(topEdge + deltaY, 0, imageHeight - height);
            rightEdge = leftEdge + width;
            bottomEdge = topEdge + height;
        }
        else
        {
            if (_cropDragMode.HasFlag(CropDragMode.Left)) leftEdge = Math.Clamp(leftEdge + deltaX, 0, rightEdge - 1);
            if (_cropDragMode.HasFlag(CropDragMode.Right)) rightEdge = Math.Clamp(rightEdge + deltaX, leftEdge + 1, imageWidth);
            if (_cropDragMode.HasFlag(CropDragMode.Top)) topEdge = Math.Clamp(topEdge + deltaY, 0, bottomEdge - 1);
            if (_cropDragMode.HasFlag(CropDragMode.Bottom)) bottomEdge = Math.Clamp(bottomEdge + deltaY, topEdge + 1, imageHeight);
        }

        SetCropSelection(new Rect(leftEdge, topEdge, rightEdge - leftEdge, bottomEdge - topEdge));
    }

    private void CropOverlayCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _cropDragMode = CropDragMode.None;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void CropOverlayCanvas_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _cropDragMode = CropDragMode.None;

    private void SetCropSelection(Rect? selection)
    {
        _cropSelection = selection;
        _cropApplied = false;
        _updatingCropFields = true;
        CropXBox.Text = selection is Rect crop ? ((int)crop.X).ToString() : string.Empty;
        CropYBox.Text = selection is Rect cropY ? ((int)cropY.Y).ToString() : string.Empty;
        CropWidthBox.Text = selection is Rect cropWidth ? ((int)cropWidth.Width).ToString() : string.Empty;
        CropHeightBox.Text = selection is Rect cropHeight ? ((int)cropHeight.Height).ToString() : string.Empty;
        _updatingCropFields = false;
        UpdateCropOverlay();
    }

    private void CropDimensionBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_updatingCropFields || _selectedPreview is null)
            return;
        try
        {
            EnsureSourcePreview();
            string widthText = CropWidthBox.Text?.Trim() ?? string.Empty;
            string heightText = CropHeightBox.Text?.Trim() ?? string.Empty;
            if (widthText.Length == 0 && heightText.Length == 0)
            {
                SetCropSelection(null);
                return;
            }

            int? requestedWidth = ParseCropDimension(widthText, "Crop width", _selectedPreview.Width);
            int? requestedHeight = ParseCropDimension(heightText, "Crop height", _selectedPreview.Height);
            Rect current = _cropSelection ?? new Rect(0, 0, _selectedPreview.Width, _selectedPreview.Height);
            int width = requestedWidth ?? (int)current.Width;
            int height = requestedHeight ?? (int)current.Height;
            int x = Math.Clamp((int)current.X, 0, _selectedPreview.Width - width);
            int y = Math.Clamp((int)current.Y, 0, _selectedPreview.Height - height);
            SetCropSelection(new Rect(x, y, width, height));
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            SetCropSelection(_cropSelection);
        }
    }

    private static int? ParseCropDimension(string text, string label, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!int.TryParse(text, out int value) || value < 1 || value > maximum)
            throw new ArgumentException($"{label} must be a whole number from 1 to {maximum}.");
        return value;
    }

    private void CropToolCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        bool enabled = CropToolCheckBox.IsChecked == true;
        CropControlsPanel.IsVisible = enabled;
        if (!enabled)
        {
            bool refreshPreview = _cropApplied;
            _cropApplied = false;
            if (refreshPreview && EditorPanel.IsVisible)
                _ = UpdateEditorPreviewAsync();
        }
        CropOverlayCanvas.IsVisible = enabled && !_showingProcessedPreview;
        EditCropOnSourceButton.IsVisible = enabled && _showingProcessedPreview;
        UpdateCropOverlay();
    }

    private void ResizeToolCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        ResizeControlsPanel.IsVisible = ResizeToolCheckBox.IsChecked == true;
        ProcessingOption_Changed(sender, e);
    }

    private void RotationToolCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        RotationPicker.IsVisible = RotationToolCheckBox.IsChecked == true;
        ProcessingOption_Changed(sender, e);
    }

    private void FilterToolCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        FilterPicker.IsVisible = FilterToolCheckBox.IsChecked == true;
        ProcessingOption_Changed(sender, e);
    }

    private void ProcessingPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ProcessingOption_Changed(sender, e);

    private void ProcessingOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_initializing && EditorPanel?.IsVisible == true)
            _ = UpdateEditorPreviewAsync();
    }

    private void ProcessingTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_initializing && EditorPanel?.IsVisible == true)
            _ = UpdateEditorPreviewAsync();
    }

    private void CropButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CropToolCheckBox.IsChecked != true)
            CropToolCheckBox.IsChecked = true;
        if (_cropSelection is null)
        {
            StatusText.Text = "Draw or enter a crop rectangle before applying Crop.";
            return;
        }
        _cropApplied = true;
        _ = UpdateEditorPreviewAsync();
    }

    private void ClearCropButton_Click(object? sender, RoutedEventArgs e)
    {
        EnsureSourcePreview();
        SetCropSelection(null);
        StatusText.Text = "Crop cleared. The full source image will be used.";
        if (EditorPanel.IsVisible)
            _ = UpdateEditorPreviewAsync();
    }

    private void EditCropOnSourceButton_Click(object? sender, RoutedEventArgs e) => EnsureSourcePreview();

    private void EnsureSourcePreview()
    {
        if (!_showingProcessedPreview || _selectedPreview is null)
            return;
        _showingProcessedPreview = false;
        EditCropOnSourceButton.IsVisible = false;
        CropOverlayCanvas.IsVisible = CropToolCheckBox.IsChecked == true;
        SetPreviewBitmap(_selectedPreview.PngBytes, EditorPreviewImage, ref _editorBitmap);
        UpdateCropOverlay();
    }

    private void ShowEditor(ImageGalleryItem item)
    {
        _saveDirectory = _location == ImageBrowserLocation.UserImages
            ? _imageService.GetSafeCurrentDirectory(_location, _currentDirectory)
            : _imageService.GetRoot(ImageBrowserLocation.UserImages);
        EditorImageTitle.Text = $"{item.Name} · {_selectedPreview!.Width} × {_selectedPreview.Height}";
        CropToolCheckBox.IsChecked = false;
        ResizeToolCheckBox.IsChecked = false;
        RotationToolCheckBox.IsChecked = false;
        FilterToolCheckBox.IsChecked = false;
        CropControlsPanel.IsVisible = false;
        ResizeControlsPanel.IsVisible = false;
        RotationPicker.IsVisible = false;
        FilterPicker.IsVisible = false;
        _cropApplied = false;
        SetCropSelection(null);
        _showingProcessedPreview = false;
        EditCropOnSourceButton.IsVisible = false;
        CropOverlayCanvas.IsVisible = CropToolCheckBox.IsChecked == true;
        ResizeWidthBox.Text = ResizeHeightBox.Text = string.Empty;
        KeepAspectCheckBox.IsChecked = true;
        RotationPicker.SelectedIndex = 0;
        FilterPicker.SelectedIndex = 0;
        EditorPanel.IsVisible = true;
        GalleryPanel.IsVisible = false;
        UpdateMenuAvailability();
        SetPreviewBitmap(_selectedPreview.PngBytes, EditorPreviewImage, ref _editorBitmap);
        UpdateCropOverlay();
    }

    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        EditorPanel.IsVisible = false;
        GalleryPanel.IsVisible = true;
        RefreshEntries();
    }

    private async Task UpdateEditorPreviewAsync()
    {
        if (_selectedImagePath is null)
            return;
        try
        {
            int updateVersion = ++_previewUpdateVersion;
            string sourcePath = _selectedImagePath;
            StatusText.Text = "Updating preview…";
            var options = ReadProcessingOptions();
            bool hasOperations = options.CropX.HasValue || options.ResizeWidth.HasValue || options.ResizeHeight.HasValue ||
                options.RotationDegrees != 0 || !string.Equals(options.Filter, "None", StringComparison.OrdinalIgnoreCase);
            byte[] png = hasOperations
                ? await Task.Run(() => _imageService.CreateProcessedPreview(sourcePath, options))
                : _selectedPreview?.PngBytes ?? Array.Empty<byte>();
            if (updateVersion != _previewUpdateVersion || png.Length == 0)
                return;
            SetPreviewBitmap(png, EditorPreviewImage, ref _editorBitmap);
            _showingProcessedPreview = hasOperations;
            CropOverlayCanvas.IsVisible = CropToolCheckBox.IsChecked == true && !hasOperations;
            EditCropOnSourceButton.IsVisible = hasOperations && CropToolCheckBox.IsChecked == true;
            StatusText.Text = hasOperations ? "Preview updated. The source image remains unchanged." : "Source image shown; no operations are enabled.";
        }
        catch (Exception ex) { StatusText.Text = $"Could not update preview: {ex.Message}"; }
    }

    private ImageProcessingOptions ReadProcessingOptions()
    {
        int? cropX = CropToolCheckBox.IsChecked == true && _cropApplied && _cropSelection is Rect crop ? (int)crop.X : null;
        int? cropY = CropToolCheckBox.IsChecked == true && _cropApplied && _cropSelection is Rect cropYValue ? (int)cropYValue.Y : null;
        int? cropWidth = CropToolCheckBox.IsChecked == true && _cropApplied && _cropSelection is Rect cropWidthValue ? (int)cropWidthValue.Width : null;
        int? cropHeight = CropToolCheckBox.IsChecked == true && _cropApplied && _cropSelection is Rect cropHeightValue ? (int)cropHeightValue.Height : null;
        int? resizeWidth = ResizeToolCheckBox.IsChecked == true ? ParseOptionalNumber(ResizeWidthBox.Text, "Resize width") : null;
        int? resizeHeight = ResizeToolCheckBox.IsChecked == true ? ParseOptionalNumber(ResizeHeightBox.Text, "Resize height") : null;
        if (cropWidth == 0 || cropHeight == 0 || resizeWidth == 0 || resizeHeight == 0)
            throw new ArgumentException("Crop width, crop height, and resize dimensions must be greater than zero.");
        if (new[] { cropX, cropY, cropWidth, cropHeight }.Any(value => value.HasValue) &&
            new[] { cropX, cropY, cropWidth, cropHeight }.Any(value => !value.HasValue))
            throw new ArgumentException("Enter all four crop values, or leave all four blank.");
        if (ResizeToolCheckBox.IsChecked == true && !KeepAspectCheckBox.IsChecked.GetValueOrDefault() && (resizeWidth.HasValue != resizeHeight.HasValue))
            throw new ArgumentException("Enter both resize dimensions when Keep aspect ratio is turned off.");
        int rotation = RotationToolCheckBox.IsChecked == true ? int.Parse(SelectedTag(RotationPicker, "0")) : 0;
        string filter = FilterToolCheckBox.IsChecked == true ? SelectedTag(FilterPicker, "None") : "None";
        return new ImageProcessingOptions(cropX, cropY, cropWidth, cropHeight, resizeWidth, resizeHeight,
            KeepAspectCheckBox.IsChecked == true, rotation, filter);
    }

    private static int? ParseOptionalNumber(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!int.TryParse(text.Trim(), out int value) || value < 0 || value > 16384)
            throw new ArgumentException($"{label} must be a whole number from 0 to 16384.");
        return value;
    }

    private static string SelectedTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static Bitmap BitmapFromBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return new Bitmap(stream);
    }

    private static void SetPreviewBitmap(byte[] bytes, Image target, ref Bitmap? current)
    {
        var bitmap = BitmapFromBytes(bytes);
        current?.Dispose();
        current = bitmap;
        target.Source = bitmap;
    }

    private void DisposeThumbnails()
    {
        foreach (ImageGalleryItem entry in _entries)
            entry.Thumbnail?.Dispose();
    }

    private static bool IsWithin(string root, string path)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullPath = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullRoot, fullPath, comparison) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static bool PathsEqual(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeThumbnails();
        _previewBitmap?.Dispose();
        _editorBitmap?.Dispose();
        base.OnClosed(e);
    }

    [Flags]
    private enum CropDragMode
    {
        None = 0,
        Draw = 1,
        Move = 2,
        Left = 4,
        Right = 8,
        Top = 16,
        Bottom = 32
    }

    private sealed record LoadedImageGalleryItem(ImageAssetEntry Entry, byte[]? ThumbnailPng);

}

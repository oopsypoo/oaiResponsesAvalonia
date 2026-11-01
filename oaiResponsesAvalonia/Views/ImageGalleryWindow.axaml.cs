using Avalonia.Controls;
using Avalonia.Input;
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

    private async void RefreshEntries()
    {
        try
        {
            string currentPath = _imageService.GetSafeCurrentDirectory(_location, _currentDirectory);
            _currentDirectory = currentPath;
            CurrentPathText.Text = currentPath;
            UpButton.IsEnabled = !PathsEqual(currentPath, _imageService.GetRoot(_location));
            NewFolderButton.IsEnabled = _location == ImageBrowserLocation.UserImages;
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
            _selectedImagePath = null;
            _selectedPreview = null;
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

    private void RefreshButton_Click(object? sender, RoutedEventArgs e) => RefreshEntries();

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

    private void ShowEditor(ImageGalleryItem item)
    {
        _saveDirectory = _location == ImageBrowserLocation.UserImages
            ? _imageService.GetSafeCurrentDirectory(_location, _currentDirectory)
            : _imageService.GetRoot(ImageBrowserLocation.UserImages);
        EditorImageTitle.Text = $"{item.Name} · {_selectedPreview!.Width} × {_selectedPreview.Height}";
        OutputNameBox.Text = Path.GetFileNameWithoutExtension(item.Name);
        CropXBox.Text = CropYBox.Text = CropWidthBox.Text = CropHeightBox.Text = string.Empty;
        ResizeWidthBox.Text = ResizeHeightBox.Text = string.Empty;
        KeepAspectCheckBox.IsChecked = true;
        RotationPicker.SelectedIndex = 0;
        FilterPicker.SelectedIndex = 0;
        KeepTransparencyCheckBox.IsChecked = _selectedPreview.HasTransparency;
        UpdateFormatHelp();
        EditorPanel.IsVisible = true;
        GalleryPanel.IsVisible = false;
        SetPreviewBitmap(_selectedPreview.PngBytes, EditorPreviewImage, ref _editorBitmap);
    }

    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        EditorPanel.IsVisible = false;
        GalleryPanel.IsVisible = true;
    }

    private async void ApplyPreviewButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedImagePath is null)
            return;
        try
        {
            StatusText.Text = "Applying preview operations…";
            var options = ReadProcessingOptions();
            byte[] png = await Task.Run(() => _imageService.CreateProcessedPreview(_selectedImagePath, options));
            SetPreviewBitmap(png, EditorPreviewImage, ref _editorBitmap);
            StatusText.Text = "Preview updated. The source image remains unchanged.";
        }
        catch (Exception ex) { StatusText.Text = $"Could not update preview: {ex.Message}"; }
    }

    private async void SaveEditedButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedImagePath is null)
            return;
        SaveEditedButton.IsEnabled = false;
        try
        {
            var options = ReadProcessingOptions();
            string extension = SelectedTag(OutputFormatPicker, ".png").ToLowerInvariant();
            var icoSizes = extension == ".ico" ? ParseIcoSizes(IcoSizesBox.Text ?? string.Empty) : null;
            string baseName = Path.GetFileNameWithoutExtension((OutputNameBox.Text ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(baseName))
                throw new ArgumentException("Enter a filename for the saved image.");
            bool keepTransparency = KeepTransparencyCheckBox.IsChecked == true;
            StatusText.Text = "Processing and saving a copy…";
            byte[] output = await Task.Run(() => _imageService.ConvertToBytes(_selectedImagePath, options, extension, keepTransparency, icoSizes));
            string savedPath = _imageService.SaveToUserImages(_saveDirectory, baseName + extension, output);
            StatusText.Text = $"Saved copy: {savedPath}";
        }
        catch (Exception ex) { StatusText.Text = $"Save failed: {ex.Message}"; }
        finally { SaveEditedButton.IsEnabled = true; }
    }

    private void OutputFormatPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IcoSizesBox is null)
            return;
        string extension = SelectedTag(OutputFormatPicker, ".png");
        IcoSizesBox.IsVisible = extension == ".ico";
        KeepTransparencyCheckBox.IsVisible = extension is ".png" or ".webp" or ".bmp" or ".ico";
        UpdateFormatHelp();
    }

    private void UpdateFormatHelp()
    {
        if (TransparencyHelpText is null)
            return;
        string extension = SelectedTag(OutputFormatPicker, ".png");
        TransparencyHelpText.Text = extension is ".jpg" or ".jpeg"
            ? "JPEG cannot store transparency; transparent pixels will be composited onto white."
            : "When unchecked, transparent pixels are composited onto white before saving.";
    }

    private ImageProcessingOptions ReadProcessingOptions()
    {
        int? cropX = ParseOptionalNumber(CropXBox.Text, "Crop X");
        int? cropY = ParseOptionalNumber(CropYBox.Text, "Crop Y");
        int? cropWidth = ParseOptionalNumber(CropWidthBox.Text, "Crop width");
        int? cropHeight = ParseOptionalNumber(CropHeightBox.Text, "Crop height");
        int? resizeWidth = ParseOptionalNumber(ResizeWidthBox.Text, "Resize width");
        int? resizeHeight = ParseOptionalNumber(ResizeHeightBox.Text, "Resize height");
        if (cropWidth == 0 || cropHeight == 0 || resizeWidth == 0 || resizeHeight == 0)
            throw new ArgumentException("Crop width, crop height, and resize dimensions must be greater than zero.");
        if (new[] { cropX, cropY, cropWidth, cropHeight }.Any(value => value.HasValue) &&
            new[] { cropX, cropY, cropWidth, cropHeight }.Any(value => !value.HasValue))
            throw new ArgumentException("Enter all four crop values, or leave all four blank.");
        if (!KeepAspectCheckBox.IsChecked.GetValueOrDefault() && (resizeWidth.HasValue != resizeHeight.HasValue))
            throw new ArgumentException("Enter both resize dimensions when Keep aspect ratio is turned off.");
        int rotation = int.Parse(SelectedTag(RotationPicker, "0"));
        string filter = SelectedTag(FilterPicker, "None");
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

    private sealed record LoadedImageGalleryItem(ImageAssetEntry Entry, byte[]? ThumbnailPng);

}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Icon;
using SixLabors.ImageSharp.Formats.Ico;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SharpImage = SixLabors.ImageSharp.Image;

namespace oaiResponsesAvalonia.Services;

public enum ImageBrowserLocation
{
    ApplicationImages,
    UserImages
}

public sealed record ImageAssetEntry(string FullPath, string Name, bool IsDirectory, string Details);

public sealed record ImageAssetPreview(byte[] PngBytes, int Width, int Height, string Format, int FrameCount, bool HasTransparency);

public sealed record ImageProcessingOptions(
    int? CropX = null,
    int? CropY = null,
    int? CropWidth = null,
    int? CropHeight = null,
    int? ResizeWidth = null,
    int? ResizeHeight = null,
    bool KeepAspectRatio = true,
    int RotationDegrees = 0,
    string Filter = "None");

/// <summary>
/// Shared image operations for the gallery/editor and the planned img terminal context.
/// All browse and write operations are constrained to the configured application/user image roots.
/// </summary>
public sealed class ImageLibraryService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".webp", ".jpg", ".jpeg", ".bmp", ".ico"
    };

    private string _applicationImagesRoot = string.Empty;
    private string _userImagesRoot = string.Empty;

    public ImageLibraryService(string applicationImagesRoot, string userImagesRoot) => SetRoots(applicationImagesRoot, userImagesRoot);

    public void SetRoots(string applicationImagesRoot, string userImagesRoot)
    {
        _applicationImagesRoot = Path.GetFullPath(applicationImagesRoot);
        _userImagesRoot = Path.GetFullPath(userImagesRoot);
        Directory.CreateDirectory(_applicationImagesRoot);
        Directory.CreateDirectory(_userImagesRoot);
    }

    public string GetRoot(ImageBrowserLocation location) => location == ImageBrowserLocation.ApplicationImages
        ? _applicationImagesRoot
        : _userImagesRoot;

    public IReadOnlyList<ImageAssetEntry> ListEntries(ImageBrowserLocation location, string directory)
    {
        string root = GetRoot(location);
        string current = RequireDirectoryWithinRoot(root, directory);
        var result = new List<ImageAssetEntry>();

        foreach (string childDirectory in Directory.EnumerateDirectories(current)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (IsReparsePoint(childDirectory))
                continue;

            result.Add(new ImageAssetEntry(childDirectory, Path.GetFileName(childDirectory), true, "Folder"));
        }

        foreach (string file in Directory.EnumerateFiles(current)
                     .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (IsReparsePoint(file))
                continue;

            try
            {
                var info = SharpImage.Identify(file);
                string format = info.Metadata.DecodedImageFormat?.Name ?? Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                result.Add(new ImageAssetEntry(file, Path.GetFileName(file), false,
                    $"{info.Width} × {info.Height} · {format}"));
            }
            catch (Exception ex) when (ex is InvalidImageContentException or IOException or UnauthorizedAccessException)
            {
                result.Add(new ImageAssetEntry(file, Path.GetFileName(file), false, "Could not read image metadata"));
            }
        }

        return result;
    }

    public byte[] CreateThumbnail(string imagePath, int maxSize = 88)
    {
        ValidateManagedImagePath(imagePath);
        using Image<Rgba32> image = SharpImage.Load<Rgba32>(new DecoderOptions { MaxFrames = 1 }, imagePath);
        image.Mutate(context => context.AutoOrient().Resize(new ResizeOptions
        {
            Size = new Size(maxSize, maxSize),
            Mode = ResizeMode.Max
        }));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    public ImageAssetPreview CreatePreview(string imagePath, int maxSize = 1200)
    {
        ValidateManagedImagePath(imagePath);
        int frameCount = SharpImage.Identify(imagePath).FrameCount;
        using Image<Rgba32> image = SharpImage.Load<Rgba32>(new DecoderOptions { MaxFrames = 1 }, imagePath);
        string format = image.Metadata.DecodedImageFormat?.Name ?? Path.GetExtension(imagePath).TrimStart('.').ToUpperInvariant();
        image.Mutate(context => context.AutoOrient());
        int width = image.Width;
        int height = image.Height;
        bool hasTransparency = ContainsTransparency(image);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(maxSize, maxSize),
            Mode = ResizeMode.Max
        }));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return new ImageAssetPreview(output.ToArray(), width, height, format, frameCount, hasTransparency);
    }

    public byte[] CreateProcessedPreview(string imagePath, ImageProcessingOptions options)
    {
        using Image<Rgba32> image = LoadProcessed(imagePath, options);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(1200, 1200),
            Mode = ResizeMode.Max
        }));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    public byte[] ConvertToBytes(string imagePath, ImageProcessingOptions options, string extension, bool keepTransparency, IReadOnlyList<int>? icoSizes = null)
    {
        using Image<Rgba32> image = LoadProcessed(imagePath, options);
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || !keepTransparency)
            FlattenTransparency(image);

        using var output = new MemoryStream();
        switch (extension.ToLowerInvariant())
        {
            case ".png": image.SaveAsPng(output); break;
            case ".webp": image.SaveAsWebp(output); break;
            case ".jpg":
            case ".jpeg": image.SaveAsJpeg(output); break;
            case ".bmp":
                if (keepTransparency)
                    image.Save(output, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Bit32, SupportTransparency = true });
                else
                    image.SaveAsBmp(output);
                break;
            case ".ico": SaveMultiSizeIco(image, icoSizes is { Count: > 0 } ? icoSizes : [256, 128, 64, 48, 32, 16], output); break;
            default: throw new NotSupportedException($"The output format '{extension}' is not supported.");
        }
        return output.ToArray();
    }

    public string SaveToUserImages(string targetDirectory, string fileName, byte[] bytes)
    {
        string directory = RequireDirectoryWithinRoot(_userImagesRoot, targetDirectory);
        Directory.CreateDirectory(directory);
        string safeName = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..")
            throw new ArgumentException("Enter a valid output filename.", nameof(fileName));

        string extension = Path.GetExtension(safeName);
        if (!SupportedExtensions.Contains(extension))
            throw new NotSupportedException("Only PNG, WebP, JPEG, BMP, and ICO output is supported.");

        string destination = GetUniquePath(directory, safeName);
        File.WriteAllBytes(destination, bytes);
        return destination;
    }

    public string ImportToUserImages(string sourcePath, string targetDirectory)
    {
        string extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension))
            throw new NotSupportedException("Import supports PNG, WebP, JPEG, BMP, and ICO files only.");

        string directory = RequireDirectoryWithinRoot(_userImagesRoot, targetDirectory);
        Directory.CreateDirectory(directory);
        string destination = GetUniquePath(directory, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destination);
        return destination;
    }

    public string CreateUserFolder(string parentDirectory, string folderName)
    {
        string parent = RequireDirectoryWithinRoot(_userImagesRoot, parentDirectory);
        string name = folderName.Trim();
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Enter a valid folder name.", nameof(folderName));

        string created = Path.GetFullPath(Path.Combine(parent, name));
        EnsureWithinRoot(_userImagesRoot, created);
        Directory.CreateDirectory(created);
        return created;
    }

    public void DeleteUserFile(string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        EnsureStrictlyWithinRoot(_userImagesRoot, fullPath);
        EnsureNoReparsePointsBetween(_userImagesRoot, fullPath);
        if (Directory.Exists(fullPath) || !File.Exists(fullPath))
            throw new FileNotFoundException("The selected user image was not found.", fullPath);
        if (!SupportedExtensions.Contains(Path.GetExtension(fullPath)))
            throw new NotSupportedException("Only supported image files can be deleted from the gallery.");

        File.Delete(fullPath);
    }

    public void DeleteUserFolder(string directoryPath)
    {
        string fullPath = Path.GetFullPath(directoryPath);
        EnsureStrictlyWithinRoot(_userImagesRoot, fullPath);
        EnsureNoReparsePointsBetween(_userImagesRoot, fullPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Folder not found: {fullPath}");

        EnsureTreeContainsNoReparsePoints(fullPath);
        Directory.Delete(fullPath, recursive: true);
    }

    public string GetSafeCurrentDirectory(ImageBrowserLocation location, string directory) =>
        RequireDirectoryWithinRoot(GetRoot(location), directory);

    private Image<Rgba32> LoadProcessed(string imagePath, ImageProcessingOptions options)
    {
        ValidateManagedImagePath(imagePath);
        Image<Rgba32> image = SharpImage.Load<Rgba32>(imagePath);
        try
        {
            image.Mutate(context => context.AutoOrient());
            bool hasCrop = options.CropX.HasValue || options.CropY.HasValue || options.CropWidth.HasValue || options.CropHeight.HasValue;
            if (hasCrop)
            {
                if (options.CropX is not int x || options.CropY is not int y || options.CropWidth is not int width || options.CropHeight is not int height ||
                    x < 0 || y < 0 || width < 1 || height < 1 || x + width > image.Width || y + height > image.Height)
                    throw new ArgumentException($"Crop rectangle must fit inside the image ({image.Width} × {image.Height}). Enter X, Y, width, and height.");
                image.Mutate(context => context.Crop(new Rectangle(x, y, width, height)));
            }

            if (options.ResizeWidth.HasValue || options.ResizeHeight.HasValue)
            {
                int? width = options.ResizeWidth;
                int? height = options.ResizeHeight;
                if (!options.KeepAspectRatio && (!width.HasValue || !height.HasValue))
                    throw new ArgumentException("Enter both resize dimensions when Keep aspect ratio is turned off.");
                if (options.KeepAspectRatio && width.HasValue && height.HasValue)
                    image.Mutate(context => context.Resize(new ResizeOptions { Size = new Size(width.Value, height.Value), Mode = ResizeMode.Max }));
                else
                    image.Mutate(context => context.Resize(width ?? 0, height ?? 0));
            }

            switch (options.RotationDegrees)
            {
                case 90: image.Mutate(context => context.Rotate(RotateMode.Rotate90)); break;
                case 180: image.Mutate(context => context.Rotate(RotateMode.Rotate180)); break;
                case 270: image.Mutate(context => context.Rotate(RotateMode.Rotate270)); break;
                case 0: break;
                default: throw new ArgumentOutOfRangeException(nameof(options), "Rotation must be 0, 90, 180, or 270 degrees.");
            }

            ApplyFilter(image, options.Filter);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static void ApplyFilter(Image<Rgba32> image, string filter)
    {
        if (string.Equals(filter, "None", StringComparison.OrdinalIgnoreCase))
            return;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba32 pixel = row[x];
                    float r = pixel.R, g = pixel.G, b = pixel.B;
                    switch (filter.ToLowerInvariant())
                    {
                        case "grayscale":
                            byte gray = (byte)Math.Clamp((int)Math.Round(0.299f * r + 0.587f * g + 0.114f * b), 0, 255);
                            row[x] = new Rgba32(gray, gray, gray, pixel.A);
                            break;
                        case "sepia":
                            row[x] = new Rgba32(
                                ClampByte(0.393f * r + 0.769f * g + 0.189f * b),
                                ClampByte(0.349f * r + 0.686f * g + 0.168f * b),
                                ClampByte(0.272f * r + 0.534f * g + 0.131f * b), pixel.A);
                            break;
                        case "invert":
                            row[x] = new Rgba32((byte)(255 - pixel.R), (byte)(255 - pixel.G), (byte)(255 - pixel.B), pixel.A);
                            break;
                        default: throw new ArgumentException($"The filter '{filter}' is not supported.");
                    }
                }
            }
        });
    }

    private static byte ClampByte(float value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static bool ContainsTransparency(Image<Rgba32> image)
    {
        bool found = false;
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height && !found; y++)
            {
                foreach (Rgba32 pixel in accessor.GetRowSpan(y))
                {
                    if (pixel.A < 255)
                    {
                        found = true;
                        break;
                    }
                }
            }
        });
        return found;
    }

    private static void FlattenTransparency(Image<Rgba32> image)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba32 pixel = row[x];
                    if (pixel.A == 255)
                        continue;
                    int alpha = pixel.A;
                    row[x] = new Rgba32(
                        (byte)((pixel.R * alpha + 255 * (255 - alpha) + 127) / 255),
                        (byte)((pixel.G * alpha + 255 * (255 - alpha) + 127) / 255),
                        (byte)((pixel.B * alpha + 255 * (255 - alpha) + 127) / 255), 255);
                }
            }
        });
    }

    private static void SaveMultiSizeIco(Image<Rgba32> source, IReadOnlyList<int> sizes, Stream destination)
    {
        if (sizes.Any(size => size is < 1 or > 256))
            throw new ArgumentException("ICO sizes must be between 1 and 256 pixels.", nameof(sizes));

        int canvasSize = sizes.Max();
        using Image<Rgba32> icon = new(canvasSize, canvasSize);
        for (int index = 0; index < sizes.Count; index++)
        {
            int size = sizes[index];
            using Image<Rgba32> artwork = source.Clone(context => context.Resize(new ResizeOptions
            {
                Size = new Size(size, size),
                Mode = ResizeMode.Max
            }));
            using Image<Rgba32> square = new(size, size);
            using Image<Rgba32> frameCanvas = new(canvasSize, canvasSize);
            int offsetX = (size - artwork.Width) / 2;
            int offsetY = (size - artwork.Height) / 2;
            artwork.ProcessPixelRows(square, (artworkRows, squareRows) =>
            {
                for (int row = 0; row < artwork.Height; row++)
                    artworkRows.GetRowSpan(row).CopyTo(squareRows.GetRowSpan(row + offsetY).Slice(offsetX, artwork.Width));
            });
            square.ProcessPixelRows(frameCanvas, (squareRows, frameRows) =>
            {
                for (int row = 0; row < size; row++)
                    squareRows.GetRowSpan(row).CopyTo(frameRows.GetRowSpan(row).Slice(0, size));
            });
            if (index == 0)
            {
                frameCanvas.ProcessPixelRows(icon, (sourceRows, iconRows) =>
                {
                    for (int row = 0; row < canvasSize; row++)
                        sourceRows.GetRowSpan(row).CopyTo(iconRows.GetRowSpan(row));
                });
            }
            else
                icon.Frames.AddFrame(frameCanvas.Frames.RootFrame);

            var metadata = icon.Frames[index].Metadata.GetIcoMetadata();
            metadata.Compression = IconFrameCompression.Png;
            byte encodedSize = size == 256 ? (byte)0 : checked((byte)size);
            metadata.EncodingWidth = encodedSize;
            metadata.EncodingHeight = encodedSize;
        }
        icon.SaveAsIco(destination);
    }

    private void ValidateManagedImagePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        bool managed = IsWithinRoot(_applicationImagesRoot, fullPath) || IsWithinRoot(_userImagesRoot, fullPath);
        if (!managed)
            throw new UnauthorizedAccessException("Image access is restricted to the configured application and user image folders.");
        if (!File.Exists(fullPath) || IsReparsePoint(fullPath))
            throw new FileNotFoundException("The selected image is missing or is a linked file.", fullPath);
        if (!SupportedExtensions.Contains(Path.GetExtension(fullPath)))
            throw new NotSupportedException("Only PNG, WebP, JPEG, BMP, and ICO images are supported.");
    }

    private static string RequireDirectoryWithinRoot(string root, string directory)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullDirectory = Path.GetFullPath(directory);
        EnsureWithinRoot(fullRoot, fullDirectory);
        if (!Directory.Exists(fullDirectory))
            throw new DirectoryNotFoundException($"Folder not found: {fullDirectory}");
        if (!string.Equals(fullRoot, fullDirectory, PathComparison) && IsReparsePoint(fullDirectory))
            throw new UnauthorizedAccessException("Linked folders are not browsable from the image gallery.");
        return fullDirectory;
    }

    private static void EnsureStrictlyWithinRoot(string root, string path)
    {
        EnsureWithinRoot(root, path);
        if (string.Equals(Path.GetFullPath(root), Path.GetFullPath(path), PathComparison))
            throw new UnauthorizedAccessException("The User Images root cannot be deleted.");
    }

    private static void EnsureNoReparsePointsBetween(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root);
        string current = Path.GetFullPath(path);
        EnsureWithinRoot(fullRoot, current);
        if (IsReparsePoint(fullRoot))
            throw new UnauthorizedAccessException("Linked folders cannot be deleted from the gallery.");

        while (!string.Equals(current, fullRoot, PathComparison))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if (IsReparsePoint(current))
                    throw new UnauthorizedAccessException("Linked files and folders cannot be deleted from the gallery.");
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, PathComparison))
                break;
            current = parent;
        }
    }

    private static void EnsureTreeContainsNoReparsePoints(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (IsReparsePoint(entry))
                throw new UnauthorizedAccessException("Folders containing linked files or folders cannot be deleted.");
            if (Directory.Exists(entry))
                EnsureTreeContainsNoReparsePoints(entry);
        }
    }

    private static void EnsureWithinRoot(string root, string path)
    {
        if (!IsWithinRoot(root, path))
            throw new UnauthorizedAccessException("Image operations cannot leave the selected image folder.");
    }

    private static bool IsWithinRoot(string root, string path)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string normalizedPath = Path.GetFullPath(path);
        if (string.Equals(normalizedRoot, normalizedPath, PathComparison))
            return true;
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, PathComparison) ||
               (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar && normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, PathComparison));
    }

    private static string GetUniquePath(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return candidate;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int suffix = 2; ; suffix++)
        {
            candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using oaiResponsesAvalonia.Data;

namespace oaiResponsesAvalonia.Services
{
    public class MediaStorageService
    {
        private string _imagesFolder = string.Empty;

        public MediaStorageService()
        {
        }

        public MediaStorageService(string imagesFolder)
        {
            SetImagesFolder(imagesFolder);
        }

        public void SetImagesFolder(string imagesFolder)
        {
            _imagesFolder = string.IsNullOrWhiteSpace(imagesFolder)
                ? string.Empty
                : Path.GetFullPath(imagesFolder);

            if (!string.IsNullOrWhiteSpace(_imagesFolder))
            {
                Directory.CreateDirectory(_imagesFolder);
            }
        }

        public string EnsureImagesFolder()
        {
            if (string.IsNullOrWhiteSpace(_imagesFolder))
            {
                _imagesFolder = AppPaths.ImagesDirectory;
            }

            Directory.CreateDirectory(_imagesFolder);
            return _imagesFolder;
        }

        public List<string> SaveAssistantImages(IEnumerable<string> payloads, string outputFormat = "png")
        {
            var savedPaths = new List<string>();

            if (payloads == null)
                return savedPaths;

            string folder = EnsureImagesFolder();
            string normalizedFormat = NormalizeImageFormat(outputFormat);
            string extension = GetExtensionForImageFormat(normalizedFormat);

            foreach (var payload in payloads)
            {
                if (string.IsNullOrWhiteSpace(payload))
                    continue;

                string? filePath = null;

                try
                {
                    if (payload.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    {
                        int commaIndex = payload.IndexOf(',');
                        if (commaIndex > 0)
                        {
                            string meta = payload.Substring(0, commaIndex);
                            string b64 = payload.Substring(commaIndex + 1);

                            string ext = ".png";
                            if (meta.Contains("jpeg", StringComparison.OrdinalIgnoreCase))
                                ext = ".jpg";
                            else if (meta.Contains("webp", StringComparison.OrdinalIgnoreCase))
                                ext = ".webp";
                            else if (meta.Contains("png", StringComparison.OrdinalIgnoreCase))
                                ext = ".png";

                            byte[] bytes = Convert.FromBase64String(b64);
                            string name = $"resp_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}{ext}";
                            filePath = Path.Combine(folder, name);
                            File.WriteAllBytes(filePath, bytes);
                        }
                    }
                    else if (payload.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             payload.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        // Leave URL download support for later.
                    }
                    else
                    {
                        byte[] bytes = Convert.FromBase64String(payload);
                        string name = $"resp_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}{extension}";
                        filePath = Path.Combine(folder, name);
                        File.WriteAllBytes(filePath, bytes);
                    }

                    if (!string.IsNullOrWhiteSpace(filePath))
                    {
                        savedPaths.Add(ToStoredMediaPath(filePath));
                    }
                }
                catch
                {
                    // Ignore individual bad payloads for now.
                }
            }

            return savedPaths;
        }

        private static string NormalizeImageFormat(string format)
        {
            if (string.IsNullOrWhiteSpace(format))
                return "png";

            return format.Trim().ToLowerInvariant() switch
            {
                "jpg" => "jpeg",
                "jpeg" => "jpeg",
                "png" => "png",
                "webp" => "webp",
                _ => "png"
            };
        }

        private static string GetExtensionForImageFormat(string format)
        {
            return format switch
            {
                "jpeg" => ".jpg",
                "webp" => ".webp",
                _ => ".png"
            };
        }

        public void DeleteFiles(IEnumerable<string> filePaths)
        {
            if (filePaths == null)
                return;

            foreach (string path in filePaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string? resolvedPath = ResolveMediaPath(path);
                    if (resolvedPath != null && File.Exists(resolvedPath))
                    {
                        File.Delete(resolvedPath);
                    }
                }
                catch
                {
                    // Keep deletion tolerant for now.
                }
            }
        }
        public string? ImportUserImage(string sourceFilePath)
        {
            return ImportUserFile(sourceFilePath);
        }
        private static string MakeSafeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "attachment.bin";

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }

            return fileName;
        }

        public string? ImportUserFile(string sourceFilePath)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return null;

            try
            {
                string folder = EnsureImagesFolder();
                string originalName = MakeSafeFileName(Path.GetFileName(sourceFilePath));
                string destinationPath = Path.Combine(
                    folder,
                    $"user_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{originalName}");

                File.Copy(sourceFilePath, destinationPath, overwrite: false);
                return ToStoredMediaPath(destinationPath);
            }
            catch
            {
                return null;
            }
        }

        public string? ResolveMediaPath(string? storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath))
                return null;

            if (Path.IsPathRooted(storedPath) || IsWindowsAbsolutePath(storedPath))
            {
                if (File.Exists(storedPath))
                    return storedPath;
            }

            string fileName = GetPortableFileName(storedPath);
            if (string.IsNullOrWhiteSpace(fileName))
                return storedPath;

            string managedPath = Path.Combine(EnsureImagesFolder(), fileName);
            if (File.Exists(managedPath))
                return managedPath;

            if (Path.IsPathRooted(storedPath) || IsWindowsAbsolutePath(storedPath))
                return storedPath;

            return managedPath;
        }

        public int MigrateLegacyMediaPaths()
        {
            using var context = new AppDbContext();
            var mediaFiles = context.Media.ToList();
            int migratedCount = 0;

            foreach (var media in mediaFiles)
            {
                string oldPath = media.LocalPath;
                if (string.IsNullOrWhiteSpace(oldPath) ||
                    oldPath.StartsWith("media/", StringComparison.OrdinalIgnoreCase) ||
                    (!Path.IsPathRooted(oldPath) && !IsWindowsAbsolutePath(oldPath)))
                {
                    continue;
                }

                string? sourcePath = ResolveMediaPath(oldPath);
                if (sourcePath == null || !File.Exists(sourcePath))
                    continue;

                string? storedPath = IsInImagesFolder(sourcePath)
                    ? ToStoredMediaPath(sourcePath)
                    : ImportUserFile(sourcePath);

                if (!string.IsNullOrWhiteSpace(storedPath) &&
                    !string.Equals(oldPath, storedPath, StringComparison.Ordinal))
                {
                    media.LocalPath = storedPath;
                    migratedCount++;
                }
            }

            if (migratedCount > 0)
                context.SaveChanges();

            return migratedCount;
        }

        private bool IsInImagesFolder(string path)
        {
            string root = EnsureImagesFolder().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return fullPath.StartsWith(root, comparison);
        }

        private static string ToStoredMediaPath(string fullPath)
        {
            return "media/" + Path.GetFileName(fullPath);
        }

        private static string GetPortableFileName(string path)
        {
            string normalized = path.Replace('\\', '/');
            int lastSeparator = normalized.LastIndexOf('/');
            return lastSeparator >= 0 ? normalized.Substring(lastSeparator + 1) : normalized;
        }

        private static bool IsWindowsAbsolutePath(string path)
        {
            return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
                   (path[2] == '\\' || path[2] == '/');
        }
    }
}
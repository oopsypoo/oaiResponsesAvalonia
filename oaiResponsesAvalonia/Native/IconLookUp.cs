using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace oaiResponsesAvalonia.Native
{
    /// <summary>
    /// Provides functionality to look up and load application icons on Linux systems.
    /// Used to create Avalonia controls representing these icons.
    /// </summary>
    internal static class IconLookup
    {
        private static readonly string[] ImageExtensions = { ".svg", ".png", ".jpg", ".jpeg", ".bmp" };
        private static readonly string[] FallbackAppDirectories =
        {
            "scalable/apps", "32x32/apps", "24x24/apps", "48x48/apps", "64x64/apps",
            "22x22/apps", "128x128/apps", "256x256/apps", "16x16/apps", "apps"
        };
        private static readonly ConcurrentDictionary<string, Lazy<IconAsset?>> Cache = new(StringComparer.Ordinal);
        private static readonly Lazy<IReadOnlyList<string>> DataDirectories = new(GetDataDirectories);
        private static readonly Lazy<IReadOnlyList<string>> AppIconDirectories = new(GetAppIconDirectories);

        private sealed record IconAsset(IImage Image);

        public static Control? CreateControl(string? icon, double size)
        {
            if (string.IsNullOrWhiteSpace(icon) || !OperatingSystem.IsLinux())
                return null;

            var asset = Cache.GetOrAdd(icon, static value => new Lazy<IconAsset?>(
                () => Load(value), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

            return asset is null
                ? null
                : new Image { Source = asset.Image, Width = size, Height = size };
        }

        private static IconAsset? Load(string icon)
        {
            foreach (string value in GetIconValues(icon))
            {
                foreach (string path in GetCandidatePaths(value))
                {
                    try
                    {
                        using var stream = File.OpenRead(path);
                        if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
                            return new IconAsset(new SvgImage { Source = SvgSource.LoadFromStream(stream) });

                        return new IconAsset(Bitmap.DecodeToWidth(stream, 32));
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> GetIconValues(string icon)
        {
            string value = icon.Trim();
            string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2 && parts[0] == "." && parts[1] == "GThemedIcon")
            {
                foreach (string name in parts.Skip(2))
                    yield return name;
                yield break;
            }

            if (value.StartsWith("themed:", StringComparison.OrdinalIgnoreCase))
            {
                yield return value[7..];
                yield break;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                yield return uri.LocalPath;
                yield break;
            }

            yield return value;
        }

        private static IEnumerable<string> GetCandidatePaths(string icon)
        {
            if (Path.IsPathFullyQualified(icon))
            {
                foreach (string path in WithExtensions(icon))
                    if (File.Exists(path))
                        yield return path;
                yield break;
            }

            string name = Path.GetFileName(icon);
            string extension = Path.GetExtension(name);
            if (ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                name = name[..^extension.Length];

            if (string.IsNullOrWhiteSpace(name))
                yield break;

            foreach (string directory in AppIconDirectories.Value)
            {
                foreach (string path in WithExtensions(Path.Combine(directory, name)))
                    if (File.Exists(path))
                        yield return path;
            }

            foreach (string dataDirectory in DataDirectories.Value)
                foreach (string path in WithExtensions(Path.Combine(dataDirectory, "pixmaps", name)))
                    if (File.Exists(path))
                        yield return path;
        }

        private static IEnumerable<string> WithExtensions(string path)
        {
            string extension = Path.GetExtension(path);
            if (ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
                yield break;
            }

            foreach (string supportedExtension in ImageExtensions)
                yield return path + supportedExtension;
        }

        private static IReadOnlyList<string> GetAppIconDirectories()
        {
            var result = new List<string>();
            foreach (string dataDirectory in DataDirectories.Value)
            {
                string iconsRoot = Path.Combine(dataDirectory, "icons");
                foreach (string themeDirectory in EnumerateDirectories(iconsRoot)
                    .OrderBy(path => Path.GetFileName(path).Equals("hicolor", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(path => path, StringComparer.Ordinal))
                {
                    var appDirectories = ReadAppDirectories(themeDirectory);
                    if (appDirectories.Count > 0)
                        result.AddRange(appDirectories);
                }
            }

            return result;
        }

        private static IReadOnlyList<string> ReadAppDirectories(string themeDirectory)
        {
            var result = new List<string>();
            string indexPath = Path.Combine(themeDirectory, "index.theme");
            if (File.Exists(indexPath))
            {
                try
                {
                    bool inIconThemeSection = false;
                    foreach (string line in File.ReadLines(indexPath))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("[", StringComparison.Ordinal))
                        {
                            inIconThemeSection = trimmed.Equals("[Icon Theme]", StringComparison.OrdinalIgnoreCase);
                            continue;
                        }

                        if (!inIconThemeSection)
                            continue;

                        int separator = trimmed.IndexOf('=');
                        if (separator < 0 ||
                            !(trimmed[..separator].Trim().Equals("Directories", StringComparison.OrdinalIgnoreCase) ||
                              trimmed[..separator].Trim().Equals("ScaledDirectories", StringComparison.OrdinalIgnoreCase)))
                            continue;

                        foreach (string directory in trimmed[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            string relativePath = directory.Trim();
                            if (relativePath.Equals("apps", StringComparison.OrdinalIgnoreCase) ||
                                relativePath.EndsWith("/apps", StringComparison.OrdinalIgnoreCase))
                            {
                                string appDirectory = Path.Combine(themeDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                                if (Directory.Exists(appDirectory))
                                    result.Add(appDirectory);
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            if (result.Count == 0)
                foreach (string relativePath in FallbackAppDirectories)
                {
                    string appDirectory = Path.Combine(themeDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(appDirectory))
                        result.Add(appDirectory);
                }

            return result;
        }

        private static IReadOnlyList<string> GetDataDirectories()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");
            string xdgDataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share";
            return new[]
                {
                    xdgDataHome,
                    Path.Combine(home, ".local", "share", "flatpak", "exports", "share"),
                    "/var/lib/flatpak/exports/share"
                }
                .Concat(xdgDataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static IEnumerable<string> EnumerateDirectories(string path)
        {
            try
            {
                return Directory.EnumerateDirectories(path).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}

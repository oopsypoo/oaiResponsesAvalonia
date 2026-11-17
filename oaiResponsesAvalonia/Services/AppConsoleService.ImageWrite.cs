using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace oaiResponsesAvalonia.Services;

// img-context write commands. Every write is confined to the User Images folder; Application Images stay read-only.
internal sealed partial class AppConsoleService
{
    private static readonly string[] ImageWriteCommands = ["mkdir", "cp", "mv", "resize", "rotate", "convert", "filter"];

    private static IReadOnlyDictionary<string, string> Map(params string[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < pairs.Length; index += 2)
            map[pairs[index]] = pairs[index + 1];
        return map;
    }

    private static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string> first, params string[] more)
    {
        var map = new Dictionary<string, string>(first, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in Map(more))
            map[pair.Key] = pair.Value;
        return map;
    }

    private static readonly IReadOnlyDictionary<string, string> OutputValueOptions = Map("-d", "dir", "--dir", "dir", "-o", "output", "--output", "output", "-b", "body", "--body", "body");
    private static readonly IReadOnlyDictionary<string, string> ForceFlag = Map("--force", "force");
    private static readonly IReadOnlyDictionary<string, string> ProcessingFlags = Map("--force", "force", "--no-transparency", "notransparency", "--exact", "exact");
    private static readonly IReadOnlyDictionary<string, string> ProcessingValueOptions = Merge(OutputValueOptions, "-q", "quality", "--quality", "quality", "--background", "background");

    private sealed record ImageOutputPlan(string Directory, string? FileName, string? Body, bool Force, int BodyStart);

    private string ExecuteImageWrite(string command, string[] args)
    {
        EnsureImageLibrary();
        return command switch
        {
            "mkdir" => MakeImageDirectories(args),
            "rm" => RemoveUserItems(args),
            "cp" or "mv" => CopyOrMoveImages(command, args),
            "resize" => ResizeImages(args),
            "rotate" => RotateImages(args),
            "convert" => ConvertImages(args),
            "filter" => FilterImages(args),
            _ => throw new InvalidOperationException($"unknown image command: {command}")
        };
    }

    private string MakeImageDirectories(string[] args)
    {
        ImgCommandArguments parsed = ImgCommandArguments.Parse(args, Map(), Map("-p", "parents", "--parents", "parents"));
        if (parsed.Positionals.Count == 0)
            throw new InvalidOperationException("usage: mkdir [-p] <folder ...>");

        bool parents = parsed.Has("parents");
        return RunImageBatch(parsed.Positionals, path =>
        {
            string full = ResolveUserWritePath(path);
            if (Directory.Exists(full) && parents)
                return $"exists: {FormatUserImagePath(full)}";
            _imageLibrary!.CreateUserDirectory(full, parents);
            return $"created {FormatUserImagePath(full)}";
        }, path => path);
    }

    private string RemoveUserItems(string[] args)
    {
        ImgCommandArguments parsed = ImgCommandArguments.Parse(args, Map(), Map("-r", "recursive", "-R", "recursive", "--recursive", "recursive"));
        if (parsed.Positionals.Count == 0)
            throw new InvalidOperationException("usage: rm [-r] <image-or-folder ...>");
        if (_imageLocation == ImageBrowserLocation.ApplicationImages)
            throw new InvalidOperationException("rm works only inside the User Images folder; Application Images cannot be deleted.");

        bool recursive = parsed.Has("recursive");
        var targets = new List<string>();
        foreach (string pattern in parsed.Positionals)
        {
            if (!HasWildcard(pattern))
            {
                targets.Add(ResolveUserWritePath(pattern));
                continue;
            }

            string[] matches = ExpandFileMatches(pattern, workspace: false, images: true)
                .Where(path => ImgCommandHelpers.TryGetFormatExtension(Path.GetExtension(path)) is not null).ToArray();
            if (matches.Length == 0)
                throw new FileNotFoundException($"no images match pattern: {pattern}");
            targets.AddRange(matches);
        }

        string[] distinct = targets.Distinct(PathComparer).ToArray();
        if (distinct.Length > ImgCommandHelpers.MaximumBatchFiles)
            throw new InvalidOperationException($"{distinct.Length} items match; the limit is {ImgCommandHelpers.MaximumBatchFiles} per command.");

        return RunImageBatch(distinct, full =>
        {
            string display = FormatUserImagePath(full);
            if (string.Equals(Path.GetFullPath(full).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(_userImagesRoot!).TrimEnd(Path.DirectorySeparatorChar), GetPathComparison()))
                throw new InvalidOperationException("cannot delete the User Images folder itself.");
            EnsureImageNoReparsePoints(ImageBrowserLocation.UserImages, full);

            if (Directory.Exists(full))
            {
                if (!recursive)
                    throw new InvalidOperationException($"{display} is a folder (use -r to delete it and its contents).");
                if (Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories).Any(IsReparsePoint))
                    throw new InvalidOperationException($"{display} contains a link or reparse point; refusing to delete it.");
                string current = _imageCurrentDirectory!;
                Directory.Delete(full, recursive: true);
                if (!Directory.Exists(current))
                    _imageCurrentDirectory = _imageLibrary!.GetRoot(ImageBrowserLocation.UserImages);
                return $"removed {display}/";
            }

            if (!File.Exists(full))
                throw new FileNotFoundException($"no such image or folder: {display}");
            if (ImgCommandHelpers.TryGetFormatExtension(Path.GetExtension(full)) is null)
                throw new InvalidOperationException($"{display} is not a supported image file.");
            File.Delete(full);
            return $"removed {display}";
        }, Path.GetFileName);
    }

    private string CopyOrMoveImages(string command, string[] args)
    {
        bool move = command == "mv";
        ImgCommandArguments parsed = ImgCommandArguments.Parse(args, OutputValueOptions, ForceFlag);
        bool explicitTarget = parsed.Values.Count > 0;
        List<string> patterns = parsed.Positionals.ToList();
        string? positionalDestination = null;
        if (!explicitTarget && patterns.Count >= 2)
        {
            positionalDestination = patterns[^1];
            patterns.RemoveAt(patterns.Count - 1);
        }

        if (patterns.Count == 0)
            throw new InvalidOperationException(move
                ? "usage: mv <image-or-pattern ...> <destination> | mv <image-or-pattern ...> [-d <dir>] [-o <name> | -b <body>] [--force]"
                : "usage: cp <image-or-pattern ...> [destination] | cp <image-or-pattern ...> [-d <dir>] [-o <name> | -b <body>] [--force]");
        if (move && !explicitTarget && positionalDestination is null)
            throw new InvalidOperationException("mv needs a destination: a folder, a new name, or -d/-o/-b.");

        string[] sources = ExpandImageSources(patterns);
        if (move && sources.FirstOrDefault(source => !IsInUserImages(source)) is { } outside)
            throw new InvalidOperationException($"mv works only inside the User Images folder; '{Path.GetFileName(outside)}' is in Application Images (use cp to copy it).");

        ImageOutputPlan plan = positionalDestination is null
            ? CreateOutputPlan(parsed, sources.Length)
            : CreatePlanFromDestination(positionalDestination, parsed.Has("force"), sources.Length);
        return RunImageBatch(sources, source =>
        {
            string destination = ResolveImageDestination(source, sources, sources.Length, Array.IndexOf(sources, source), plan, null, null, keepExtension: true, isMove: move);
            string written = move
                ? _imageLibrary!.MoveUserFile(source, destination, plan.Force)
                : _imageLibrary!.CopyToUserImages(source, destination, plan.Force);
            return $"{Path.GetFileName(source)} -> {FormatUserImagePath(written)}";
        }, Path.GetFileName);
    }

    private string ResizeImages(string[] args)
    {
        ImgCommandArguments parsed = ImgCommandArguments.Parse(args, Merge(ProcessingValueOptions, "-s", "size", "--size", "size"), ProcessingFlags);
        string spec = parsed.Value("size") ?? throw new InvalidOperationException("usage: resize <image-or-pattern ...> -s <W=px|H=px|percent%|WxH --exact> [-d <dir>] [-o <name> | -b <body>] [-q N] [--no-transparency] [--background <colour>] [--force]");
        ImgResizeSpec size = ImgCommandHelpers.ParseResizeSpec(spec, parsed.Has("exact"));
        string[] sources = ExpandImageSources(RequirePositionals(parsed, "resize"));

        return ProcessImages(sources, parsed, "resized", null, null, source =>
        {
            if (size.Percent is double percent)
            {
                ImageInfo info = Image.Identify(source);
                return new ImageProcessingOptions(ResizeWidth: ImgCommandHelpers.ScaleDimension(info.Width, percent));
            }
            return new ImageProcessingOptions(ResizeWidth: size.Width, ResizeHeight: size.Height, KeepAspectRatio: !parsed.Has("exact"));
        });
    }

    private string RotateImages(string[] args)
    {
        ImgCommandArguments parsed = ImgCommandArguments.Parse(args, Merge(ProcessingValueOptions, "-a", "angle", "--angle", "angle", "--canvas", "canvas"), ProcessingFlags);
        string canvas = parsed.Value("canvas")?.ToLowerInvariant() ?? "expand";
        if (canvas is not ("expand" or "keep"))
            throw new InvalidOperationException("--canvas must be 'expand' (default) or 'keep'.");
        string angleText = parsed.Value("angle") ?? throw new InvalidOperationException("usage: rotate <image-or-pattern ...> -a <degrees> [--canvas expand|keep] [-d <dir>] [-o <name> | -b <body>] [-q N] [--no-transparency] [--background <colour>] [--force]");
        if (!double.TryParse(angleText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double angle))
            throw new InvalidOperationException("angle must be a number of degrees (positive is clockwise).");
        (int? rightAngle, double? freeAngle) = ImgCommandHelpers.NormalizeAngle(angle);
        string[] sources = ExpandImageSources(RequirePositionals(parsed, "rotate"));

        return ProcessImages(sources, parsed, "rotated", null, null, _ => new ImageProcessingOptions(RotationDegrees: rightAngle ?? 0, FreeRotationDegrees: freeAngle, KeepCanvasSize: canvas == "keep"));
    }

    private string ConvertImages(string[] args)
    {
        ImgCommandArguments parsed = ImgCommandArguments.Parse(args, Merge(ProcessingValueOptions, "-t", "to", "--to", "to", "-s", "size", "--size", "size"), ProcessingFlags);
        List<string> patterns = parsed.Positionals.ToList();
        string? extension = null;
        if (parsed.Value("to") is { } toText)
            extension = ImgCommandHelpers.TryGetFormatExtension(toText) ?? throw new InvalidOperationException("target format must be png, jpg, webp, bmp, or ico.");
        else if (patterns.Count >= 2 && !HasWildcard(patterns[^1]) && ImgCommandHelpers.TryGetFormatExtension(patterns[^1]) is { } trailing)
        {
            extension = trailing;
            patterns.RemoveAt(patterns.Count - 1);
        }
        else if (parsed.Value("output") is { } outputName && Path.GetExtension(outputName).Length > 0)
            extension = ImgCommandHelpers.TryGetFormatExtension(Path.GetExtension(outputName)) ?? throw new InvalidOperationException("target format must be png, jpg, webp, bmp, or ico.");

        if (extension is null || patterns.Count == 0)
            throw new InvalidOperationException("usage: convert <image-or-pattern ...> <png|jpg|webp|bmp|ico> [-s <ico sizes>] [-q N] [--no-transparency] [--background <colour>] [-d <dir>] [-o <name> | -b <body>] [--force]");

        IReadOnlyList<int>? icoSizes = null;
        if (parsed.Value("size") is { } sizeText)
        {
            if (extension != ".ico")
                throw new InvalidOperationException("-s sets frame sizes and is only valid when converting to ico.");
            icoSizes = ImgCommandHelpers.ParseIcoSizes(sizeText);
        }

        string[] sources = ExpandImageSources(patterns);
        return ProcessImages(sources, parsed, null, extension, icoSizes, _ => new ImageProcessingOptions());
    }

    private string FilterImages(string[] args)
    {
        ImgCommandArguments parsed = ImgCommandArguments.Parse(args, Merge(ProcessingValueOptions, "-n", "amount", "--amount", "amount"), ProcessingFlags);
        string usage = $"usage: filter <{string.Join("|", ImgCommandHelpers.FilterNames)}> <image-or-pattern ...> [-n <amount>] [-d <dir>] [-o <name> | -b <body>] [-q N] [--no-transparency] [--background <colour>] [--force]";
        if (parsed.Positionals.Count < 2)
            throw new InvalidOperationException(usage);

        string filter = parsed.Positionals[0].ToLowerInvariant();
        if (!ImgCommandHelpers.FilterNames.Contains(filter))
            throw new InvalidOperationException($"unknown filter '{parsed.Positionals[0]}'. Available: {string.Join(", ", ImgCommandHelpers.FilterNames)}.");

        float? amount = null;
        if (parsed.Value("amount") is { } amountText)
        {
            if (filter is "grayscale" or "sepia" or "invert")
                throw new InvalidOperationException($"{filter} has no adjustable amount.");
            float maximum = filter is "blur" or "sharpen" ? 50f : 10f;
            if (!float.TryParse(amountText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value) || !(value > 0f && value <= maximum))
                throw new InvalidOperationException($"amount must be greater than 0 and at most {maximum:0} (brightness/contrast: 1 = unchanged; blur/sharpen: strength).");
            amount = value;
        }

        string[] sources = ExpandImageSources(parsed.Positionals.Skip(1));
        return ProcessImages(sources, parsed, filter, null, null, _ => new ImageProcessingOptions(Filter: filter, FilterAmount: amount));
    }

    private string ProcessImages(string[] sources, ImgCommandArguments parsed, string? suffix, string? targetExtension, IReadOnlyList<int>? icoSizes, Func<string, ImageProcessingOptions> optionsFor)
    {
        bool keepTransparency = !parsed.Has("notransparency");
        int quality = parsed.Value("quality") is { } qualityText ? ImgCommandHelpers.ParseQuality(qualityText) : 90;
        Rgba32? background = parsed.Value("background") is { } colour ? ImgCommandHelpers.ParseColor(colour) : null;
        ImageOutputPlan plan = CreateOutputPlan(parsed, sources.Length);

        return RunImageBatch(sources, source =>
        {
            ImageProcessingOptions options = optionsFor(source);
            string destination = ResolveImageDestination(source, sources, sources.Length, Array.IndexOf(sources, source), plan, suffix, targetExtension, keepExtension: false, isMove: false);
            string extension = Path.GetExtension(destination);
            byte[] bytes = _imageLibrary!.ConvertToBytes(source, options, extension, keepTransparency, extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ? icoSizes : null, quality, background);
            string written = _imageLibrary.WriteUserFile(destination, bytes, plan.Force);
            return $"{Path.GetFileName(source)} -> {FormatUserImagePath(written)}";
        }, Path.GetFileName);
    }

    private static List<string> RequirePositionals(ImgCommandArguments parsed, string command) =>
        parsed.Positionals.Count > 0 ? parsed.Positionals : throw new InvalidOperationException($"{command} needs at least one image or pattern.");

    private string RunImageBatch(IReadOnlyList<string> items, Func<string, string> action, Func<string, string> label)
    {
        if (items.Count == 1)
            return action(items[0]);

        var lines = new List<string>();
        int succeeded = 0, failed = 0;
        foreach (string item in items)
        {
            try
            {
                lines.Add(action(item));
                succeeded++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                lines.Add($"error: {label(item)}: {ex.Message}");
            }
        }
        lines.Add($"done: {succeeded} ok, {failed} failed");
        return string.Join("\n", lines);
    }

    private string[] ExpandImageSources(IEnumerable<string> patterns)
    {
        if (_imageLocation is null || _imageCurrentDirectory is null)
            throw new InvalidOperationException("select an image root with 'cd images' or 'cd user' before naming source images.");

        var sources = new List<string>();
        foreach (string pattern in patterns)
        {
            string[] matches = ExpandFileMatches(pattern, workspace: false, images: true);
            if (HasWildcard(pattern))
                matches = matches.Where(path => ImgCommandHelpers.TryGetFormatExtension(Path.GetExtension(path)) is not null).ToArray();
            else if (!File.Exists(matches[0]))
                throw new FileNotFoundException($"no such image: {pattern}");
            if (matches.Length == 0)
                throw new FileNotFoundException($"no images match pattern: {pattern}");
            sources.AddRange(matches);
        }

        string[] distinct = sources.Distinct(PathComparer).ToArray();
        if (distinct.Length > ImgCommandHelpers.MaximumBatchFiles)
            throw new InvalidOperationException($"{distinct.Length} images match; the limit is {ImgCommandHelpers.MaximumBatchFiles} per command.");
        return distinct;
    }

    private string GetUserWriteBase() =>
        _imageLocation == ImageBrowserLocation.UserImages && _imageCurrentDirectory is not null ? _imageCurrentDirectory : _userImagesRoot!;

    // Writes always land in User Images: relative paths start at the current User Images folder (or its root when
    // browsing Application Images or the img root), and a leading / starts at the User Images root.
    private string ResolveUserWritePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("path cannot be empty.");

        string normalized = path.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) && !path.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidOperationException("absolute host paths are not available; use a path relative to the User Images folder.");

        string prefix = "/" + _userImagesDisplayName;
        if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[(prefix.Length)..];

        string candidate = path.StartsWith("/", StringComparison.Ordinal)
            ? Path.Combine(_userImagesRoot!, normalized.TrimStart(Path.DirectorySeparatorChar))
            : Path.Combine(GetUserWriteBase(), normalized);
        string full = Path.GetFullPath(candidate);
        EnsureImageNoReparsePoints(ImageBrowserLocation.UserImages, full);
        return full;
    }

    private bool IsInUserImages(string fullPath)
    {
        string relative = Path.GetRelativePath(_userImagesRoot!, fullPath);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, GetPathComparison());
    }

    private string FormatUserImagePath(string fullPath)
    {
        string relative = Path.GetRelativePath(_userImagesRoot!, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        return "/" + _userImagesDisplayName + (relative == "." ? string.Empty : "/" + relative);
    }

    private ImageOutputPlan CreateOutputPlan(ImgCommandArguments parsed, int sourceCount)
    {
        string? output = parsed.Value("output");
        string? body = parsed.Value("body");
        if (output is not null && body is not null)
            throw new InvalidOperationException("-o and -b cannot be combined: -o names one file, -b names a numbered batch.");
        if (output is not null && sourceCount > 1)
            throw new InvalidOperationException($"-o names a single file but {sourceCount} images were given; use -b <body> to name and number them.");
        if (output is not null && ImgCommandHelpers.HasInvalidNameCharacters(output))
            throw new InvalidOperationException("-o must be a file name without folders; use -d for the folder.");
        if (body is not null && ImgCommandHelpers.HasInvalidNameCharacters(body))
            throw new InvalidOperationException("-b must be a plain name without folders; use -d for the folder.");

        string directory = parsed.Value("dir") is { } dirText ? ResolveUserWritePath(dirText) : GetUserWriteBase();
        if (!Directory.Exists(directory))
            _imageLibrary!.CreateUserDirectory(directory, createParents: true);
        return new ImageOutputPlan(directory, output, body, parsed.Has("force"), body is null ? 1 : ImgCommandHelpers.GetNextBodyIndex(directory, body));
    }

    private ImageOutputPlan CreatePlanFromDestination(string destination, bool force, int sourceCount)
    {
        string full = ResolveUserWritePath(destination);
        bool trailingSeparator = destination.EndsWith('/') || destination.EndsWith('\\');
        if (trailingSeparator || Directory.Exists(full))
        {
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException($"no such folder: {destination} (use -d to create it).");
            return new ImageOutputPlan(full, null, null, force, 1);
        }

        if (sourceCount > 1)
            throw new InvalidOperationException("with several images the destination must be an existing folder (or use -b <body>).");
        string parent = Path.GetDirectoryName(full) ?? _userImagesRoot!;
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException($"no such folder: {Path.GetRelativePath(_userImagesRoot!, parent).Replace(Path.DirectorySeparatorChar, '/')} (use -d to create it).");
        return new ImageOutputPlan(parent, Path.GetFileName(full), null, force, 1);
    }

    private static string ResolveImageDestination(string source, string[] allSources, int total, int index, ImageOutputPlan plan, string? suffix, string? targetExtension, bool keepExtension, bool isMove)
    {
        string sourceExtension = Path.GetExtension(source);
        string path;
        if (plan.FileName is not null)
        {
            string givenExtension = Path.GetExtension(plan.FileName);
            string stem = givenExtension.Length == 0 ? plan.FileName : Path.GetFileNameWithoutExtension(plan.FileName);
            string extension = givenExtension.Length == 0 ? targetExtension ?? sourceExtension : givenExtension;
            if (givenExtension.Length > 0)
            {
                if (ImgCommandHelpers.TryGetFormatExtension(givenExtension) is null)
                    throw new InvalidOperationException("output name must end in .png, .jpg, .webp, .bmp, or .ico.");
                string? expected = targetExtension ?? (keepExtension ? sourceExtension : null);
                if (expected is not null && !ImgCommandHelpers.NormalizeExtension(givenExtension).Equals(ImgCommandHelpers.NormalizeExtension(expected), StringComparison.Ordinal))
                    throw new InvalidOperationException($"-o extension {givenExtension} conflicts with the {expected} format of this command.");
            }

            path = Path.Combine(plan.Directory, stem + extension);
            if (File.Exists(path) && !plan.Force)
                throw new IOException($"'{Path.GetFileName(path)}' already exists (use --force to overwrite, or -b to auto-number).");
        }
        else
        {
            string extension = targetExtension ?? sourceExtension;
            string stem = plan.Body is not null
                ? total == 1 ? plan.Body : ImgCommandHelpers.FormatBodyStem(plan.Body, plan.BodyStart + index, plan.BodyStart + total - 1)
                : Path.GetFileNameWithoutExtension(source) + (suffix is null ? string.Empty : "_" + suffix);
            string preferred = Path.Combine(plan.Directory, stem + extension);
            if (isMove && string.Equals(Path.GetFullPath(preferred), Path.GetFullPath(source), GetPathComparison()))
                throw new IOException("the image is already at the destination.");
            path = plan.Force && plan.Body is null ? preferred : ImgCommandHelpers.GetAutoNumberedPath(plan.Directory, stem, extension);
        }

        if (allSources.Any(other => string.Equals(Path.GetFullPath(other), Path.GetFullPath(path), GetPathComparison())))
            throw new IOException("the output would overwrite a source image; choose another name or folder.");
        return path;
    }
}

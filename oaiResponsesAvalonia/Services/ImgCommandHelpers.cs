using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace oaiResponsesAvalonia.Services;

/// <summary>Parsed command line of an img write command (mkdir, cp, mv, resize, rotate, convert, filter).</summary>
internal sealed class ImgCommandArguments
{
    public List<string> Positionals { get; } = [];
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

    public string? Value(string name) => Values.TryGetValue(name, out string? value) ? value : null;
    public bool Has(string name) => Flags.Contains(name);

    /// <param name="valueOptions">Maps every accepted spelling (for example "-s", "--size") to its canonical name.</param>
    /// <param name="flagOptions">Same mapping for options without a value.</param>
    public static ImgCommandArguments Parse(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> valueOptions, IReadOnlyDictionary<string, string> flagOptions)
    {
        var result = new ImgCommandArguments();
        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            if (argument.Length < 2 || argument[0] != '-')
            {
                result.Positionals.Add(argument);
                continue;
            }

            string name = argument;
            string? inlineValue = null;
            int equals = argument.StartsWith("--", StringComparison.Ordinal) ? argument.IndexOf('=') : -1;
            if (equals > 0)
            {
                name = argument[..equals];
                inlineValue = argument[(equals + 1)..];
            }

            if (flagOptions.TryGetValue(name, out string? flag) && inlineValue is null)
            {
                result.Flags.Add(flag);
            }
            else if (valueOptions.TryGetValue(name, out string? canonical))
            {
                string value = inlineValue ?? (index + 1 < args.Count ? args[++index] : throw new InvalidOperationException($"{name} requires a value."));
                if (!result.Values.TryAdd(canonical, value))
                    throw new InvalidOperationException($"{name} was given more than once.");
            }
            else
            {
                throw new InvalidOperationException($"unknown option: {argument}");
            }
        }
        return result;
    }
}

internal sealed record ImgResizeSpec(int? Width, int? Height, double? Percent);

internal static class ImgCommandHelpers
{
    public const int MaximumBatchFiles = 200;
    public const int MaximumDimension = 16384;
    public static readonly string[] FilterNames = ["grayscale", "sepia", "invert", "blur", "sharpen", "brightness", "contrast"];

    private static readonly Regex SingleSide = new(@"^([WwHh])=(\d+)$", RegexOptions.Compiled);
    private static readonly Regex Both = new(@"^(\d+)[xX](\d+)$", RegexOptions.Compiled);
    private static readonly Regex PercentSpec = new(@"^(\d+(?:\.\d+)?)%$", RegexOptions.Compiled);

    public static ImgResizeSpec ParseResizeSpec(string spec, bool exact)
    {
        Match side = SingleSide.Match(spec);
        if (side.Success)
        {
            if (exact)
                throw new InvalidOperationException("--exact needs both sizes, for example -s 512x384.");
            int value = ParseDimension(side.Groups[2].Value);
            return char.ToUpperInvariant(side.Groups[1].Value[0]) == 'W' ? new ImgResizeSpec(value, null, null) : new ImgResizeSpec(null, value, null);
        }

        Match both = Both.Match(spec);
        if (both.Success)
        {
            if (!exact)
                throw new InvalidOperationException("WxH changes the aspect ratio; add --exact, or use -s W=<pixels> / -s H=<pixels> to keep it.");
            return new ImgResizeSpec(ParseDimension(both.Groups[1].Value), ParseDimension(both.Groups[2].Value), null);
        }

        Match percent = PercentSpec.Match(spec);
        if (percent.Success)
        {
            if (exact)
                throw new InvalidOperationException("--exact cannot be combined with a percentage.");
            double value = double.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture);
            if (value <= 0 || value > 1000)
                throw new InvalidOperationException("percentage must be greater than 0 and at most 1000.");
            return new ImgResizeSpec(null, null, value);
        }

        throw new InvalidOperationException("size must be W=<pixels>, H=<pixels>, <percent>% or (with --exact) <width>x<height>.");
    }

    private static int ParseDimension(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 1 || value > MaximumDimension)
            throw new InvalidOperationException($"sizes must be between 1 and {MaximumDimension} pixels.");
        return value;
    }

    public static int ScaleDimension(int original, double percent)
    {
        double scaled = Math.Round(original * percent / 100.0);
        if (scaled < 1 || scaled > MaximumDimension)
            throw new InvalidOperationException($"the resulting size must be between 1 and {MaximumDimension} pixels.");
        return (int)scaled;
    }

    /// <summary>Right angles map to lossless rotations; any other angle grows the canvas.</summary>
    public static (int? RightAngle, double? FreeAngle) NormalizeAngle(double angle)
    {
        if (!double.IsFinite(angle))
            throw new InvalidOperationException("angle must be a finite number of degrees.");
        double normalized = ((angle % 360) + 360) % 360;
        if (normalized == 0)
            return (0, null);
        if (normalized is 90 or 180 or 270)
            return ((int)normalized, null);
        return (null, normalized);
    }

    public static IReadOnlyList<int> ParseIcoSizes(string text)
    {
        var sizes = new List<int>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int size) || size is < 1 or > 256)
                throw new InvalidOperationException("ICO sizes must be whole numbers from 1 to 256, for example -s 256,128,64.");
            if (!sizes.Contains(size))
                sizes.Add(size);
        }
        if (sizes.Count == 0)
            throw new InvalidOperationException("provide at least one ICO size, for example -s 256,128,64.");
        return sizes;
    }

    public static Rgba32 ParseColor(string text)
    {
        if (!Color.TryParse(text.Trim(), out Color color))
            throw new InvalidOperationException($"unknown colour '{text}'. Use a name such as white or a hex value such as #ffffff.");
        return color.ToPixel<Rgba32>();
    }

    public static int ParseQuality(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int quality) || quality is < 1 or > 100)
            throw new InvalidOperationException("quality must be a whole number from 1 to 100.");
        return quality;
    }

    /// <summary>Accepts "png", ".png", "jpg", "jpeg", "webp", "bmp" and "ico"; returns the extension with a leading dot.</summary>
    public static string? TryGetFormatExtension(string text)
    {
        return text.TrimStart('.').ToLowerInvariant() switch
        {
            "png" => ".png",
            "jpg" or "jpeg" => ".jpg",
            "webp" => ".webp",
            "bmp" => ".bmp",
            "ico" => ".ico",
            _ => null
        };
    }

    public static string NormalizeExtension(string extension) =>
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : extension.ToLowerInvariant();

    public static bool HasInvalidNameCharacters(string name) =>
        name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\');

    /// <summary>Returns stem.ext, or stem_2.ext, stem_3.ext ... for the first name that is free in the directory.</summary>
    public static string GetAutoNumberedPath(string directory, string stem, string extension)
    {
        string candidate = Path.Combine(directory, stem + extension);
        for (int suffix = 2; PathExists(candidate); suffix++)
            candidate = Path.Combine(directory, $"{stem}_{suffix}{extension}");
        return candidate;
    }

    /// <summary>Next free number for "body_NN" names so a later batch continues after earlier ones.</summary>
    public static int GetNextBodyIndex(string directory, string body)
    {
        if (!Directory.Exists(directory))
            return 1;

        var pattern = new Regex("^" + Regex.Escape(body) + @"_(\d+)(?:_\d+)?\.[^.]+$", RegexOptions.IgnoreCase);
        int highest = 0;
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            Match match = pattern.Match(Path.GetFileName(file));
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                highest = Math.Max(highest, index);
        }
        return highest + 1;
    }

    public static string FormatBodyStem(string body, int index, int totalCount)
    {
        int width = Math.Max(2, totalCount.ToString(CultureInfo.InvariantCulture).Length);
        return $"{body}_{index.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}";
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);
}

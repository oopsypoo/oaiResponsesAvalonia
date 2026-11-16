using System.Reflection;
using oaiResponsesAvalonia.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageInspector.Tests;

public sealed class ImgCommandHelpersTests
{
    [Theory]
    [InlineData("W=512", false, 512, null, null)]
    [InlineData("h=300", false, null, 300, null)]
    [InlineData("800x600", true, 800, 600, null)]
    [InlineData("50%", false, null, null, 50.0)]
    public void ParseResizeSpec_AcceptsDocumentedForms(string spec, bool exact, int? width, int? height, double? percent)
    {
        ImgResizeSpec result = ImgCommandHelpers.ParseResizeSpec(spec, exact);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(percent, result.Percent);
    }

    [Theory]
    [InlineData("800x600", false)]
    [InlineData("W=512", true)]
    [InlineData("0x10", true)]
    [InlineData("abc", false)]
    public void ParseResizeSpec_RejectsAspectRatioChangesWithoutExact(string spec, bool exact) =>
        Assert.Throws<InvalidOperationException>(() => ImgCommandHelpers.ParseResizeSpec(spec, exact));

    [Theory]
    [InlineData(90, 90, null)]
    [InlineData(-90, 270, null)]
    [InlineData(450, 90, null)]
    [InlineData(360, 0, null)]
    public void NormalizeAngle_MapsRightAnglesToLosslessRotation(double angle, int expected, double? free)
    {
        (int? right, double? freeAngle) = ImgCommandHelpers.NormalizeAngle(angle);
        Assert.Equal(expected, right);
        Assert.Equal(free, freeAngle);
    }

    [Fact]
    public void NormalizeAngle_KeepsArbitraryAnglesPositive()
    {
        (int? right, double? freeAngle) = ImgCommandHelpers.NormalizeAngle(-37.5);
        Assert.Null(right);
        Assert.Equal(322.5, freeAngle);
    }

    [Fact]
    public void ParseIcoSizes_DeduplicatesAndRejectsOversizedFrames()
    {
        Assert.Equal([256, 128, 64], ImgCommandHelpers.ParseIcoSizes("256, 128,64,128"));
        Assert.Throws<InvalidOperationException>(() => ImgCommandHelpers.ParseIcoSizes("512"));
    }

    [Fact]
    public void BodyNaming_ContinuesAfterExistingNumbers()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "holiday_01.png"), "x");
            File.WriteAllText(Path.Combine(directory, "holiday_07.jpg"), "x");
            Assert.Equal(8, ImgCommandHelpers.GetNextBodyIndex(directory, "holiday"));
            Assert.Equal("holiday_08", ImgCommandHelpers.FormatBodyStem("holiday", 8, 9));
            Assert.Equal(Path.Combine(directory, "holiday_01_2.png"), ImgCommandHelpers.GetAutoNumberedPath(directory, "holiday_01", ".png"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class ImgConsoleWriteTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    private readonly string _appImages;
    private readonly string _userImages;
    private readonly AppConsoleService _console = new();

    public ImgConsoleWriteTests()
    {
        _appImages = Path.Combine(_root, "app");
        _userImages = Path.Combine(_root, "user");
        var library = new ImageLibraryService(_appImages, _userImages);
        SetField("_imageLibrary", library);
        SetField("_applicationImagesRoot", library.GetRoot(ImageBrowserLocation.ApplicationImages));
        SetField("_userImagesRoot", library.GetRoot(ImageBrowserLocation.UserImages));
        typeof(AppConsoleService).GetProperty(nameof(AppConsoleService.ActiveContext))!.SetValue(_console, ConsoleContext.Images);

        using var image = new Image<Rgba32>(100, 40, new Rgba32(200, 30, 30, 255));
        image.SaveAsPng(Path.Combine(_appImages, "pic.png"));
        image.SaveAsPng(Path.Combine(_appImages, "pic2.png"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void SetField(string name, object value) =>
        typeof(AppConsoleService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_console, value);

    private string Run(string command)
    {
        AppConsoleResult result = _console.Execute(command);
        Assert.NotEqual("error", result.Kind);
        return result.Output;
    }

    private string RunError(string command)
    {
        AppConsoleResult result = _console.Execute(command);
        Assert.Equal("error", result.Kind);
        return result.Output;
    }

    [Fact]
    public void Resize_WritesToUserImagesKeepsAspectAndAutoNumbers()
    {
        Run("cd images");
        Run("resize pic.png -s W=50");
        Run("resize pic.png -s W=50");

        using Image first = Image.Load(Path.Combine(_userImages, "pic_resized.png"));
        Assert.Equal((50, 20), (first.Width, first.Height));
        Assert.True(File.Exists(Path.Combine(_userImages, "pic_resized_2.png")));
        Assert.Equal(2, Directory.GetFiles(_appImages).Length);
        Assert.Contains("WxH", RunError("resize pic.png -s 10x10"));
    }

    [Fact]
    public void Rotate_AnyAngleEnlargesCanvas()
    {
        Run("cd images");
        Run("rotate pic.png -a 45 -o turned");

        using Image rotated = Image.Load(Path.Combine(_userImages, "turned.png"));
        Assert.True(rotated.Height > 80);
    }

    [Fact]
    public void Convert_ToIcoWritesOneFramePerSize()
    {
        Run("cd images");
        Run("convert pic.png ico -s 32,16 -d icons");

        using Image icon = Image.Load(Path.Combine(_userImages, "icons", "pic.ico"));
        Assert.Equal(2, icon.Frames.Count);
        Assert.Contains("only valid", RunError("convert pic.png png -s 32"));
    }

    [Fact]
    public void Batch_BodyNamesNumberAndContinue()
    {
        Run("cd images");
        string output = Run("filter grayscale *.png -b holiday");
        Assert.Contains("done: 2 ok, 0 failed", output);
        Run("filter sepia pic.png pic2.png -b holiday");

        foreach (string name in new[] { "holiday_01.png", "holiday_02.png", "holiday_03.png", "holiday_04.png" })
            Assert.True(File.Exists(Path.Combine(_userImages, name)), name);
        Assert.Contains("-o names a single file", RunError("filter invert *.png -o x"));
    }

    [Fact]
    public void Mkdir_CpMv_StayInsideUserImages()
    {
        Run("mkdir -p a/b");
        Assert.True(Directory.Exists(Path.Combine(_userImages, "a", "b")));
        Assert.Contains("outside", RunError("mkdir ../escape"));

        Run("cd images");
        Run("cp pic.png a/");
        Assert.True(File.Exists(Path.Combine(_userImages, "a", "pic.png")));
        Assert.Contains("Application Images", RunError("mv pic.png a"));

        Run("cd /");
        Run("cd user");
        Run("mv a/pic.png renamed");
        Assert.True(File.Exists(Path.Combine(_userImages, "renamed.png")));
        Assert.False(File.Exists(Path.Combine(_userImages, "a", "pic.png")));
        Assert.Contains("outside", RunError("cp renamed.png -d ../out"));
    }

    [Fact]
    public void ExplicitOutputThatExistsIsRefusedWithoutForce()
    {
        Run("cd images");
        Run("filter invert pic.png -o same");
        Assert.Contains("already exists", RunError("filter invert pic.png -o same"));
        Run("filter invert pic.png -o same --force");
    }
}

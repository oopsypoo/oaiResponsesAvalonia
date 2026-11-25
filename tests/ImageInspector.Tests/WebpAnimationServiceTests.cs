using oaiResponsesAvalonia.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageInspector.Tests;

public sealed class WebpAnimationServiceTests
{
    [Fact]
    public void CreateWebpAnimationBytes_EncodesFramesTimingAndInfiniteLoop()
    {
        string root = Path.Combine(Path.GetTempPath(), "ImageInspector.WebpAnimationTests", Guid.NewGuid().ToString("N"));
        string userRoot = Path.Combine(root, "user");
        Directory.CreateDirectory(root);
        try
        {
            string firstPath = WriteSolidPng(root, "first.png", new Rgba32(240, 20, 30, 255));
            string secondPath = WriteSolidPng(root, "second.png", new Rgba32(20, 40, 240, 255));
            var service = new ImageLibraryService(Path.Combine(root, "application"), userRoot);

            byte[] bytes = service.CreateWebpAnimationBytes(
                [firstPath, secondPath], 16, 12, loopForever: true, loopCount: 1,
                method: 0, quality: 100, frameDurationMilliseconds: 120);

            using Image<Rgba32> decoded = Image.Load<Rgba32>(bytes);
            Assert.Equal(2, decoded.Frames.Count);
            Assert.Equal((ushort)0, decoded.Metadata.GetWebpMetadata().RepeatCount);
            Assert.Equal((uint)120, decoded.Frames[0].Metadata.GetWebpMetadata().FrameDelay);
            Assert.Equal((uint)120, decoded.Frames[1].Metadata.GetWebpMetadata().FrameDelay);

            ImageAssetPreview preview = service.CreateWebpAnimationPreview(bytes);
            Assert.Equal(2, preview.FrameCount);
            Assert.Equal(16, preview.Width);
            Assert.Equal(12, preview.Height);
            Assert.Equal(2, preview.AnimationFrames.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateWebpAnimationBytes_PersistsFiniteRepeatCount()
    {
        string root = Path.Combine(Path.GetTempPath(), "ImageInspector.WebpAnimationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string firstPath = WriteSolidPng(root, "first.png", new Rgba32(255, 0, 0, 255));
            string secondPath = WriteSolidPng(root, "second.png", new Rgba32(0, 0, 255, 255));
            var service = new ImageLibraryService(Path.Combine(root, "application"), Path.Combine(root, "user"));

            byte[] bytes = service.CreateWebpAnimationBytes(
                [firstPath, secondPath], 8, 8, loopForever: false, loopCount: 3,
                method: 1, quality: 90, frameDurationMilliseconds: 75);

            using Image<Rgba32> decoded = Image.Load<Rgba32>(bytes);
            Assert.Equal((ushort)3, decoded.Metadata.GetWebpMetadata().RepeatCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WriteSolidPng(string directory, string fileName, Rgba32 color)
    {
        string path = Path.Combine(directory, fileName);
        using var image = new Image<Rgba32>(4, 4, color);
        image.SaveAsPng(path, new PngEncoder());
        return path;
    }
}

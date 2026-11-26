using oaiResponsesAvalonia.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
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

    [Fact]
    public void ReadWebpAnimationForEditing_ExtractsFramesAndPreservesIndividualDurations()
    {
        string root = Path.Combine(Path.GetTempPath(), "ImageInspector.WebpAnimationTests", Guid.NewGuid().ToString("N"));
        string userRoot = Path.Combine(root, "user");
        Directory.CreateDirectory(root);
        try
        {
            string firstPath = WriteSolidPng(root, "first.png", new Rgba32(240, 20, 30, 255));
            string secondPath = WriteSolidPng(root, "second.png", new Rgba32(20, 40, 240, 255));
            var service = new ImageLibraryService(Path.Combine(root, "application"), userRoot);
            var sourceFrames = new[]
            {
                new WebpAnimationFrameInput(firstPath, null, 80),
                new WebpAnimationFrameInput(secondPath, null, 240)
            };
            byte[] original = service.CreateWebpAnimationFromFrames(
                sourceFrames, 16, 12, loopForever: false, loopCount: 4,
                method: 0, quality: 100, defaultFrameDurationMilliseconds: 100);
            string sourcePath = service.SaveToUserImages(userRoot, "source.webp", original);

            WebpAnimationEditDocument document = service.ReadWebpAnimationForEditing(sourcePath);

            Assert.Equal(16, document.Width);
            Assert.Equal(12, document.Height);
            Assert.Equal((ushort)4, document.RepeatCount);
            Assert.Equal(new[] { 80, 240 }, document.Frames.Select(frame => frame.DurationMilliseconds));

            byte[] edited = service.CreateWebpAnimationFromFrames(
                document.Frames.Reverse()
                    .Select(frame => new WebpAnimationFrameInput(null, frame.PngBytes, frame.DurationMilliseconds))
                    .ToArray(),
                document.Width, document.Height, loopForever: false, loopCount: document.RepeatCount,
                method: 0, quality: 100, defaultFrameDurationMilliseconds: 100);
            using Image<Rgba32> decoded = Image.Load<Rgba32>(edited);
            Assert.Equal((ushort)4, decoded.Metadata.GetWebpMetadata().RepeatCount);
            Assert.Equal((uint)240, decoded.Frames[0].Metadata.GetWebpMetadata().FrameDelay);
            Assert.Equal((uint)80, decoded.Frames[1].Metadata.GetWebpMetadata().FrameDelay);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateWebpAnimationFromFrames_UsesPerFrameGeometryAndMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "ImageInspector.WebpAnimationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string redPath = WriteSolidPng(root, "red.png", new Rgba32(255, 0, 0, 255));
            string bluePath = WriteSolidPng(root, "blue.png", new Rgba32(0, 0, 255, 255));
            var service = new ImageLibraryService(Path.Combine(root, "application"), Path.Combine(root, "user"));
            var frames = new[]
            {
                new WebpAnimationFrameInput(redPath, null, 80, 2, 2, 1, 1, AlphaBlend: false, DisposeAfterFrame: true),
                new WebpAnimationFrameInput(bluePath, null, 170, 2, 2, 0, 0, AlphaBlend: true, DisposeAfterFrame: false)
            };

            byte[] firstFramePreview = service.CreateWebpAnimationFramePreview(frames[0], 4, 4);
            using (Image<Rgba32> preview = Image.Load<Rgba32>(firstFramePreview))
            {
                Assert.Equal(new Rgba32(0, 0, 0, 0), preview[0, 0]);
                Assert.Equal(new Rgba32(255, 0, 0, 255), preview[1, 1]);
                Assert.Equal(new Rgba32(0, 0, 0, 0), preview[3, 3]);
            }

            byte[] encoded = service.CreateWebpAnimationFromFrames(
                frames, 4, 4, loopForever: true, loopCount: 1,
                method: 0, quality: 100, defaultFrameDurationMilliseconds: 100);
            using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);

            Assert.Equal((uint)80, decoded.Frames[0].Metadata.GetWebpMetadata().FrameDelay);
            Assert.Equal((uint)170, decoded.Frames[1].Metadata.GetWebpMetadata().FrameDelay);
            Assert.Equal((byte)0, decoded.Frames[0][0, 0].A);
            Assert.True(decoded.Frames[0][1, 1].R > 200);
            Assert.Equal(FrameBlendMode.Source, decoded.Frames[0].Metadata.GetWebpMetadata().BlendMode);
            Assert.Equal(FrameBlendMode.Over, decoded.Frames[1].Metadata.GetWebpMetadata().BlendMode);
            Assert.Equal(FrameDisposalMode.RestoreToBackground, decoded.Frames[0].Metadata.GetWebpMetadata().DisposalMode);
            Assert.Equal(FrameDisposalMode.DoNotDispose, decoded.Frames[1].Metadata.GetWebpMetadata().DisposalMode);

            string encodedPath = service.SaveToUserImages(Path.Combine(root, "user"), "metadata.webp", encoded);
            WebpAnimationEditDocument editDocument = service.ReadWebpAnimationForEditing(encodedPath);
            Assert.Equal(new[] { false, true }, editDocument.Frames.Select(frame => frame.AlphaBlend));
            Assert.Equal(new[] { true, false }, editDocument.Frames.Select(frame => frame.DisposeAfterFrame));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateWebpAnimationFromFrames_OddCanvasDimensionsRoundTrip()
    {
        string root = Path.Combine(Path.GetTempPath(), "ImageInspector.WebpAnimationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string redPath = WriteSolidPng(root, "red.png", new Rgba32(255, 0, 0, 255));
            string bluePath = WriteSolidPng(root, "blue.png", new Rgba32(0, 0, 255, 255));
            var service = new ImageLibraryService(Path.Combine(root, "application"), Path.Combine(root, "user"));
            var frames = new[]
            {
                new WebpAnimationFrameInput(redPath, null, 90, 3, 2, 1, 0),
                new WebpAnimationFrameInput(bluePath, null, 130, 2, 3, 3, 0)
            };

            byte[] encoded = service.CreateWebpAnimationFromFrames(
                frames, 5, 3, loopForever: true, loopCount: 1,
                method: 0, quality: 100, defaultFrameDurationMilliseconds: 100);
            using Image<Rgba32> decoded = Image.Load<Rgba32>(encoded);

            Assert.Equal(5, decoded.Width);
            Assert.Equal(3, decoded.Height);
            Assert.Equal(2, decoded.Frames.Count);
            Assert.Equal((uint)90, decoded.Frames[0].Metadata.GetWebpMetadata().FrameDelay);
            Assert.Equal((uint)130, decoded.Frames[1].Metadata.GetWebpMetadata().FrameDelay);
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

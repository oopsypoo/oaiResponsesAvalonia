using Avalonia;
using oaiResponsesAvalonia.Views;

namespace ImageInspector.Tests;

public class ImageInspectorGeometryTests
{
    [Fact]
    public void FitKeepsSmallImagesAtOriginalSize()
    {
        var viewport = new Size(800, 600);
        double scale = ImageInspectorGeometry.FitScale(40, 30, viewport);
        var region = ImageInspectorGeometry.VisibleRegion(40, 30, viewport, default, scale, 1)!.Value;

        Assert.Equal(1, scale);
        Assert.Equal(new PixelRect(0, 0, 40, 30), region.Source);
        Assert.Equal(new PixelSize(40, 30), region.Raster);
        Assert.Equal(380, region.Left);
        Assert.Equal(285, region.Top);
    }

    [Fact]
    public void FitRasterUsesViewportAndPreservesAspectRatio()
    {
        var viewport = new Size(800, 600);
        double scale = ImageInspectorGeometry.FitScale(4000, 2000, viewport);
        var region = ImageInspectorGeometry.VisibleRegion(4000, 2000, viewport, default, scale, 1)!.Value;

        Assert.Equal(0.2, scale, 8);
        Assert.Equal(new PixelRect(0, 0, 4000, 2000), region.Source);
        Assert.Equal(new PixelSize(800, 400), region.Raster);
        Assert.Equal(100, region.Top);
    }

    [Fact]
    public void ZoomAndPanRenderOnlyVisibleOriginalPixels()
    {
        var region = ImageInspectorGeometry.VisibleRegion(
            4000, 3000, new Size(800, 600), new Vector(500, 300), 1, 1)!.Value;

        Assert.Equal(new PixelRect(500, 300, 800, 600), region.Source);
        Assert.Equal(new PixelSize(800, 600), region.Raster);
        Assert.Equal(500, region.Left);
        Assert.Equal(300, region.Top);
    }

    [Fact]
    public void HighDpiUsesAvailableDetailWithoutUpscalingRaster()
    {
        var viewport = new Size(800, 600);
        var region = ImageInspectorGeometry.VisibleRegion(4000, 2000, viewport, default, 0.2, 2)!.Value;
        var zoomed = ImageInspectorGeometry.VisibleRegion(4000, 3000, viewport, default, 2, 2)!.Value;

        Assert.Equal(new PixelSize(1600, 800), region.Raster);
        Assert.Equal(new PixelSize(400, 300), zoomed.Raster);
    }

    [Fact]
    public void FractionalScrollIncludesBoundaryPixels()
    {
        var region = ImageInspectorGeometry.VisibleRegion(
            2000, 2000, new Size(101, 80), new Vector(10.25, 20.25), 0.5, 1)!.Value;

        Assert.Equal(new PixelRect(20, 40, 203, 161), region.Source);
        Assert.Equal(new PixelSize(102, 81), region.Raster);
        Assert.Equal(10, region.Left);
        Assert.Equal(20, region.Top);
    }

    [Fact]
    public void EmptyViewportDoesNotRequestRender()
    {
        Assert.Null(ImageInspectorGeometry.VisibleRegion(100, 100, new Size(0, 0), default, 1, 1));
    }
}

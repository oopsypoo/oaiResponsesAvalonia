using System;
using Avalonia;

namespace oaiResponsesAvalonia.Views;

internal readonly record struct InspectorRenderRegion(
    PixelRect Source,
    PixelSize Raster,
    double Left,
    double Top,
    double Width,
    double Height);

internal static class ImageInspectorGeometry
{
    public static double FitScale(int width, int height, Size viewport) =>
        Math.Min(1, Math.Min(viewport.Width / width, viewport.Height / height));

    public static InspectorRenderRegion? VisibleRegion(
        int width, int height, Size viewport, Vector offset, double zoom, double renderScale)
    {
        if (width <= 0 || height <= 0 || viewport.Width <= 0 || viewport.Height <= 0 || zoom <= 0 || renderScale <= 0)
            return null;

        double canvasWidth = Math.Max(viewport.Width, width * zoom);
        double canvasHeight = Math.Max(viewport.Height, height * zoom);
        double imageLeft = (canvasWidth - width * zoom) / 2;
        double imageTop = (canvasHeight - height * zoom) / 2;
        double left = Math.Max(imageLeft, offset.X);
        double top = Math.Max(imageTop, offset.Y);
        double right = Math.Min(imageLeft + width * zoom, offset.X + viewport.Width);
        double bottom = Math.Min(imageTop + height * zoom, offset.Y + viewport.Height);
        if (right <= left || bottom <= top)
            return null;

        int x = Math.Clamp((int)Math.Floor((left - imageLeft) / zoom), 0, width - 1);
        int y = Math.Clamp((int)Math.Floor((top - imageTop) / zoom), 0, height - 1);
        int endX = Math.Clamp((int)Math.Ceiling((right - imageLeft) / zoom), x + 1, width);
        int endY = Math.Clamp((int)Math.Ceiling((bottom - imageTop) / zoom), y + 1, height);
        int sourceWidth = endX - x;
        int sourceHeight = endY - y;
        double rasterScale = Math.Min(1, zoom * renderScale);
        var raster = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(sourceWidth * rasterScale)),
            Math.Max(1, (int)Math.Ceiling(sourceHeight * rasterScale)));
        return new InspectorRenderRegion(
            new PixelRect(x, y, sourceWidth, sourceHeight), raster,
            imageLeft + x * zoom, imageTop + y * zoom, sourceWidth * zoom, sourceHeight * zoom);
    }
}

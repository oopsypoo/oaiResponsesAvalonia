using Avalonia.Media.Imaging;
using oaiResponsesAvalonia.Services;

namespace oaiResponsesAvalonia.Views;

public sealed class ImageGalleryItem
{
    public ImageGalleryItem(ImageAssetEntry entry, Bitmap? thumbnail)
    {
        FullPath = entry.FullPath;
        Name = entry.Name;
        IsDirectory = entry.IsDirectory;
        Details = entry.Details;
        Thumbnail = thumbnail;
        Icon = entry.IsDirectory ? "📁" : "🖼";
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public string Details { get; }
    public string Icon { get; }
    public Bitmap? Thumbnail { get; }
}

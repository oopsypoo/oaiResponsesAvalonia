using System;
using System.IO;

namespace oaiResponsesAvalonia
{
    public static class ImageInputHelper
    {
        public static string? ToDataUrl(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            var ext = Path.GetExtension(filePath)?.TrimStart('.').ToLowerInvariant();
            if (ext == "jpg")
                ext = "jpeg";

            var bytes = File.ReadAllBytes(filePath);
            var b64 = Convert.ToBase64String(bytes);
            return $"data:image/{ext};base64,{b64}";
        }

        public static string GetMimeType(string? filePath)
        {
            var ext = Path.GetExtension(filePath)?.ToLowerInvariant();

            return ext switch
            {
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };
        }
    }
}
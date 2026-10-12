using System;
using System.Collections.Generic;
using System.IO;

namespace oaiResponsesAvalonia
{
    public static class FileInputHelper
    {
        private static readonly Dictionary<string, string> MimeMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [".txt"] = "text/plain",
                [".css"] = "text/plain",
                [".md"] = "text/markdown",
                [".csv"] = "text/csv",
                [".json"] = "application/json",
                [".xml"] = "application/xml",
                [".log"] = "text/plain",
                [".cs"] = "text/plain",
                [".xaml"] = "text/plain",
                [".js"] = "text/plain",
                [".ts"] = "text/plain",
                [".html"] = "text/html",
                [".htm"] = "text/html",
                [".pdf"] = "application/pdf",
                [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                [".zip"] = "application/zip",

                [".png"] = "image/png",
                [".jpg"] = "image/jpeg",
                [".jpeg"] = "image/jpeg",
                [".gif"] = "image/gif",
                [".bmp"] = "image/bmp",
                [".webp"] = "image/webp"
            };

        public static string GetMimeType(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "application/octet-stream";

            string? ext = Path.GetExtension(path);
            if (string.IsNullOrWhiteSpace(ext))
                return "application/octet-stream";

            return MimeMap.TryGetValue(ext, out string? mime)
                ? mime
                : "application/octet-stream";
        }

        public static bool IsImageMimeType(string mimeType)
        {
            return !string.IsNullOrWhiteSpace(mimeType) &&
                   mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        public static string ToDataUrl(string? filePath, string? mimeType = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return string.Empty;

            mimeType ??= GetMimeType(filePath);

            byte[] bytes = File.ReadAllBytes(filePath);
            string base64 = Convert.ToBase64String(bytes);

            return $"data:{mimeType};base64,{base64}";
        }
    }
}
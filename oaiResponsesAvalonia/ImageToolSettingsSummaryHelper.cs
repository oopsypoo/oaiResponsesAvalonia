using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace oaiResponsesAvalonia
{
    public static class ImageToolSettingsSummaryHelper
    {
        private sealed class Snapshot
        {
            [JsonPropertyName("quality")]
            public string? Quality { get; set; }

            [JsonPropertyName("size")]
            public string? Size { get; set; }

            [JsonPropertyName("output_format")]
            public string? OutputFormat { get; set; }

            [JsonPropertyName("output_compression")]
            public int? OutputCompression { get; set; }

            [JsonPropertyName("background")]
            public string? Background { get; set; }

        }

        public static string Build(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return string.Empty;

            try
            {
                var s = JsonSerializer.Deserialize<Snapshot>(json);
                if (s == null)
                    return string.Empty;

                var parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(s.OutputFormat))
                    parts.Add($"fmt: {s.OutputFormat}");

                if (!string.IsNullOrWhiteSpace(s.Quality))
                    parts.Add($"q: {s.Quality}");

                if (!string.IsNullOrWhiteSpace(s.Size))
                    parts.Add($"size: {s.Size}");

                if (!string.IsNullOrWhiteSpace(s.Background))
                    parts.Add($"bg: {s.Background}");

                if (s.OutputCompression.HasValue)
                    parts.Add($"comp: {s.OutputCompression.Value}");

                return string.Join(" | ", parts);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using oaiResponsesAvalonia.Services;

namespace oaiResponsesAvalonia
{
    public class ModelsResponse
    {
        [JsonPropertyName("object")]
        public string Object { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public List<ModelData> Data { get; set; } = new();
    }

    public class ModelData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    public static class ModelApiService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private const string ModelsEndpoint = "https://api.openai.com/v1/models";
        public static async Task<List<string>> GetAvailableModelsAsync(string apiKey, string? endpoint = null)
        {
            string url = string.IsNullOrWhiteSpace(endpoint)
                ? ModelsEndpoint
                : endpoint;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API key is missing.", nameof(apiKey));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using HttpResponseMessage response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();

            var modelsResponse = JsonSerializer.Deserialize<ModelsResponse>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return modelsResponse?.Data?
                .Select(m => m.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .OrderBy(id => id)
                .ToList()
                ?? new List<string>();
        }
    }
    public static class AvailableModelsStorage
    {
        public static string FilePath => AppPaths.ModelsFilePath;

        public static bool Exists()
        {
            return File.Exists(FilePath) || File.Exists(AppPaths.LegacyModelsFilePath);
        }

        public static List<string> Load()
        {
            AppPaths.EnsureDataDirectory();

            if (!File.Exists(FilePath))
            {
                if (!File.Exists(AppPaths.LegacyModelsFilePath))
                    return new List<string>();

                var legacyModels = Normalize(File.ReadAllLines(AppPaths.LegacyModelsFilePath));
                Save(legacyModels);
                return legacyModels;
            }

            return Normalize(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new List<string>());
        }

        public static void Save(IEnumerable<string> models)
        {
            var list = Normalize(models);

            AppPaths.EnsureDataDirectory();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static List<string> Normalize(IEnumerable<string> models)
        {
            return models
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static void Delete()
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);

            if (File.Exists(AppPaths.LegacyModelsFilePath))
                File.Delete(AppPaths.LegacyModelsFilePath);
        }

        public static bool HasContent()
        {
            return Load().Count > 0;
        }
    }
}
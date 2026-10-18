using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace oaiResponsesAvalonia.Services
{
    public static class McpServerStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static string FilePath => Path.Combine(AppPaths.DataDirectory, "mcp_servers.json");

        public static List<McpServerConfig> Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new List<McpServerConfig>();

                return JsonSerializer.Deserialize<List<McpServerConfig>>(File.ReadAllText(FilePath))
                       ?? new List<McpServerConfig>();
            }
            catch
            {
                return new List<McpServerConfig>();
            }
        }

        public static void Save(IEnumerable<McpServerConfig> servers)
        {
            AppPaths.EnsureDataDirectory();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(servers.ToList(), JsonOptions));
        }
    }
}

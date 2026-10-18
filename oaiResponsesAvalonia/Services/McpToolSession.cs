using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services
{
    // Holds live connections to the enabled MCP servers for the duration of one request.
    public sealed class McpToolSession : IAsyncDisposable
    {
        private const int MaxFunctionNameLength = 64;
        private const int MaxResultCharacters = 60000;

        private sealed record Entry(McpClient Client, string ServerName, string ToolName, McpToolSetting Setting);

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly List<McpClient> _clients = new();
        private readonly List<object> _functionTools = new();

        public IReadOnlyList<object> FunctionTools => _functionTools;
        public bool HasTools => _functionTools.Count > 0;
        public List<string> Warnings { get; } = new();

        // Connects to every enabled server that has at least one enabled tool.
        public static async Task<McpToolSession> ConnectAsync(IEnumerable<McpServerConfig> servers, CancellationToken cancellationToken = default)
        {
            var session = new McpToolSession();

            foreach (var server in servers.Where(s => s.Enabled && s.Tools.Any(t => t.Enabled && t.Available)))
            {
                try
                {
                    await session.AddServerAsync(server, cancellationToken);
                }
                catch (Exception ex)
                {
                    session.Warnings.Add($"MCP server '{server.Name}' is unavailable: {ex.Message}");
                }
            }

            return session;
        }

        private async Task AddServerAsync(McpServerConfig server, CancellationToken cancellationToken)
        {
            var client = await McpClientService.ConnectAsync(server, cancellationToken);
            _clients.Add(client);

            var enabled = server.Tools.Where(t => t.Enabled).ToDictionary(t => t.Name, StringComparer.Ordinal);
            var listed = await client.ListToolsAsync(cancellationToken: cancellationToken);

            foreach (var tool in listed)
            {
                if (!enabled.TryGetValue(tool.Name, out var setting))
                    continue;

                string functionName = CreateFunctionName(server.Name, tool.Name);
                _entries[functionName] = new Entry(client, server.Name, tool.Name, setting);
                _functionTools.Add(new
                {
                    type = "function",
                    name = functionName,
                    description = $"[MCP server: {server.Name}] {tool.Description}",
                    parameters = tool.ProtocolTool.InputSchema
                });
            }
        }

        public bool Owns(string functionName) => _entries.ContainsKey(functionName);

        public bool RequiresApproval(string functionName) =>
            !_entries.TryGetValue(functionName, out var entry) || entry.Setting.RequireApproval;

        public string Describe(string functionName) =>
            _entries.TryGetValue(functionName, out var entry) ? $"{entry.ServerName}/{entry.ToolName}" : functionName;

        public async Task<string> CallAsync(string functionName, string argumentsJson, CancellationToken cancellationToken = default)
        {
            if (!_entries.TryGetValue(functionName, out var entry))
                return Serialize(false, "Unknown MCP tool.");

            try
            {
                var arguments = new Dictionary<string, object?>();
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in doc.RootElement.EnumerateObject())
                        arguments[property.Name] = property.Value.Clone();
                }

                var result = await entry.Client.CallToolAsync(entry.ToolName, arguments, cancellationToken: cancellationToken);
                return Serialize(result.IsError != true, FlattenContent(result));
            }
            catch (Exception ex)
            {
                return Serialize(false, ex.Message);
            }
        }

        private static string FlattenContent(CallToolResult result)
        {
            var text = new StringBuilder();
            foreach (var block in result.Content)
            {
                if (text.Length > 0)
                    text.AppendLine();

                text.Append(block is TextContentBlock t ? t.Text : $"[{block.Type} content omitted]");
            }

            if (text.Length == 0 && result.StructuredContent != null)
                text.Append(result.StructuredContent.ToString());

            if (text.Length > MaxResultCharacters)
            {
                text.Length = MaxResultCharacters;
                text.Append("\n[truncated]");
            }

            return text.ToString();
        }

        private static string Serialize(bool ok, string content) =>
            ok
                ? JsonSerializer.Serialize(new { ok = true, content })
                : JsonSerializer.Serialize(new { ok = false, error = content });

        // Function names must match ^[a-zA-Z0-9_-]{1,64}$.
        private static string CreateFunctionName(string serverName, string toolName)
        {
            string name = $"mcp__{Sanitize(serverName)}__{Sanitize(toolName)}";
            if (name.Length <= MaxFunctionNameLength)
                return name;

            string hash = ((uint)StringComparer.Ordinal.GetHashCode(name)).ToString("x8");
            return name[..(MaxFunctionNameLength - 9)] + "_" + hash;
        }

        private static string Sanitize(string value) =>
            Regex.Replace(value ?? string.Empty, "[^a-zA-Z0-9_-]", "_");

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients)
            {
                try { await client.DisposeAsync(); }
                catch { /* shutting down; nothing useful to do */ }
            }

            _clients.Clear();
        }
    }
}

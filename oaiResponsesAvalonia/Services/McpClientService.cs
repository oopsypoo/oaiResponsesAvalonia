using ModelContextProtocol.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services
{
    public static class McpClientService
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

        // Connects (handshake), lists the server's tools and disconnects again.
        public static async Task<List<McpToolInfo>> ListToolsAsync(McpServerConfig server, CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);

            await using var client = await McpClient.CreateAsync(CreateTransport(server), cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);

            return tools
                .Select(t => new McpToolInfo(t.Name, t.Description ?? string.Empty, t.ProtocolTool.Annotations?.ReadOnlyHint))
                .ToList();
        }

        // Opens a long-lived connection; the caller disposes it.
        public static async Task<McpClient> ConnectAsync(McpServerConfig server, CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            return await McpClient.CreateAsync(CreateTransport(server), cancellationToken: timeout.Token);
        }

        public static string DescribeCommand(McpServerConfig server) =>
            $"{server.Command} {server.Arguments}".Trim();

        private static IClientTransport CreateTransport(McpServerConfig server)
        {
            if (server.IsHttp)
            {
                if (!Uri.TryCreate(server.Url?.Trim(), UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new InvalidOperationException("Enter a valid http(s) URL.");

                var options = new HttpClientTransportOptions
                {
                    Endpoint = uri,
                    Name = server.Name
                };

                var headers = ParseLines(server.HeadersText, ':');
                if (headers.Count > 0)
                    options.AdditionalHeaders = headers;

                return new HttpClientTransport(options);
            }

            if (string.IsNullOrWhiteSpace(server.Command))
                throw new InvalidOperationException("Enter a command to start the server.");

            var stdio = new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command.Trim(),
                Arguments = SplitArguments(server.Arguments)
            };

            var environment = ParseLines(server.EnvironmentText, '=');
            if (environment.Count > 0)
                stdio.EnvironmentVariables = environment.ToDictionary(p => p.Key, p => (string?)p.Value);

            return new StdioClientTransport(stdio);
        }

        private static Dictionary<string, string> ParseLines(string? text, char separator)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int index = line.IndexOf(separator);
                if (index <= 0)
                    continue;

                string key = line[..index].Trim();
                string value = ExpandEnvironmentReferences(line[(index + 1)..].Trim());
                if (key.Length > 0)
                    result[key] = value;
            }

            return result;
        }

        // Replaces ${NAME} with the value of that environment variable.
        private static string ExpandEnvironmentReferences(string value) =>
            Regex.Replace(value, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}",
                m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? string.Empty);

        private static List<string> SplitArguments(string? arguments)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            char quote = '\0';
            bool hasToken = false;

            foreach (char c in arguments ?? string.Empty)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else current.Append(c);
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                    hasToken = true;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (hasToken || current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }

            if (hasToken || current.Length > 0)
                result.Add(current.ToString());

            return result;
        }

        // Keeps the user's choices for known tools. New tools start disabled; read-only ones don't ask each time.
        public static void MergeTools(McpServerConfig server, IReadOnlyList<McpToolInfo> listed)
        {
            foreach (var setting in server.Tools)
                setting.Available = listed.Any(t => t.Name == setting.Name);

            foreach (var info in listed)
            {
                var setting = server.Tools.FirstOrDefault(t => t.Name == info.Name);
                if (setting == null)
                {
                    server.Tools.Add(new McpToolSetting
                    {
                        Name = info.Name,
                        Description = info.Description,
                        ReadOnlyHint = info.ReadOnlyHint,
                        Enabled = false,
                        RequireApproval = info.ReadOnlyHint != true,
                        Available = true
                    });
                }
                else
                {
                    setting.Description = info.Description;
                    setting.ReadOnlyHint = info.ReadOnlyHint;
                }
            }
        }
    }
}

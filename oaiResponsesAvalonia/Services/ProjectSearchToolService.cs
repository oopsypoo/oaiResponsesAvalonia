using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace oaiResponsesAvalonia.Services
{
    public sealed class ProjectSearchToolService
    {
        private readonly DeveloperToolsOptions _options;
        private readonly WorkspaceGuard _guard;

        public ProjectSearchToolService(DeveloperToolsOptions options, WorkspaceGuard guard)
        {
            _options = options;
            _guard = guard;
        }

        public string SearchProjectText(string query, string glob, string subpath, bool? caseSensitive, int? maxResults)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                    throw new InvalidOperationException("Query is required.");

                var root = _guard.ResolveRelativeDirectory(subpath);
                var pattern = string.IsNullOrWhiteSpace(glob) ? "*.*" : glob;
                var limit = Math.Clamp(maxResults ?? 50, 1, _options.MaxSearchResults);
                var comparison = (caseSensitive ?? false)
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;

                var matches = new List<object>();

                foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                {
                    if (!_options.AllowedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        continue;

                    var info = new FileInfo(file);
                    if (info.Length > _options.MaxFileBytes)
                        continue;

                    string[] lines;
                    try
                    {
                        lines = File.ReadAllLines(file);
                    }
                    catch
                    {
                        continue;
                    }

                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].IndexOf(query, comparison) >= 0)
                        {
                            matches.Add(new
                            {
                                path = _guard.ToRelativePath(file),
                                line = i + 1,
                                snippet = lines[i].Trim()
                            });

                            if (matches.Count >= limit)
                            {
                                return JsonSerializer.Serialize(new
                                {
                                    ok = true,
                                    matches,
                                    truncated = true
                                }, JsonOptions);
                            }
                        }
                    }
                }

                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    matches,
                    truncated = false
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = ex.Message
                }, JsonOptions);
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }
}
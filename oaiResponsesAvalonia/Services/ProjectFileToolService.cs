using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace oaiResponsesAvalonia.Services
{
    public sealed class ProjectFileToolService
    {
        private readonly DeveloperToolsOptions _options;
        private readonly WorkspaceGuard _guard;

        public ProjectFileToolService(DeveloperToolsOptions options, WorkspaceGuard guard)
        {
            _options = options;
            _guard = guard;
        }

        public string ReadProjectFile(string path, int? startLine, int? endLine)
        {
            try
            {
                var fullPath = _guard.ResolveRelativeFile(path);

                var info = new FileInfo(fullPath);
                if (!info.Exists)
                    throw new FileNotFoundException("File not found.", path);

                if (info.Length > _options.MaxFileBytes)
                    throw new InvalidOperationException($"File exceeds maximum allowed size of {_options.MaxFileBytes} bytes.");

                var lines = File.ReadAllLines(fullPath);

                int start = Math.Max(1, startLine ?? 1);
                int end = Math.Min(lines.Length, endLine ?? Math.Min(lines.Length, start + _options.MaxReadLines - 1));

                if (end < start)
                    throw new InvalidOperationException("end_line must be greater than or equal to start_line.");

                if ((end - start + 1) > _options.MaxReadLines)
                    end = start + _options.MaxReadLines - 1;

                var selected = new List<string>();
                for (int i = start; i <= end; i++)
                {
                    selected.Add($"{i}: {lines[i - 1]}");
                }

                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    path,
                    start_line = start,
                    end_line = end,
                    content = string.Join(Environment.NewLine, selected)
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                return Failure(ex);
            }
        }

        public string ListProjectFiles(string subpath, string glob, int? maxResults)
        {
            try
            {
                var root = _guard.ResolveRelativeDirectory(subpath);
                var pattern = string.IsNullOrWhiteSpace(glob) ? "*.*" : glob;
                var limit = Math.Clamp(maxResults ?? 100, 1, 500);

                var files = Directory
                    .EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                    .Where(f => _options.AllowedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Select(_guard.ToRelativePath)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Take(limit)
                    .ToList();

                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    files
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                return Failure(ex);
            }
        }

        public string WriteProjectFile(string path, string content, bool? createIfMissing, bool? overwriteExisting)
        {
            try
            {
                EnsureWriteAllowed();

                if (!_options.WriteProjectFileEnabled)
                    throw new InvalidOperationException("write_project_file is not enabled.");

                if (content == null)
                    throw new InvalidOperationException("Content is required.");

                var fullPath = _guard.ResolveRelativeFile(path);
                var fileExists = File.Exists(fullPath);

                if (fileExists && overwriteExisting != true)
                    throw new InvalidOperationException("File already exists and overwrite_existing was not true.");

                if (!fileExists && createIfMissing != true)
                    throw new InvalidOperationException("File does not exist and create_if_missing was not true.");

                var bytes = Encoding.UTF8.GetByteCount(content);
                if (bytes > _options.MaxWriteFileBytes)
                    throw new InvalidOperationException($"Content exceeds maximum allowed size of {_options.MaxWriteFileBytes} bytes.");

                CreateParentDirectoryIfNeeded(fullPath);
                File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    path,
                    operation = "write_project_file",
                    created = !fileExists,
                    overwritten = fileExists
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                return Failure(ex);
            }
        }

        public string ReplaceInProjectFile(string path, string find, string replace, bool? replaceAll, int? expectedMatchCount)
        {
            try
            {
                EnsureWriteAllowed();

                if (!_options.ReplaceInProjectFileEnabled)
                    throw new InvalidOperationException("replace_in_project_file is not enabled.");

                if (string.IsNullOrEmpty(find))
                    throw new InvalidOperationException("find must not be empty.");

                var fullPath = _guard.ResolveRelativeFile(path);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException("File not found.", path);

                var existing = File.ReadAllText(fullPath);
                int matchCount = CountOccurrences(existing, find);

                if (matchCount == 0)
                    throw new InvalidOperationException("No matching text was found.");

                if (expectedMatchCount.HasValue && expectedMatchCount.Value != matchCount)
                    throw new InvalidOperationException($"Expected {expectedMatchCount.Value} matches, but found {matchCount}.");

                string updated;
                int replacedCount;

                if (replaceAll == true)
                {
                    updated = existing.Replace(find, replace ?? string.Empty);
                    replacedCount = matchCount;
                }
                else
                {
                    int firstIndex = existing.IndexOf(find, StringComparison.Ordinal);
                    updated = existing.Remove(firstIndex, find.Length).Insert(firstIndex, replace ?? string.Empty);
                    replacedCount = 1;
                }

                var bytes = Encoding.UTF8.GetByteCount(updated);
                if (bytes > _options.MaxWriteFileBytes)
                    throw new InvalidOperationException($"Updated file exceeds maximum allowed size of {_options.MaxWriteFileBytes} bytes.");

                File.WriteAllText(fullPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    path,
                    operation = "replace_in_project_file",
                    matches_found = matchCount,
                    matches_replaced = replacedCount
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                return Failure(ex);
            }
        }

        private void EnsureWriteAllowed()
        {
            if (_options.ReadOnlyOnly)
                throw new InvalidOperationException("Write tools are disabled because read-only mode is enabled.");
        }

        private static void CreateParentDirectoryIfNeeded(string fullPath)
        {
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);
        }

        private static int CountOccurrences(string input, string value)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(value))
                return 0;

            int count = 0;
            int index = 0;

            while ((index = input.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private static string Failure(Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.Message
            }, JsonOptions);
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }
}
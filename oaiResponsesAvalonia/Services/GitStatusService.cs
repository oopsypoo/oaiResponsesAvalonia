using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services
{
    /// <summary>
    /// Retrieves a read-only snapshot of Git status using the Git command-line client.
    /// </summary>
    public sealed class GitStatusService
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);

        public async Task<string> GetStatusAsync(string? workspaceRoot)
        {
            string root = (workspaceRoot ?? string.Empty).Trim();
            if (root.Length == 0)
                return "No workspace root is set.";
            if (!Directory.Exists(root))
                return "The configured workspace path does not exist or is not accessible.";

            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(Path.GetFullPath(root));
            startInfo.ArgumentList.Add("--no-optional-locks");
            startInfo.ArgumentList.Add("status");
            startInfo.ArgumentList.Add("--porcelain=v2");
            startInfo.ArgumentList.Add("--branch");

            try
            {
                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                    return "Git could not be started.";

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(CommandTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return "Git status timed out.";
                }

                string output = await outputTask.ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    if (error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
                        return "This workspace is not inside a Git repository.";

                    return "Git status could not be read.\n" + (string.IsNullOrWhiteSpace(error) ? "Git returned an error." : error.Trim());
                }

                return FormatStatus(output);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "Git was not found on PATH. Install Git to view repository status.";
            }
            catch (FileNotFoundException)
            {
                return "Git was not found on PATH. Install Git to view repository status.";
            }
            catch (Exception ex)
            {
                return "Git status could not be read.\n" + ex.Message;
            }
        }

        private static string FormatStatus(string output)
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            var changes = new List<(string State, string Path)>();
            int staged = 0;
            int unstaged = 0;
            int untracked = 0;
            int conflicted = 0;

            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    int separator = line.IndexOf(' ', 2);
                    if (separator > 0)
                        headers[line.Substring(2, separator - 2)] = line.Substring(separator + 1);
                    continue;
                }

                if (line.StartsWith("? ", StringComparison.Ordinal))
                {
                    untracked++;
                    changes.Add(("??", line.Substring(2)));
                    continue;
                }

                if (line.StartsWith("u ", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(' ', 11, StringSplitOptions.RemoveEmptyEntries);
                    string state = parts.Length > 1 ? parts[1] : "UU";
                    string path = parts.Length > 10 ? parts[10] : line;
                    conflicted++;
                    changes.Add((state, path));
                    continue;
                }

                if (line.StartsWith("1 ", StringComparison.Ordinal) || line.StartsWith("2 ", StringComparison.Ordinal))
                {
                    int maximumParts = line.StartsWith("2 ", StringComparison.Ordinal) ? 10 : 9;
                    string[] parts = line.Split(' ', maximumParts, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                        continue;

                    string state = parts[1];
                    if (state.Length >= 2)
                    {
                        if (state[0] != '.') staged++;
                        if (state[1] != '.') unstaged++;
                    }
                    int pathIndex = line.StartsWith("2 ", StringComparison.Ordinal) ? 9 : 8;
                    string path = parts.Length > pathIndex ? parts[pathIndex] : line;
                    changes.Add((state, path));
                }
            }

            var report = new StringBuilder();
            report.AppendLine("Git repository status");
            report.AppendLine("---------------------");
            report.AppendLine("Branch: " + (headers.TryGetValue("branch.head", out string? branch)
                ? branch == "(detached)" ? "detached HEAD" : branch
                : "unknown"));

            if (!headers.ContainsKey("branch.upstream"))
            {
                report.AppendLine("Upstream: none configured");
                report.AppendLine("Ahead/behind: unavailable (no upstream)");
            }
            else
            {
                report.AppendLine("Upstream: " + headers["branch.upstream"]);
                if (headers.TryGetValue("branch.ab", out string? aheadBehind))
                {
                    string[] counts = aheadBehind.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string ahead = counts.FirstOrDefault(value => value.StartsWith("+", StringComparison.Ordinal))?.TrimStart('+') ?? "0";
                    string behind = counts.FirstOrDefault(value => value.StartsWith("-", StringComparison.Ordinal))?.TrimStart('-') ?? "0";
                    report.AppendLine($"Ahead/behind: {ahead} ahead, {behind} behind (since last fetch)");
                }
                else
                    report.AppendLine("Ahead/behind: unavailable");
            }

            if (headers.TryGetValue("branch.oid", out string? oid))
                report.AppendLine("Commit: " + oid);

            report.AppendLine();
            report.AppendLine(changes.Count == 0
                ? "Working tree: clean"
                : $"Working tree: {changes.Count} changed item(s) ({staged} staged, {unstaged} unstaged, {untracked} untracked, {conflicted} conflicted)");

            if (changes.Count > 0)
            {
                foreach ((string state, string path) in changes)
                    report.AppendLine($"  {state}  {path}");
            }
            report.AppendLine();
            report.Append("Status is read-only. Ahead/behind counts reflect the last fetch.");
            return report.ToString();
        }
    }
}

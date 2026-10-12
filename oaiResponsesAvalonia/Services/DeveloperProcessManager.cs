using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services
{
    public sealed class DeveloperProcessManager
    {
        private const int MaxOutputCharacters = 65536;
        private static readonly ConcurrentDictionary<string, RunningProcess> Processes = new(StringComparer.OrdinalIgnoreCase);

        static DeveloperProcessManager()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => new DeveloperProcessManager().StopAllRunningProcesses();
        }

        public async Task<string> StartDotNetRunAsync(string projectFullPath, string projectPath, string configuration)
        {
            try
            {
                var startInfo = DotNetCliService.CreateStartInfo(
                    Path.GetDirectoryName(projectFullPath) ?? string.Empty,
                    "run",
                    "--project",
                    projectFullPath,
                    "--configuration",
                    string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration,
                    "--no-build");

                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                if (!process.Start())
                    throw new InvalidOperationException("Unable to start dotnet run.");

                var record = new RunningProcess(Guid.NewGuid().ToString("N"), process, projectPath);
                if (!Processes.TryAdd(record.Id, record))
                {
                    process.Kill(entireProcessTree: true);
                    throw new InvalidOperationException("Unable to track the launched process.");
                }

                process.Exited += (_, _) =>
                {
                    lock (record.SyncRoot)
                    {
                        record.IsRunning = false;
                        record.ExitCode = process.ExitCode;
                        record.ExitedAtUtc = DateTime.UtcNow;
                    }
                };

                if (process.HasExited)
                {
                    lock (record.SyncRoot)
                    {
                        record.IsRunning = false;
                        record.ExitCode = process.ExitCode;
                        record.ExitedAtUtc = DateTime.UtcNow;
                    }
                }

                _ = ReadOutputAsync(process.StandardOutput, record, isError: false);
                _ = ReadOutputAsync(process.StandardError, record, isError: true);

                return Serialize(new
                {
                    ok = true,
                    process_id = record.Id,
                    os_process_id = process.Id,
                    project_path = projectPath,
                    configuration = configuration ?? "Debug",
                    is_running = true,
                    message = "The project was launched with dotnet run --no-build. Use get_running_processes, get_process_output, or stop_running_process to manage it."
                });
            }
            catch (Exception ex)
            {
                return Failure(ex);
            }
        }

        public string GetRunningProcesses()
        {
            var items = new System.Collections.Generic.List<object>();
            foreach (RunningProcess record in Processes.Values)
            {
                lock (record.SyncRoot)
                {
                    items.Add(new
                    {
                        process_id = record.Id,
                        os_process_id = record.ProcessId,
                        project_path = record.ProjectPath,
                        is_running = record.IsRunning,
                        exit_code = record.ExitCode,
                        started_at_utc = record.StartedAtUtc,
                        exited_at_utc = record.ExitedAtUtc
                    });
                }
            }

            return Serialize(new { ok = true, processes = items });
        }

        public string GetProcessOutput(string? processId)
        {
            if (!Processes.TryGetValue(processId ?? string.Empty, out RunningProcess? record))
                return Serialize(new { ok = false, error = "Unknown developer-launched process." });

            lock (record.SyncRoot)
            {
                return Serialize(new
                {
                    ok = true,
                    process_id = record.Id,
                    is_running = record.IsRunning,
                    exit_code = record.ExitCode,
                    output_truncated = record.OutputTruncated,
                    standard_output = record.StandardOutput.ToString(),
                    standard_error = record.StandardError.ToString()
                });
            }
        }

        public string StopRunningProcess(string? processId)
        {
            if (!Processes.TryGetValue(processId ?? string.Empty, out RunningProcess? record))
                return Serialize(new { ok = false, error = "Unknown developer-launched process." });

            try
            {
                lock (record.SyncRoot)
                {
                    if (record.IsRunning && !record.Process.HasExited)
                        record.Process.Kill(entireProcessTree: true);
                }

                return Serialize(new { ok = true, process_id = record.Id, operation = "stop_running_process" });
            }
            catch (Exception ex)
            {
                return Failure(ex);
            }
        }

        public int StopAllRunningProcesses()
        {
            int stopped = 0;

            foreach (RunningProcess record in Processes.Values)
            {
                try
                {
                    lock (record.SyncRoot)
                    {
                        if (record.IsRunning && !record.Process.HasExited)
                        {
                            record.Process.Kill(entireProcessTree: true);
                            stopped++;
                        }
                    }
                }
                catch
                {
                    // Continue attempting to stop the remaining Developer Tools-launched processes.
                }
            }

            return stopped;
        }

        private static async Task ReadOutputAsync(StreamReader reader, RunningProcess record, bool isError)
        {
            var buffer = new char[4096];
            int read;

            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                lock (record.SyncRoot)
                {
                    var target = isError ? record.StandardError : record.StandardOutput;
                    int remaining = MaxOutputCharacters - target.Length;
                    if (remaining > 0)
                        target.Append(buffer, 0, Math.Min(remaining, read));

                    if (read > remaining)
                        record.OutputTruncated = true;
                }
            }
        }

        private static string Serialize(object value) => System.Text.Json.JsonSerializer.Serialize(value, JsonOptions);

        private static string Failure(Exception ex) => Serialize(new { ok = false, error = ex.Message });

        private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        };

        private sealed class RunningProcess
        {
            public RunningProcess(string id, Process process, string projectPath)
            {
                Id = id;
                Process = process;
                ProcessId = process.Id;
                ProjectPath = projectPath;
            }

            public string Id { get; }
            public Process Process { get; }
            public int ProcessId { get; }
            public string ProjectPath { get; }
            public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
            public DateTime? ExitedAtUtc { get; set; }
            public bool IsRunning { get; set; } = true;
            public int? ExitCode { get; set; }
            public bool OutputTruncated { get; set; }
            public StringBuilder StandardOutput { get; } = new StringBuilder();
            public StringBuilder StandardError { get; } = new StringBuilder();
            public object SyncRoot { get; } = new object();
        }
    }
}

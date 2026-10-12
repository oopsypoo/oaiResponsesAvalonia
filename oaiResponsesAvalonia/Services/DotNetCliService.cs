using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services
{
    public sealed class DotNetCliService
    {
        private const int MaxOutputCharacters = 65536;

        public async Task<ProcessExecutionResult> ExecuteAsync(
            string workingDirectory,
            TimeSpan timeout,
            params string[] arguments)
        {
            var result = new ProcessExecutionResult();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var startInfo = CreateStartInfo(workingDirectory, arguments);
                using var process = new Process { StartInfo = startInfo };

                if (!process.Start())
                {
                    result.Error = "Unable to start dotnet.";
                    return result;
                }

                result.Started = true;
                Task<LimitedTextResult> stdoutTask = ReadLimitedAsync(process.StandardOutput);
                Task<LimitedTextResult> stderrTask = ReadLimitedAsync(process.StandardError);
                Task exitTask = process.WaitForExitAsync();
                Task completedTask = await Task.WhenAny(exitTask, Task.Delay(timeout));

                if (completedTask != exitTask)
                {
                    result.TimedOut = true;
                    TryKill(process);
                    await exitTask;
                }

                LimitedTextResult stdout = await stdoutTask;
                LimitedTextResult stderr = await stderrTask;
                result.StandardOutput = stdout.Text;
                result.StandardError = stderr.Text;
                result.OutputTruncated = stdout.Truncated || stderr.Truncated;
                result.ExitCode = process.ExitCode;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                stopwatch.Stop();
                result.DurationMs = stopwatch.ElapsedMilliseconds;
            }

            return result;
        }

        public static ProcessStartInfo CreateStartInfo(string workingDirectory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string argument in arguments ?? Array.Empty<string>())
                startInfo.ArgumentList.Add(argument ?? string.Empty);

            return startInfo;
        }

        private static async Task<LimitedTextResult> ReadLimitedAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            var builder = new StringBuilder();
            bool truncated = false;
            int read;

            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                int remaining = MaxOutputCharacters - builder.Length;
                if (remaining > 0)
                    builder.Append(buffer, 0, Math.Min(remaining, read));

                if (read > remaining)
                    truncated = true;
            }

            return new LimitedTextResult(builder.ToString(), truncated);
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort: the process may have exited between the checks.
            }
        }

        private sealed class LimitedTextResult
        {
            public LimitedTextResult(string text, bool truncated)
            {
                Text = text;
                Truncated = truncated;
            }

            public string Text { get; }
            public bool Truncated { get; }
        }
    }
}

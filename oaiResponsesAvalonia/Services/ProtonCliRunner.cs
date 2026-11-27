using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services;

public sealed class ProtonCliRunner
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    public async Task<ProtonCommandResult> RunAuthCommandAsync(string executable, string action)
    {
        if (action is not ("login" or "logout"))
            throw new ArgumentOutOfRangeException(nameof(action));

        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(executable) ? "proton-drive" : executable.Trim(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add(action);

        using var process = new Process { StartInfo = startInfo };
        var command = $"{startInfo.FileName} auth {action}";
        var output = new StringBuilder();

        try
        {
            if (!process.Start())
                return new ProtonCommandResult(command, null, false, output.AppendLine("Failed to start process.").ToString());

            // The CLI must not wait for an interactive strategy or input prompt.
            process.StandardInput.Close();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task exitTask = process.WaitForExitAsync();
            Task completed = await Task.WhenAny(exitTask, Task.Delay(CommandTimeout));

            if (completed != exitTask)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                output.AppendLine("Command timed out after 10 minutes; the process was terminated.");
                AppendStreams(output, await stdoutTask, await stderrTask);
                return new ProtonCommandResult(command, process.ExitCode, true, output.ToString());
            }

            await exitTask;
            AppendStreams(output, await stdoutTask, await stderrTask);
            output.AppendLine($"Exit code: {process.ExitCode}");
            return new ProtonCommandResult(command, process.ExitCode, false, output.ToString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            output.AppendLine($"Error: {ex.Message}");
            return new ProtonCommandResult(command, null, false, output.ToString());
        }
    }

    private static void AppendStreams(StringBuilder output, string stdout, string stderr)
    {
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            output.AppendLine("[stdout]");
            output.AppendLine(stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            output.AppendLine("[stderr]");
            output.AppendLine(stderr.TrimEnd());
        }
    }
}

public sealed record ProtonCommandResult(string Command, int? ExitCode, bool TimedOut, string Output, string StandardOutput = "")
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

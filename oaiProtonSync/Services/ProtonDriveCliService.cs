using System.Diagnostics;

namespace oaiProtonSync.Services;

public sealed class ProtonDriveCliService
{
    public async Task<int> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string> outputReceived,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start '{executable}'.");
        }

        var stdoutTask = ForwardLinesAsync(process.StandardOutput, outputReceived, cancellationToken);
        var stderrTask = ForwardLinesAsync(process.StandardError, outputReceived, cancellationToken);

        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdoutTask, stderrTask);
            return process.ExitCode;
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private static async Task ForwardLinesAsync(
        StreamReader reader,
        Action<string> outputReceived,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            outputReceived(line);
        }
    }
}

using System.Diagnostics;

namespace oaiProtonSync.Services;

public sealed class ProtonDriveCliService
{
    public async Task<int> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Action<string> outputReceived,
        CancellationToken cancellationToken = default,
        Action<string>? standardOutputReceived = null)
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

        var stdoutTask = ForwardLinesAsync(
            process.StandardOutput,
            line =>
            {
                outputReceived(line);
                standardOutputReceived?.Invoke(line);
            },
            cancellationToken);
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
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the HasExited check and Kill.
                }
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

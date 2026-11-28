using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services;

public sealed class ProtonCliRunner
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    public async Task<ProtonCommandResult> RunAuthCommandAsync(
        string executable,
        string action,
        Action<string>? outputReceived = null)
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
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputLock = new object();
        bool stdoutHeaderWritten = false;
        bool stderrHeaderWritten = false;
        var openedAuthenticationUrls = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        void TryOpenAuthenticationUrl(string text)
        {
            // Proton Drive opens the browser itself on Linux. On Windows, open the
            // URL emitted by the login command through the registered default browser.
            if (action != "login" || !OperatingSystem.IsWindows())
                return;

            string? url = ExtractFirstHttpUrl(text);
            if (url is null)
                return;

            lock (openedAuthenticationUrls)
            {
                if (!openedAuthenticationUrls.Add(url))
                    return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                outputReceived?.Invoke($"{Environment.NewLine}[browser] Opening authentication link in your default browser.{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                outputReceived?.Invoke($"{Environment.NewLine}[browser] Could not open the authentication link automatically: {ex.Message}{Environment.NewLine}");
            }
        }

        try
        {
            if (!process.Start())
                return new ProtonCommandResult(command, null, false, "Failed to start process.");

            // The CLI must not wait for an interactive strategy or input prompt.
            process.StandardInput.Close();

            Task stdoutTask = PumpStreamAsync(process.StandardOutput, "[stdout]", stdout, outputReceived,
                outputLock, () => stdoutHeaderWritten, value => stdoutHeaderWritten = value, TryOpenAuthenticationUrl);
            Task stderrTask = PumpStreamAsync(process.StandardError, "[stderr]", stderr, outputReceived,
                outputLock, () => stderrHeaderWritten, value => stderrHeaderWritten = value, TryOpenAuthenticationUrl);
            Task exitTask = process.WaitForExitAsync();
            Task completed = await Task.WhenAny(exitTask, Task.Delay(CommandTimeout));

            if (completed != exitTask)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                await Task.WhenAll(stdoutTask, stderrTask);

                const string timeoutMessage = "Command timed out after 10 minutes; the process was terminated.";
                string capturedOutput = FormatCapturedOutput(stdout.ToString(), stderr.ToString());
                return new ProtonCommandResult(command, process.ExitCode, true,
                    $"{timeoutMessage}{Environment.NewLine}{capturedOutput}", stdout.ToString());
            }

            await exitTask;
            await Task.WhenAll(stdoutTask, stderrTask);
            string formattedOutput = FormatCapturedOutput(stdout.ToString(), stderr.ToString());
            return new ProtonCommandResult(command, process.ExitCode, false,
                $"{formattedOutput}Exit code: {process.ExitCode}{Environment.NewLine}", stdout.ToString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string message = $"Error: {ex.Message}";
            return new ProtonCommandResult(command, null, false, message, stdout.ToString());
        }
    }

    private static async Task PumpStreamAsync(
        System.IO.StreamReader reader,
        string header,
        StringBuilder capture,
        Action<string>? outputReceived,
        object outputLock,
        Func<bool> headerWritten,
        Action<bool> setHeaderWritten,
        Action<string>? authenticationUrlHandler)
    {
        var buffer = new char[1024];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0)
                return;

            string chunk = new(buffer, 0, count);
            string displayChunk;
            lock (outputLock)
            {
                capture.Append(chunk);
                displayChunk = headerWritten()
                    ? chunk
                    : $"{header}{Environment.NewLine}{chunk}";
                setHeaderWritten(true);
            }

            outputReceived?.Invoke(displayChunk);
            authenticationUrlHandler?.Invoke(capture.ToString());
        }
    }

    internal static string? ExtractFirstHttpUrl(string text)
    {
        Match match = Regex.Match(text, @"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;

        string candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
        return Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(uri.Host)
                ? candidate
                : null;
    }

    private static string FormatCapturedOutput(string stdout, string stderr)
    {
        var output = new StringBuilder();
        if (!string.IsNullOrEmpty(stdout))
            output.AppendLine("[stdout]").Append(stdout);
        if (!string.IsNullOrEmpty(stderr))
            output.AppendLine("[stderr]").Append(stderr);
        return output.ToString();
    }
}

public sealed record ProtonCommandResult(string Command, int? ExitCode, bool TimedOut, string Output, string StandardOutput = "")
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using oaiResponsesAvalonia.Services;

namespace oaiResponsesAvalonia.Services
{
    public sealed class DotNetProjectToolService
    {
        private static readonly HashSet<string> AllowedTemplates = new(StringComparer.OrdinalIgnoreCase)
        {
            "console",
            "classlib",
            "wpf",
            "winforms",
            "webapi",
            "mvc",
            "xunit"
        };

        private static readonly Regex SafeNamePattern = new(
            "^[A-Za-z_][A-Za-z0-9_.-]*$",
            RegexOptions.CultureInvariant);

        private readonly DeveloperToolsOptions _options;
        private readonly WorkspaceGuard _guard;
        private readonly DotNetCliService _dotNetCli;
        private readonly DeveloperProcessManager _processManager;

        public DotNetProjectToolService(
            DeveloperToolsOptions options,
            WorkspaceGuard guard,
            DotNetCliService dotNetCli,
            DeveloperProcessManager processManager)
        {
            _options = options;
            _guard = guard;
            _dotNetCli = dotNetCli;
            _processManager = processManager;
        }

        public async Task<string> CreateDotNetSolutionAsync(
            string solutionName,
            string projectName,
            string? template,
            string framework,
            string outputDirectory)
        {
            try
            {
                EnsureExecutionAllowed(_options.CreateDotNetSolutionEnabled, "create_dotnet_solution");
                ValidateName(solutionName, "solution_name");
                ValidateName(projectName, "project_name");

                string safeTemplate = template ?? string.Empty;
                if (!AllowedTemplates.Contains(safeTemplate))
                    throw new InvalidOperationException("The requested template is not allowed.");

                ValidateChildDirectory(outputDirectory);
                string outputFullPath = Path.Combine(_guard.RootFullPath, outputDirectory);
                if (Directory.Exists(outputFullPath) || File.Exists(outputFullPath))
                    throw new InvalidOperationException("The requested output_directory already exists.");

                Directory.CreateDirectory(outputFullPath);

                var commandResults = new List<object>();
                ProcessExecutionResult solutionResult = await _dotNetCli.ExecuteAsync(
                    outputFullPath,
                    TimeSpan.FromSeconds(_options.CommandTimeoutSeconds),
                    "new", "sln", "--name", solutionName, "--output", outputFullPath);
                commandResults.Add(ToCommandResult("dotnet new sln", solutionResult));
                if (!solutionResult.Ok)
                    return Serialize(new
                    {
                        ok = false,
                        operation = "create_dotnet_solution",
                        created_directory = outputDirectory,
                        error = "The solution creation command failed.",
                        commands = commandResults
                    });

                string projectRelativePath = Path.Combine(outputDirectory, "src", projectName);
                string projectFullPath = Path.Combine(_guard.RootFullPath, projectRelativePath);
                var newProjectArguments = new List<string>
                {
                    "new", safeTemplate, "--name", projectName, "--output", projectFullPath
                };

                if (!string.IsNullOrWhiteSpace(framework))
                {
                    ValidateFramework(framework);
                    newProjectArguments.Add("--framework");
                    newProjectArguments.Add(framework);
                }

                ProcessExecutionResult projectResult = await _dotNetCli.ExecuteAsync(
                    outputFullPath,
                    TimeSpan.FromSeconds(_options.CommandTimeoutSeconds),
                    newProjectArguments.ToArray());
                commandResults.Add(ToCommandResult("dotnet new " + template, projectResult));
                if (!projectResult.Ok)
                    return Serialize(new
                    {
                        ok = false,
                        operation = "create_dotnet_solution",
                        created_directory = outputDirectory,
                        error = "The project creation command failed.",
                        commands = commandResults
                    });

                string? solutionFullPath = Directory
                    .EnumerateFiles(outputFullPath, "*.sln*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                string? projectFileFullPath = Directory
                    .EnumerateFiles(projectFullPath, "*.csproj", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();

                if (string.IsNullOrWhiteSpace(solutionFullPath) || string.IsNullOrWhiteSpace(projectFileFullPath))
                    throw new InvalidOperationException("The generated solution or project file could not be located.");

                ProcessExecutionResult addProjectResult = await _dotNetCli.ExecuteAsync(
                    outputFullPath,
                    TimeSpan.FromSeconds(_options.CommandTimeoutSeconds),
                    "sln", solutionFullPath, "add", projectFileFullPath);
                commandResults.Add(ToCommandResult("dotnet sln add", addProjectResult));

                if (!addProjectResult.Ok)
                {
                    return Serialize(new
                    {
                        ok = false,
                        operation = "create_dotnet_solution",
                        created_directory = outputDirectory,
                        commands = commandResults,
                        error = "The project was created but could not be added to the solution."
                    });
                }

                return Serialize(new
                {
                    ok = true,
                    operation = "create_dotnet_solution",
                    created_directory = outputDirectory,
                    workspace_root = outputFullPath,
                    solution_path = Path.GetRelativePath(outputFullPath, solutionFullPath),
                    project_path = Path.GetRelativePath(outputFullPath, projectFileFullPath),
                    commands = commandResults
                });
            }
            catch (Exception ex)
            {
                return Failure(ex);
            }
        }

        public async Task<string> BuildDotNetProjectAsync(string targetPath, string configuration)
        {
            try
            {
                EnsureExecutionAllowed(_options.BuildDotNetProjectEnabled, "build_dotnet_project");
                string targetFullPath = ResolveBuildTarget(targetPath);
                string normalizedConfiguration = NormalizeConfiguration(configuration);

                ProcessExecutionResult result = await _dotNetCli.ExecuteAsync(
                    Path.GetDirectoryName(targetFullPath) ?? _guard.RootFullPath,
                    TimeSpan.FromSeconds(_options.BuildTimeoutSeconds),
                    "build", targetFullPath, "--configuration", normalizedConfiguration, "--verbosity", "minimal");

                return Serialize(new
                {
                    ok = result.Ok,
                    operation = "build_dotnet_project",
                    target_path = _guard.ToRelativePath(targetFullPath),
                    configuration = normalizedConfiguration,
                    exit_code = result.ExitCode,
                    timed_out = result.TimedOut,
                    duration_ms = result.DurationMs,
                    output_truncated = result.OutputTruncated,
                    standard_output = result.StandardOutput,
                    standard_error = result.StandardError,
                    error = result.Error
                });
            }
            catch (Exception ex)
            {
                return Failure(ex);
            }
        }

        public async Task<string> CleanDotNetProjectAsync(string targetPath, string configuration)
        {
            try
            {
                EnsureExecutionAllowed(_options.BuildDotNetProjectEnabled, "clean_dotnet_project");
                string targetFullPath = ResolveBuildTarget(targetPath);
                string normalizedConfiguration = NormalizeConfiguration(configuration);

                ProcessExecutionResult result = await _dotNetCli.ExecuteAsync(
                    Path.GetDirectoryName(targetFullPath) ?? _guard.RootFullPath,
                    TimeSpan.FromSeconds(_options.BuildTimeoutSeconds),
                    "clean", targetFullPath, "--configuration", normalizedConfiguration, "--verbosity", "minimal");

                return Serialize(new
                {
                    ok = result.Ok,
                    operation = "clean_dotnet_project",
                    target_path = _guard.ToRelativePath(targetFullPath),
                    configuration = normalizedConfiguration,
                    exit_code = result.ExitCode,
                    timed_out = result.TimedOut,
                    duration_ms = result.DurationMs,
                    output_truncated = result.OutputTruncated,
                    standard_output = result.StandardOutput,
                    standard_error = result.StandardError,
                    error = result.Error
                });
            }
            catch (Exception ex)
            {
                return Failure(ex);
            }
        }

        public async Task<string> RebuildDotNetProjectAsync(string targetPath, string configuration)
        {
            try
            {
                EnsureExecutionAllowed(_options.BuildDotNetProjectEnabled, "rebuild_dotnet_project");
                string targetFullPath = ResolveBuildTarget(targetPath);
                string normalizedConfiguration = NormalizeConfiguration(configuration);
                string workingDirectory = Path.GetDirectoryName(targetFullPath) ?? _guard.RootFullPath;
                var commandResults = new List<object>();

                ProcessExecutionResult cleanResult = await _dotNetCli.ExecuteAsync(
                    workingDirectory,
                    TimeSpan.FromSeconds(_options.BuildTimeoutSeconds),
                    "clean", targetFullPath, "--configuration", normalizedConfiguration, "--verbosity", "minimal");
                commandResults.Add(ToCommandResult("dotnet clean", cleanResult));

                if (!cleanResult.Ok)
                {
                    return Serialize(new
                    {
                        ok = false,
                        operation = "rebuild_dotnet_project",
                        target_path = _guard.ToRelativePath(targetFullPath),
                        configuration = normalizedConfiguration,
                        commands = commandResults,
                        error = "The clean phase failed, so the build phase was not run."
                    });
                }

                ProcessExecutionResult buildResult = await _dotNetCli.ExecuteAsync(
                    workingDirectory,
                    TimeSpan.FromSeconds(_options.BuildTimeoutSeconds),
                    "build", targetFullPath, "--configuration", normalizedConfiguration, "--verbosity", "minimal");
                commandResults.Add(ToCommandResult("dotnet build", buildResult));

                return Serialize(new
                {
                    ok = buildResult.Ok,
                    operation = "rebuild_dotnet_project",
                    target_path = _guard.ToRelativePath(targetFullPath),
                    configuration = normalizedConfiguration,
                    commands = commandResults,
                    error = buildResult.Ok ? null : "The build phase failed after a successful clean."
                });
            }
            catch (Exception ex)
            {
                return Failure(ex);
            }
        }

        public Task<string> RunDotNetProjectAsync(string projectPath, string configuration)
        {
            try
            {
                EnsureExecutionAllowed(_options.RunDotNetProjectEnabled, "run_dotnet_project");
                string projectFullPath = _guard.ResolveRelativeFile(projectPath);
                if (!string.Equals(Path.GetExtension(projectFullPath), ".csproj", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("run_dotnet_project requires a .csproj path.");

                return _processManager.StartDotNetRunAsync(
                    projectFullPath,
                    _guard.ToRelativePath(projectFullPath),
                    NormalizeConfiguration(configuration));
            }
            catch (Exception ex)
            {
                return Task.FromResult(Failure(ex));
            }
        }

        public string GetRunningProcesses()
        {
            EnsureExecutionAllowed(_options.RunDotNetProjectEnabled, "get_running_processes");
            return _processManager.GetRunningProcesses();
        }

        public string GetProcessOutput(string processId)
        {
            EnsureExecutionAllowed(_options.RunDotNetProjectEnabled, "get_process_output");
            return _processManager.GetProcessOutput(processId);
        }

        public string StopRunningProcess(string processId)
        {
            EnsureExecutionAllowed(_options.RunDotNetProjectEnabled, "stop_running_process");
            return _processManager.StopRunningProcess(processId);
        }

        private void EnsureExecutionAllowed(bool toolEnabled, string toolName)
        {
            if (_options.ReadOnlyOnly)
                throw new InvalidOperationException("Project operations are disabled because read-only mode is enabled.");

            if (!toolEnabled)
                throw new InvalidOperationException(toolName + " is not enabled.");
        }

        private string ResolveBuildTarget(string targetPath)
        {
            string fullPath = _guard.ResolveRelativeFile(targetPath);
            string extension = Path.GetExtension(fullPath);
            if (!string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("build_dotnet_project requires a .csproj, .sln, or .slnx path.");
            }

            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Build target not found.", targetPath);

            return fullPath;
        }

        private static void ValidateChildDirectory(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory) ||
                Path.IsPathRooted(outputDirectory) ||
                outputDirectory.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                outputDirectory.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                outputDirectory == "." ||
                outputDirectory == ".." ||
                !SafeNamePattern.IsMatch(outputDirectory))
            {
                throw new InvalidOperationException("output_directory must be the name of one new direct child folder under the workspace root.");
            }
        }

        private static void ValidateName(string value, string argumentName)
        {
            if (string.IsNullOrWhiteSpace(value) || !SafeNamePattern.IsMatch(value))
                throw new InvalidOperationException(argumentName + " must contain only a safe project or solution name.");
        }

        private static void ValidateFramework(string framework)
        {
            if (!Regex.IsMatch(framework, "^net[0-9]+(\\.[0-9]+)?(-[A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("framework must be a valid .NET target framework moniker.");
        }

        private static string NormalizeConfiguration(string configuration)
        {
            if (string.IsNullOrWhiteSpace(configuration))
                return "Debug";

            if (string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase))
                return "Debug";
            if (string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase))
                return "Release";

            throw new InvalidOperationException("configuration must be Debug or Release.");
        }

        private static object ToCommandResult(string command, ProcessExecutionResult result)
        {
            return new
            {
                command,
                ok = result.Ok,
                exit_code = result.ExitCode,
                timed_out = result.TimedOut,
                duration_ms = result.DurationMs,
                output_truncated = result.OutputTruncated,
                standard_output = result.StandardOutput,
                standard_error = result.StandardError,
                error = result.Error
            };
        }

        private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

        private static string Failure(Exception ex) => Serialize(new { ok = false, error = ex.Message });

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }
}

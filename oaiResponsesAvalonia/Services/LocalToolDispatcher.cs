using System.Text.Json;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services
{
    public sealed class LocalToolDispatcher
    {
        private readonly ProjectFileToolService _fileService;
        private readonly ProjectSearchToolService _searchService;
        private readonly DotNetProjectToolService _dotNetProjectService;

        public LocalToolDispatcher(
            ProjectFileToolService fileService,
            ProjectSearchToolService searchService,
            DotNetProjectToolService dotNetProjectService)
        {
            _fileService = fileService;
            _searchService = searchService;
            _dotNetProjectService = dotNetProjectService;
        }

        public async Task<string> DispatchAsync(string toolName, string argumentsJson)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;

            switch (toolName)
            {
                case "read_project_file":
                    return _fileService.ReadProjectFile(
                        GetString(root, "path"),
                        GetInt(root, "start_line"),
                        GetInt(root, "end_line"));

                case "search_project_text":
                    return _searchService.SearchProjectText(
                        GetString(root, "query"),
                        GetString(root, "glob"),
                        GetString(root, "subpath"),
                        GetBool(root, "case_sensitive"),
                        GetInt(root, "max_results"));

                case "list_project_files":
                    return _fileService.ListProjectFiles(
                        GetString(root, "subpath"),
                        GetString(root, "glob"),
                        GetInt(root, "max_results"));

                case "write_project_file":
                    return _fileService.WriteProjectFile(
                        GetString(root, "path"),
                        GetString(root, "content"),
                        GetBool(root, "create_if_missing"),
                        GetBool(root, "overwrite_existing"));

                case "replace_in_project_file":
                    return _fileService.ReplaceInProjectFile(
                        GetString(root, "path"),
                        GetString(root, "find"),
                        GetString(root, "replace"),
                        GetBool(root, "replace_all"),
                        GetInt(root, "expected_match_count"));

                case "create_dotnet_solution":
                    return await _dotNetProjectService.CreateDotNetSolutionAsync(
                        GetString(root, "solution_name"),
                        GetString(root, "project_name"),
                        GetString(root, "template"),
                        GetString(root, "framework"),
                        GetString(root, "output_directory"));

                case "build_dotnet_project":
                    return await _dotNetProjectService.BuildDotNetProjectAsync(
                        GetString(root, "target_path"),
                        GetString(root, "configuration"));

                case "clean_dotnet_project":
                    return await _dotNetProjectService.CleanDotNetProjectAsync(
                        GetString(root, "target_path"),
                        GetString(root, "configuration"));

                case "rebuild_dotnet_project":
                    return await _dotNetProjectService.RebuildDotNetProjectAsync(
                        GetString(root, "target_path"),
                        GetString(root, "configuration"));

                case "run_dotnet_project":
                    return await _dotNetProjectService.RunDotNetProjectAsync(
                        GetString(root, "project_path"),
                        GetString(root, "configuration"));

                case "get_running_processes":
                    return _dotNetProjectService.GetRunningProcesses();

                case "get_process_output":
                    return _dotNetProjectService.GetProcessOutput(GetString(root, "process_id"));

                case "stop_running_process":
                    return _dotNetProjectService.StopRunningProcess(GetString(root, "process_id"));

                default:
                    return JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = $"Unknown tool: {toolName}"
                    });
            }
        }

        private static string GetString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString() ?? string.Empty
                : string.Empty;
        }

        private static int? GetInt(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
                ? prop.GetInt32()
                : (int?)null;
        }

        private static bool? GetBool(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var prop) &&
                   (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
                ? prop.GetBoolean()
                : (bool?)null;
        }
    }
}

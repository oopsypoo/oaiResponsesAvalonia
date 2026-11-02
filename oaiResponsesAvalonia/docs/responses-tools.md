# Responses API tools

This document is the project-level inventory of tools added to Responses API requests by `Responses.cs`. It distinguishes OpenAI-hosted tools from application-executed function tools so that “tool” does not imply the same execution model for every entry.

> Update this inventory whenever a tool is added, removed, renamed, or its permissions or behavior change. The request-building and dispatch implementation in `Responses.cs` is the source of truth.

## Tool categories at a glance

| Category | Execution | Tools in this project | Included when |
|---|---|---|---|
| OpenAI-hosted: web | OpenAI performs web search as part of the Responses API request. | `web_search` | The web search option is active in the current tool selection. |
| OpenAI-hosted: image generation | OpenAI performs image generation as part of the Responses API request. | `image_generation` | The image-generation option is active in the current tool selection. |
| Application-local: developer tools | The model requests a function call; this application validates/dispatches it and executes the local operation, then sends the result back to the model. | The 13 functions listed below. | Developer tools are enabled, a repository/workspace root is available, and the individual tool's option allows it. Some write and project-operation tools are also excluded in read-only mode. |
| External MCP tools | Discovered from configured MCP servers and invoked through the MCP session. These are not the application's internal Responses function tools. | Not catalogued here. | MCP servers are configured and expose tools. |

The hosted tools and local functions may be combined in a request. MCP tools are a separate integration category and are intentionally outside this local-function inventory.

## OpenAI-hosted tools

These are tool types in the Responses request, not local C# functions dispatched by `LocalToolDispatcher`.

| Tool | Purpose and configuration | Execution / result |
|---|---|---|
| `web_search` | Web search. The request sets `SearchContextSize` from `WebSearchContextSize`. | OpenAI-hosted; this application does not execute a local web-search function. |
| `image_generation` | Image generation. The request uses the application's image-generation settings: `ImageGenQuality`, `ImageGenSize`, `ImageGenOutputFormat`, `ImageGenOutputCompression`, `ImageGenBackground`, and `ImageGenModel`. Compression is passed only for `jpeg` and `webp` output formats. | OpenAI-hosted; generated image output is handled as part of the Responses result. |

When image generation is the only selected tool, the request selects it explicitly with `ToolChoice`; otherwise the configured tools use automatic choice.

## Application-local developer functions

These are `type: "function"` entries supplied to the model. The model can request one, but the application—not the model—executes it. The function schema rejects additional properties. Tool availability is controlled by `DeveloperToolsOptions` and is not guaranteed merely because a tool appears in this catalog.

### Read-only workspace inspection

| Function | Purpose | Inputs | Availability / effects |
|---|---|---|---|
| `search_project_text` | Search text in allowed files under the configured workspace root; return relative paths, line numbers, and snippets. | Required: `query`. Optional: `glob`, `subpath`, `case_sensitive`, `max_results` (1–200). | Requires `SearchProjectTextEnabled`. Read-only. |
| `read_project_file` | Read a text file under the workspace root and return line-numbered text. | Required: `path`. Optional: `start_line`, `end_line` (each at least 1). | Requires `ReadProjectFileEnabled`. Read-only. |
| `list_project_files` | List allowed files under the workspace root. The result reports `total_files` and `truncated`. | Optional: `subpath`, `glob`, `max_results` (1–500; default 100). | Requires `ListProjectFilesEnabled`. Read-only. If truncated, narrow the listing or raise the limit; omitted paths must not be assumed absent. |

### Workspace file changes

| Function | Purpose | Inputs | Availability / effects |
|---|---|---|---|
| `write_project_file` | Create or write a text file inside the workspace root. | Required: `path`, `content`. Optional: `create_if_missing`, `overwrite_existing`. | Requires developer tools enabled, `WriteProjectFileEnabled`, and not `ReadOnlyOnly`. Writes are limited by the application's allowed file types and workspace guard. Subject to write-confirmation settings. |
| `replace_in_project_file` | Replace exact text in an existing allowed text file inside the workspace root. | Required: `path`, `find`, `replace`. Optional: `replace_all`, `expected_match_count` (at least 0). | Requires developer tools enabled, `ReplaceInProjectFileEnabled`, and not `ReadOnlyOnly`. Subject to write-confirmation settings. |

### .NET project operations

| Function | Purpose | Inputs | Availability / effects |
|---|---|---|---|
| `create_dotnet_solution` | Create a constrained .NET solution and one project in a new direct-child folder under the workspace root. | Required: `solution_name`, `project_name`, `template`, `output_directory`. Optional: `framework`. Supported templates in the schema: `console`, `classlib`, `wpf`, `winforms`, `webapi`, `mvc`, `xunit`. | Requires developer tools enabled, `CreateDotNetSolutionEnabled`, and not `ReadOnlyOnly`. The application constructs the `dotnet` operation; arbitrary shell commands are not accepted. May update the active workspace root to the created folder. Subject to execution-confirmation settings. |
| `build_dotnet_project` | Build an existing .NET project or solution and return bounded build output. | Required: `target_path` (`.csproj`, `.sln`, or `.slnx`). Optional: `configuration` (`Debug` or `Release`; default `Debug`). | Requires developer tools enabled, `BuildDotNetProjectEnabled`, and not `ReadOnlyOnly`. Subject to execution-confirmation settings. |
| `clean_dotnet_project` | Clean build outputs for a project or solution. A solution target cleans all projects in it. | Required: `target_path` (`.csproj`, `.sln`, or `.slnx`). Optional: `configuration` (`Debug` or `Release`; default `Debug`). | Requires developer tools enabled, `BuildDotNetProjectEnabled`, and not `ReadOnlyOnly`. Subject to execution-confirmation settings. |
| `rebuild_dotnet_project` | Clean and then build a project or solution. A solution target rebuilds all projects in it. | Required: `target_path` (`.csproj`, `.sln`, or `.slnx`). Optional: `configuration` (`Debug` or `Release`; default `Debug`). | Requires developer tools enabled, `BuildDotNetProjectEnabled`, and not `ReadOnlyOnly`. Subject to execution-confirmation settings. |
| `run_dotnet_project` | Run an already-built .NET project using `dotnet run --no-build`; the application tracks the process. | Required: `project_path` (`.csproj`). Optional: `configuration` (`Debug` or `Release`; default `Debug`). | Requires developer tools enabled, `RunDotNetProjectEnabled`, and not `ReadOnlyOnly`. Subject to execution-confirmation settings. |

### Processes started by Developer Tools

| Function | Purpose | Inputs | Availability / effects |
|---|---|---|---|
| `get_running_processes` | List processes launched by Developer Tools in this application instance. | None. | Included with the `RunDotNetProjectEnabled` tools. Read-only process information. |
| `get_process_output` | Return bounded standard output and standard error for a Developer Tools-launched process. | Required: `process_id`. | Included with the `RunDotNetProjectEnabled` tools. Read-only process information. |
| `stop_running_process` | Stop a process previously launched by Developer Tools. | Required: `process_id`. | Included with the `RunDotNetProjectEnabled` tools; classified as an execution tool for confirmation purposes. Stops a tracked process. |

## Local function call lifecycle and safeguards

1. `GetToolsForCurrentSelection()` adds the selected hosted tools. `GetLocalFunctionTools(...)` adds local function definitions allowed by `DeveloperToolsOptions`; MCP definitions, when present, are appended separately.
2. The request uses automatic tool choice when tools are available, except that image generation alone is explicitly selected.
3. The Responses API may return a function-call request. `RunToolLoopAsync(...)` classifies local calls as read/default, write, or project/process execution calls. It uses `LocalToolDispatcher`, the file/search/project services, and `WorkspaceGuard` to handle internal developer functions.
4. Confirmation requirements come from the relevant settings: `RequireConfirmation`, `RequireWriteConfirmation`, or `RequireExecutionConfirmation`. The application asks through `confirmLocalCallAsync` when a confirmation is required **and** that callback is supplied. A denied call is returned to the model as a failed tool output; an approved call is executed and its output is sent back in a `function_call_output`.
5. The request loop continues until the model returns a response with no further function calls.

Local file and process operations are intended to be handled deterministically; the request disables parallel tool calls when developer tools are enabled (or MCP tools are present).

> Confirmation is an application-level safeguard, not a property of the Responses API function schema. Verify that the calling UI supplies the confirmation callback whenever confirmation is intended to be enforced.

## Maintenance checklist for new tools

- [ ] Put the tool in the correct category: OpenAI-hosted, application-local function, or MCP.
- [ ] Document the exact API-facing name and the user-visible purpose.
- [ ] Record required/optional arguments, validation limits, availability switches, and read/write/execute effects.
- [ ] Describe approval behavior, workspace boundaries, and any data returned to the model.
- [ ] Update dispatcher/hosted-tool configuration and tests alongside this inventory.

# oaiResponsesAvalonia

## Added terminal diagnostics tool - 2026-10-27 

* Added a terminal window as a diagnostics/developer tool w/linux-like tools to search and traverse database.
* This feature is intended to remain in the Avalonia application and is not just branch-specific experimentation.
* The branch differences between `main` and `terminal_view` are now small enough that they are expected to be merged.

## Detached the avalonia-project from the wpf-project - 2026-10-12**

* New solution and name that mirrors it's project-name (i.e. header in this file)
* New GitHub repository



## First good crossplatform (Linux/Windows) build that works - 2026-10-11**

* Most problematic was NativeWebViewer
* updated in windows but not in Linux when changing tabs
* highlighter.js was not good. Get new source
* works on both platforms
* look and feel is the same



**Renamed project - 2026-10-10**

* created new project-target(avalonia): oaiResponsesAvalonia
* first "good" build
* fixed some bugs
* (GitHub copilot did all of the conversion from wpf->avalonia(used/added avalonioa MCP-server)(gpt-6-luna))
* still fixing bugs though



**Project goal - 2026-10-07**

* Rewrite the application from WPF to Avalonia, using Avalonia-style UI and application structure rather than carrying the WPF design forward unchanged.
* Use Avalonia to make the application run on both Windows and Linux. The goal is cross-platform support, while keeping the core experience focused on the OpenAI Responses API.
* This is the first step toward a leaner application; further cleanup of the Responses experience will follow as the Avalonia conversion progresses.



## 2026-10-02 — Developer Tools for local .NET workspaces

The **Responses** experience includes optional **Developer Tools** that let an OpenAI model work with a local C#/.NET workspace through constrained, auditable tools. The user chooses the workspace and explicitly enables each capability; the model is not given an unrestricted shell or arbitrary filesystem access.

### Workspace workflow

* For an existing repository or solution, select its folder as the **Workspace root**.
* To create a project, initially select the parent folder where the new solution should be created.
* When creation succeeds, the app automatically changes the Workspace root to the newly created solution folder. Later reads, edits, builds, cleans, rebuilds, and runs use that new root.
* Tool-call logs record the workspace root used for each operation, making the creation-parent and created-project transition clear in saved sessions.

### Available capabilities

|Capability|What it does|
|-|-|
|Inspect workspace files|Search project text, read selected file ranges, and list files within the configured workspace root.|
|Change files|Create/write project files or make exact text replacements when the corresponding write capability is enabled.|
|Create .NET projects|Create a constrained solution and one project in a new direct child folder using approved `dotnet new` templates.|
|Build, clean, and rebuild|Run controlled `dotnet build`, `dotnet clean`, or clean-then-build operations for a `.csproj`, `.sln`, or `.slnx` target.|
|Run in Debug|Start an already-built .NET project in the `Debug` configuration and track the launched process.|
|Manage launched processes|List Developer Tools-launched processes, read their bounded output, and stop an individual tracked process.|

### Safety and permissions

Developer Tools remain disabled until enabled by the user. Operations are constrained to the selected Workspace root and supported file/solution types.

* File writes and replacements can require per-operation approval. Users may disable this approval when they want enabled write tools to apply changes automatically.
* Creating projects, building, cleaning, rebuilding, running, and stopping launched processes are separate execution capabilities. They can require their own approval, independently of file-change approval.
* Project creation is limited to a new direct child directory of the initial workspace root and an application-maintained allowlist of .NET templates.
* Build, clean, and rebuild accept only supported `.csproj`, `.sln`, and `.slnx` targets and controlled `Debug` or `Release` configurations.
* Run operations launch only tracked .NET projects through a constrained `dotnet run --no-build` flow; there is no generic shell, PowerShell, batch-file, or arbitrary executable tool.
* Tool-call logging continues even when the log panel is hidden. Logs are saved with the conversation and include the operation, arguments, result, and workspace root.

> \\\*\\\*Note:\\\*\\\* The project creation and build/run capabilities use the .NET CLI to generate and operate on standard solutions/projects. They do not automate the Visual Studio UI, but generated `.sln`/`.slnx` solutions can be opened normally in Visual Studio.

**2026-03-15** Refactored WPF UI and session state management

* Continued the WPF refactor using an MVVM-lite approach: introduced bindable panel-state objects instead of doing a full MVVM rewrite.
* Logs tab now uses `LogsPanelState` for filter/search state, with XAML bindings and centralized filtering.
* Responses tab now uses `ResponsesPanelState` for prompt/response/selected turn plus options state (model, reasoning, tools, search/image settings).
* Responses session restore was improved so logs reload conversation content and latest saved settings consistently.
* Responses history list now shows per-turn metadata (model/reasoning/tools) for better traceability.
* Video tab was reworked toward job-based sessions instead of pseudo-conversation sessions.
* Added `VideoPanelState` and `CurrentVideoMessages` to separate job/session state from the global video library list.
* Video generation/remix now always creates a new session; remix lineage is stored via `SourceRemoteId`.
* Database/history layer was updated to support `SourceRemoteId`, including SQLite schema patching for existing databases.
* General cleanup: reduced direct control manipulation, improved tab/session restore behavior, and kept code-behind focused on orchestration rather than raw UI state.
* 

**2025-11-12 1745** Added Video

* Haven't worked on this project for a while so I made use of OpenAi. Very efficient...
* Videocreation tool by OpenAI. Using the basic calls, ++ reference image. I does the job
* Did not implement the remix-module. Maybe later
* Dalle-E is not really Dall-E anymore since I hardcoded in the model-name (gpt-image-1). There are restrictions, but there are restrictions on every image-model (they are different)
* 

**2024-05-20 1651** Added Vision

* Added OpenAI Vision using Azure OpenAI API
* Reason for this was that I had problems-/or it was confusing using JSON-format/structure. Couldn't make it work, so I shifted to Nuget->Azure.AI.OpenAI v1.0.0-beta.17
* 

**2023-11-10 0810** Did minor changes/tidying up a little

* All references to GPT3.5 Turbo is removed. All objects/classes have been reduces to just GPT. The sourcefile too.
* The tab that used to have the header GPT3.5Turbo has now the name of the model used in the chat.

**2023-11-07** First commit of this hobby-project. Using WPF, OpenAI API.
On this date there was a couple of updates from the OpenAI community regarding GPT 4 Turbo. And a bunch of other stuff. New to me so I thought I'd try it out and therefore I commited *this* *stuff* to Github.
Two models available in the **GPT4 Turbo** family at this time:

1. gpt-4-1106-preview - works "*out of the box*" in my code
*The latest GPT-4 model with improved instruction following, JSON mode, reproducible outputs, parallel function calling, and more. Returns a maximum of 4,096 output tokens. This preview model is not yet suited for production traffic*\_
2. gpt-4-vision-preview - this can be interesting to try out.
*Ability to understand images, in addition to all other GPT-4 Turbo capabilties. Returns a maximum of 4,096 output tokens. This is a preview model version and not suited yet for production traffic*

**DALL-E**
DALL-E v3 - I will see if I can update to this version.
Updated to version 3, But now I can't use EDIT or VARIATIONS..but I can see that the pics are "good".

**Assistant API**
Haven't tried yet. Can be interesting.

**TTS**
Will not update or do more here for the time being. I've allready tried using TTS via "Microsoft.CognitiveServices.Speech". I think this one is cooler. More voices/dialects from all over the world. Narrowed it down to English, Norwegian and Tagalog.
But if I'm right OpenAI offers a way so that you do not have to use this. Which is good. See source SpeechSynthesis.cs.

**GPTs**
Somehing to look into in the future? See their quotes below:
*We launched a new feature called GPTs. GPTs combine instructions, data, and capabilities into a customized version of ChatGPT
We launched a new feature called GPTs. GPTs combine instructions, data, and capabilities into a customized version of ChatGPT*


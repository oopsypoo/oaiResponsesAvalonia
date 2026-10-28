# oaiResponsesAvalonia

A cross-platform development workspace for exploring the OpenAI Responses API and creating, building, and working with software projects.

The application is built with Avalonia. Its current development tools focus on local .NET workspaces, with operations designed to be constrained to a selected workspace rather than exposing an unrestricted shell or filesystem.

## What it can do

- Work with the OpenAI Responses API.
- Use integrated terminal and developer tools for local development workflows.
- Inspect files in a selected workspace and, when enabled, create or edit files.
- Create a .NET solution and project from supported templates.
- Build, clean, rebuild, and run supported .NET projects.
- Track processes started through the developer tools and inspect their output.
- Keep tool operations and their workspace context available in session logs.

File changes and execution capabilities are separately controlled. Depending on the user's settings, operations may require approval. The developer tools do not provide a general-purpose, unrestricted shell.

## Supported and tested platforms

The application is cross-platform and is regularly tested on:

- Ubuntu 26.04.1 LTS
- Windows 11

## Run from source

The application targets .NET 10. Install the .NET 10 SDK, then run:

```bash
dotnet run --project oaiResponsesAvalonia/oaiResponsesAvalonia.csproj
```

Use the application's configuration for the API access required by the features you use.

## Project notes

- [`TODO.md`](TODO.md) lists planned and unfinished work.
- [`CHANGELOG.md`](CHANGELOG.md) records notable project changes.

The project is evolving toward a broader development tool while continuing to support the OpenAI Responses API and cross-platform desktop use.

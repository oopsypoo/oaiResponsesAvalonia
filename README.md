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

### Experimental sync batch

New snapshot filenames include the device hostname; this name is visible wherever
the snapshot is stored or uploaded. Older snapshot names remain supported.

The batch verifies and restores copies of every ZIP in a snapshot directory, compares
each verified pair for possible session matches, and runs a controlled synthetic
merge/deletion rehearsal. It never uploads to Proton or applies changes to live data.
Supply an existing output directory separate from the input directory:

```bash
dotnet run --project tests/SyncBatch -- "<snapshot-directory>" "<output-directory>"
```

Each run creates a uniquely named output folder with `report.json`, private snapshot
copies, isolated restores, and synthetic rehearsal results. These can contain private
conversation data; protect them like the original snapshots. They are retained until
you remove them yourself. Exit code 1 means a check failed; 2 means the batch could
not start. Possible matches are marked REVIEW and listed by session ID in the
`comparisons` directory; no matches do not prove independence. REVIEW does not
cause a nonzero exit code.
The rehearsal does not merge real histories, test a production sync protocol or
perform visual verification. Run the batch separately on Linux and Windows.

To run the controlled synthetic rehearsal on a copy of an existing experimental
combined baseline, use `dotnet run --project tests/SyncBatch -- --baseline
"<baseline-directory>" "<output-directory>"`. The result retains the baseline's
conversations, but its simulated changes are synthetic. It is not a general merge
or evidence that the baseline was visually reviewed.

The project is evolving toward a broader development tool while continuing to support the OpenAI Responses API and cross-platform desktop use.

# TODO

This file tracks planned and unfinished work. Completed, notable changes belong in [`CHANGELOG.md`](CHANGELOG.md).

## Product direction

- [ ] Grow the application into a general-purpose development tool that helps users start, create, and build projects, while retaining the Responses API experience.
- [ ] Keep the application cross-platform with Avalonia; regularly verify the supported workflows on Ubuntu 26.04.1 LTS and Windows 11.

## Responses API

- [ ] Review API requests, responses, and supported features against current official Responses API information; update the application and tests where needed.
- [ ] Make API compatibility easier to maintain by keeping API behavior clear, testable, and separate from presentation code.

## Terminal and developer tools

- [ ] Improve terminal workflows for starting, creating, inspecting, and building projects without introducing unrestricted shell or filesystem access by default.
- [ ] Make common development operations easier to discover, follow, and diagnose, including clear output and handling of long-running tasks.
- [ ] Use current API and tool information from MCP servers where appropriate; make its source and freshness visible, and handle unavailable or changed information safely.
- [ ] Define the intended behavior and access boundaries for the Developer Tools **Scope** options (**Current repository**, **Workspace**, **Custom**), then re-enable the hidden Scope control. Confirm that each option applies the expected boundaries.

## Release and documentation

- [ ] Decide whether and when to adopt release version numbers. Until then, keep the changelog useful without inventing versions.
- [ ] Keep the README focused on the application and its current capabilities; record major changes in the changelog.

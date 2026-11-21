# TODO

This file tracks planned and unfinished work. Completed, notable changes belong in [`CHANGELOG.md`](CHANGELOG.md).

## Product direction

* \[ ] Grow the application into a general-purpose development tool that helps users start, create, and build projects, while retaining the Responses API experience.
* \[ ] Keep the application cross-platform with Avalonia; regularly verify the supported workflows on Ubuntu 26.04.1 LTS and Windows 11.
* \[ ] Add synchronization of data over devices. Use a cloud-service of choice

## Responses API

* \[ ] Review API requests, responses, and supported features against current official Responses API information; update the application and tests where needed.
* \[ ] Make API compatibility easier to maintain by keeping API behavior clear, testable, and separate from presentation code.
* \[ ] Low priority: Refactor the Responses model selector to use a typed `ItemsSource` instead of adding `ComboBoxItem` instances in code, improving IntelliSense type inference and addressing the `MC3101` warning.
* \[ ] Remove OpenAIs web-tool and make a local tool/function (save some tokens I'm guessing)

## Terminal and developer tools

* \[x] Tidy up help messages (messy). Too much! (or just format the information better?) -better now. Still some irritating things. Needs an audit
* \[x] autocompletion on paths/files (it only shows what is available and does not actually fill out what is possible to fill out)(important for effectiveness). Needs an audit.
* \[ ] Audit terminal auto-completion: many commands' completions are out of date (for example, new `ls` options `-r`, `-S`, `-t`, `-X`, `--sort=<key>`, `--reverse`, and other commands' flags and values). Make completion derive from the same option definitions as parsing and help.
* \[ ] command '***open***': autocompletion can suggest 'programs from ´*open with*`-context menu'?
* \[ ] Improve terminal workflows for starting, creating, inspecting, and building projects without introducing unrestricted shell or filesystem access by default.
* \[ ] Make common development operations easier to discover, follow, and diagnose, including clear output and handling of long-running tasks.
* \[ ] Use current API and tool information from MCP servers where appropriate; make its source and freshness visible, and handle unavailable or changed information safely.
* \[ ] Define the intended behavior and access boundaries for the Developer Tools **Scope** options (**Current repository**, **Workspace**, **Custom**), then re-enable the hidden Scope control. Confirm that each option applies the expected boundaries.
* \[ ] Evaluate creating animated image formats (for example, GIF or animated WebP) from multiple still images, including frame timing and format behavior.
* \[ ] Open activity window should not be hidden behind the "Developers Tools". It should show all activity for all/everything.

## Images and gallery

* [ ] Low priority: Design and implement image export/file-management actions for one or more files from the image-path gallery. Decide whether this should be a dedicated export workflow or copy/move actions; clarify whether edited images saved in the user's filepath are treated differently from other gallery files. Allow moving a file only when it is bound by the user-context; otherwise offer copy without moving the source.
* [ ] Add font-support => add text on images
* [ ] Select parts of an image using different shapes, and freehand =>filters only apply to selected. Also => cut, delete, move area..?
* [ ] add zoom; in, out, fit, 100% (like other previews)
* [ ] Move/delete/cut/paste images in preview-gallery (local)

## Developer Tools

* [ ] Audit of dotnet project/solution listings. Seems like text-files! .txt is difficult to read, when added :D
* [ ] Add function for AI: 'dotnet test'

## Release and documentation

* \[ ] Decide whether and when to adopt release version numbers. Until then, keep the changelog useful without inventing versions.
* \[ ] Keep the README focused on the application and its current capabilities; record major changes in the changelog.


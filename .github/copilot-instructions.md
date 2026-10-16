# Copilot Instructions

## Project Guidelines
- Treat submitted conversation turns as immutable history: selected turns and their attachments should not be editable or removable; pending attachment controls apply only to unsent requests. Any history attachment display should be read-only.

## File Handling
- For Linux, use GIO/libgio to enumerate installed handlers for a file's MIME type and populate a context-menu submenu dynamically; retain Avalonia's TopLevel.Launcher.LaunchFileAsync for the default Open action when feasible.
- When refactoring GIO application enumeration, use a reusable helper with the signature `static List<AppEntry> ReadList(IntPtr list, string? defaultId = null)` to convert native GList results and optionally mark the default app.

## Developer Tools
- For the Developer Tools activity UI, show it in a separate user-openable and closable window rather than an inline live list, to avoid consuming space in the main window.
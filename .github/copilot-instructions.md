# Copilot Instructions

## Project Guidelines
- Treat submitted conversation turns as immutable history: selected turns and their attachments should not be editable or removable; pending attachment controls apply only to unsent requests. Any history attachment display should be read-only.

## File Handling
- For Linux Open With behavior, prefer GIO/libgio to enumerate installed handlers for a file's MIME type and launch a selected app from a dynamically populated context-menu submenu; keep the default Open action using Avalonia's TopLevel.Launcher.LaunchFileAsync when feasible.
- When refactoring GIO application enumeration, use a reusable helper with the signature `static List<AppEntry> ReadList(IntPtr list, string? defaultId = null)` to convert native GList results and optionally mark the default app.
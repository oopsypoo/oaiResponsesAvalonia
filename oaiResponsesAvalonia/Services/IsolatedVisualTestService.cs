using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;

namespace oaiResponsesAvalonia.Services;

/// <summary>Creates a disposable copy for a separate visual-test process; never opens the live database.</summary>
public static class IsolatedVisualTestService
{
    public const string Argument = "--isolated-visual-test";
    private const string Marker = ".isolated-visual-test";
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string RestoreRoot => Path.Combine(AppPaths.DefaultDataDirectory, "ProtonSync", "RestoreTests");
    public static string VisualTestRoot => Path.Combine(AppPaths.DefaultDataDirectory, "ProtonSync", "VisualTests");

    public static string CreateCopy(string restoreDirectory) => CreateCopy(restoreDirectory, RestoreRoot, VisualTestRoot);

    internal static string CreateCopy(string restoreDirectory, string restoreRoot, string visualTestRoot)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(restoreDirectory));
        EnsureDirectChild(source, restoreRoot, "restore_");
        EnsureNoLinks(source);
        string database = Path.Combine(source, "localhistory.db");
        string manifest = Path.Combine(source, "manifest.json");
        if (!File.Exists(database) || !File.Exists(manifest))
            throw new InvalidDataException("Select a prepared restore folder containing localhistory.db and manifest.json.");
        EnsureNoLinks(database);
        EnsureNoLinks(manifest);
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(database + suffix)) EnsureNoLinks(database + suffix);

        string? existing = Path.GetFullPath(visualTestRoot);
        while (!Directory.Exists(existing))
            existing = Path.GetDirectoryName(existing) ?? throw new InvalidDataException("Cannot find a visual-test parent directory.");
        EnsureNoLinks(existing);
        Directory.CreateDirectory(visualTestRoot);
        EnsureNoLinks(visualTestRoot);
        string destination = Path.Combine(visualTestRoot, $"visual_{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        try
        {
            var sourceString = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            var targetString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(destination, "localhistory.db"), Pooling = false };
            using (var input = new SqliteConnection(sourceString.ToString()))
            using (var output = new SqliteConnection(targetString.ToString()))
            {
                input.Open();
                output.Open();
                input.BackupDatabase(output);
            }

            foreach (string folder in new[] { "images", "files" })
            {
                string from = Path.Combine(source, folder);
                if (!Directory.Exists(from))
                    throw new InvalidDataException($"The prepared restore is missing its {folder} folder.");
                CopyDirectory(from, Path.Combine(destination, folder));
            }
            ValidateMediaReferences(destination);
            File.WriteAllText(Path.Combine(destination, Marker), "Disposable visual restore test\n");
            return destination;
        }
        catch
        {
            try { Directory.Delete(destination, true); } catch { /* Preserve the original failure. */ }
            throw;
        }
    }

    public static string ValidateVisualDirectory(string directory)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        EnsureDirectChild(full, VisualTestRoot, "visual_");
        EnsureNoLinks(full);
        foreach (string path in new[] { Path.Combine(full, Marker), Path.Combine(full, "localhistory.db"), Path.Combine(full, "images"), Path.Combine(full, "files") })
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new InvalidDataException("The visual-test directory is incomplete.");
            EnsureNoLinks(path);
        }
        ValidateMediaReferences(full);
        return full;
    }

    public static void Launch(string directory)
    {
        directory = ValidateVisualDirectory(directory);
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the application executable.");
        var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(IsolatedVisualTestService).Assembly.Location);
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(directory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the visual-test application.");
    }

    private static void ValidateMediaReferences(string directory)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "localhistory.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(cs.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT LocalPath FROM Media";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string path = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            const string prefix = "media/";
            string name = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : string.Empty;
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\') ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"The restore contains an unsafe media reference: {path}");
            string image = Path.Combine(directory, "images", name);
            string file = Path.Combine(directory, "files", name);
            if (!File.Exists(image) && !File.Exists(file))
                throw new InvalidDataException($"The restore is missing referenced media: {path}");
            if (File.Exists(image)) EnsureNoLinks(image);
            if (File.Exists(file)) EnsureNoLinks(file);
        }
    }

    private static void EnsureDirectChild(string directory, string root, string prefix)
    {
        string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? parent = Path.GetDirectoryName(directory);
        string name = Path.GetFileName(directory);
        if (!string.Equals(parent, expected, PathComparison) || !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidDataException($"Select a valid {prefix} folder directly under {expected}.");
    }

    private static void EnsureNoLinks(string path)
    {
        // Check all existing ancestors as well, so a linked RestoreTests or VisualTests root is rejected.
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Symbolic links are not supported in visual tests: {current}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        EnsureNoLinks(source);
        Directory.CreateDirectory(destination);
        foreach (string path in Directory.EnumerateFileSystemEntries(source))
        {
            EnsureNoLinks(path);
            string target = Path.Combine(destination, Path.GetFileName(path));
            if (Directory.Exists(path)) CopyDirectory(path, target);
            else File.Copy(path, target, false);
        }
    }
}

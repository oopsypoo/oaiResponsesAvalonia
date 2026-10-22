using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using oaiResponsesAvalonia.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace oaiResponsesAvalonia.Services;

internal sealed class AppConsoleService
{
    private const int MaximumFileBytes = 32 * 1024;
    private const int MaximumListItems = 200;
    private readonly string _rootDirectory;
    private string _currentDirectory;
    private bool _databaseConnected;

    public AppConsoleService()
    {
        _rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataDirectory));
        Directory.CreateDirectory(_rootDirectory);
        _currentDirectory = _rootDirectory;
    }

    public string Prompt => $"~{GetRelativeCurrentDirectory()} $ ";

    public AppConsoleResult Execute(string commandLine)
    {
        try
        {
            List<string> args = Tokenize(commandLine);
            if (args.Count == 0)
                return new AppConsoleResult(string.Empty, Prompt);

            string command = args[0].ToLowerInvariant();
            string output = command switch
            {
                "help" => HelpText,
                "pwd" => _currentDirectory,
                "ls" => ListDirectory(args.Skip(1).ToArray()),
                "cd" => ChangeDirectory(args.Skip(1).ToArray()),
                "cat" => ReadFile(args.Skip(1).ToArray()),
                "connect" => ConnectDatabase(args.Skip(1).ToArray()),
                "disconnect" => DisconnectDatabase(args.Skip(1).ToArray()),
                "sessions" => HandleSessions(args.Skip(1).ToArray()),
                "clear" => "\u001b[2J\u001b[H",
                _ => $"{command}: command not found. Type 'help' to see available commands."
            };

            return new AppConsoleResult(output, Prompt);
        }
        catch (Exception ex)
        {
            return new AppConsoleResult($"error: {ex.Message}", Prompt);
        }
    }

    private string ListDirectory(string[] args)
    {
        bool showHidden = args.Contains("-a", StringComparer.Ordinal);
        string[] paths = args.Where(arg => arg != "-a").ToArray();
        if (paths.Length > 1)
            throw new InvalidOperationException("usage: ls [-a] [path]");

        string directory = paths.Length == 0 ? _currentDirectory : ResolvePath(paths[0]);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"not a directory: {paths.FirstOrDefault() ?? directory}");

        var entries = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(directory)
                     .OrderBy(path => !Directory.Exists(path))
                     .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(path);
            if (!showHidden && (name.StartsWith(".", StringComparison.Ordinal) || IsHidden(path)))
                continue;

            if (IsReparsePoint(path))
                continue;

            entries.Add(Directory.Exists(path) ? name + "/" : name);
            if (entries.Count >= MaximumListItems)
            {
                entries.Add($"... listing limited to {MaximumListItems} entries");
                break;
            }
        }

        return entries.Count == 0 ? "(empty)" : string.Join("\n", entries);
    }

    private string ChangeDirectory(string[] args)
    {
        if (args.Length > 1)
            throw new InvalidOperationException("usage: cd [path]");

        string target = args.Length == 0 || args[0] == "~" ? _rootDirectory : ResolvePath(args[0]);
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException($"no such directory: {args.FirstOrDefault()}");

        EnsureNoReparsePoints(target);
        _currentDirectory = target;
        return string.Empty;
    }

    private string ReadFile(string[] args)
    {
        if (args.Length != 1)
            throw new InvalidOperationException("usage: cat <file>");

        string filePath = ResolvePath(args[0]);
        if (string.Equals(Path.GetFileName(filePath), "settings.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("settings.json may contain credentials and is not displayed by the console.");
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"no such file: {args[0]}");

        EnsureNoReparsePoints(filePath);
        var info = new FileInfo(filePath);
        if (info.Length > MaximumFileBytes)
            throw new InvalidOperationException($"file is larger than the {MaximumFileBytes / 1024} KiB console limit.");

        byte[] bytes = File.ReadAllBytes(filePath);
        if (bytes.Contains((byte)0))
            throw new InvalidOperationException("binary files are not displayed by cat.");

        return Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
    }

    private string ConnectDatabase(string[] args)
    {
        if (args.Length > 0)
            throw new InvalidOperationException("usage: connect");
        if (_databaseConnected)
            return "already connected to the application database (read-only).";

        string databasePath = AppDbContext.DatabaseFilePath;
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("application database was not found", databasePath);

        using AppDbContext db = CreateReadOnlyDbContext(databasePath);
        _ = db.Database.CanConnect();
        _databaseConnected = true;
        return "connected to the application database (read-only).";
    }

    private string DisconnectDatabase(string[] args)
    {
        if (args.Length > 0)
            throw new InvalidOperationException("usage: disconnect");
        if (!_databaseConnected)
            return "not connected.";

        _databaseConnected = false;
        return "disconnected from the application database.";
    }

    private string HandleSessions(string[] args)
    {
        if (args.Length == 0)
            throw new InvalidOperationException("usage: sessions list [--limit N] | sessions show <id>");
        if (!_databaseConnected)
            throw new InvalidOperationException("connect to the application database first.");

        string databasePath = AppDbContext.DatabaseFilePath;
        using AppDbContext db = CreateReadOnlyDbContext(databasePath);

        return args[0].ToLowerInvariant() switch
        {
            "list" => ListSessions(db, args.Skip(1).ToArray()),
            "show" => ShowSession(db, args.Skip(1).ToArray()),
            _ => throw new InvalidOperationException("usage: sessions list [--limit N] | sessions show <id>")
        };
    }

    private static string ListSessions(AppDbContext db, string[] args)
    {
        int limit = 20;
        if (args.Length == 2 && args[0] == "--limit" &&
            int.TryParse(args[1], out int requestedLimit) && requestedLimit is >= 1 and <= 100)
        {
            limit = requestedLimit;
        }
        else if (args.Length != 0)
        {
            throw new InvalidOperationException("usage: sessions list [--limit 1..100]");
        }

        var sessions = db.Sessions.AsNoTracking()
            .OrderByDescending(session => session.LastUsedAt)
            .Take(limit)
            .Select(session => new
            {
                session.Id,
                session.Title,
                session.LastUsedAt,
                MessageCount = session.Messages.Count
            })
            .ToList();

        if (sessions.Count == 0)
            return "no sessions found.";

        return string.Join("\n", sessions.Select(session =>
            $"{session.Id,5}  {session.LastUsedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {session.MessageCount,3} messages  {OneLine(session.Title, 72)}"));
    }

    private static string ShowSession(AppDbContext db, string[] args)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out int id) || id <= 0)
            throw new InvalidOperationException("usage: sessions show <id>");

        var session = db.Sessions.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new { item.Id, item.Title, item.CreatedAt, item.LastUsedAt })
            .FirstOrDefault();
        if (session is null)
            return $"session {id} not found.";

        var messages = db.Messages.AsNoTracking()
            .Where(message => message.ChatSessionId == id)
            .OrderByDescending(message => message.Timestamp)
            .Take(10)
            .Select(message => new { message.Role, message.Content, message.Timestamp })
            .ToList();

        var lines = new List<string>
        {
            $"Session {session.Id}: {session.Title}",
            $"Created: {session.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            $"Last used: {session.LastUsedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            "Recent messages (up to 10):"
        };

        foreach (var message in messages.AsEnumerable().Reverse())
        {
            string content = OneLine(message.Content, 240);
            lines.Add($"[{message.Timestamp.ToLocalTime():HH:mm:ss}] {message.Role}: {content}");
        }

        return string.Join("\n", lines);
    }

    private string ResolvePath(string userPath)
    {
        if (string.IsNullOrWhiteSpace(userPath))
            throw new InvalidOperationException("path cannot be empty.");

        string normalized = userPath.Replace('/', Path.DirectorySeparatorChar);
        string candidate;
        if (userPath == "~")
        {
            candidate = _rootDirectory;
        }
        else if (userPath.StartsWith("~/", StringComparison.Ordinal) || userPath.StartsWith("~\\", StringComparison.Ordinal))
        {
            candidate = Path.Combine(_rootDirectory, normalized[2..]);
        }
        else if (userPath.StartsWith("/", StringComparison.Ordinal))
        {
            candidate = Path.Combine(_rootDirectory, normalized.TrimStart(Path.DirectorySeparatorChar));
        }
        else if (Path.IsPathRooted(normalized))
        {
            throw new InvalidOperationException("absolute host paths are not available; use paths relative to the app data directory.");
        }
        else
        {
            candidate = Path.Combine(_currentDirectory, normalized);
        }

        string fullPath = Path.GetFullPath(candidate);
        string comparisonRoot = _rootDirectory + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.Equals(_rootDirectory, comparison) && !fullPath.StartsWith(comparisonRoot, comparison))
            throw new InvalidOperationException("path is outside the app data directory.");

        EnsureNoReparsePoints(fullPath);
        return fullPath;
    }

    private void EnsureNoReparsePoints(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (fullPath.Equals(_rootDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return;

        string relative = Path.GetRelativePath(_rootDirectory, fullPath);
        string current = _rootDirectory;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current))
                throw new InvalidOperationException("symbolic links and reparse points are not followed by the app console.");
        }
    }

    private bool IsHidden(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private string GetRelativeCurrentDirectory()
    {
        string relative = Path.GetRelativePath(_rootDirectory, _currentDirectory);
        return relative == "." ? string.Empty : "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static AppDbContext CreateReadOnlyDbContext(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new AppDbContext(options);
    }

    private static string OneLine(string? value, int maxLength)
    {
        string text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';
        bool escaping = false;
        bool tokenStarted = false;

        foreach (char character in text)
        {
            if (escaping)
            {
                token.Append(character);
                escaping = false;
                tokenStarted = true;
                continue;
            }

            if (character == '\\' && quote != '\'')
            {
                escaping = true;
                tokenStarted = true;
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                else
                    token.Append(character);
                tokenStarted = true;
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                tokenStarted = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (tokenStarted)
                {
                    result.Add(token.ToString());
                    token.Clear();
                    tokenStarted = false;
                }
            }
            else
            {
                token.Append(character);
                tokenStarted = true;
            }
        }

        if (escaping)
            token.Append('\\');
        if (quote != '\0')
            throw new InvalidOperationException("unclosed quote.");
        if (tokenStarted)
            result.Add(token.ToString());

        return result;
    }

    private const string HelpText =
        "Available commands:\n" +
        "  help                     Show this help\n" +
        "  pwd                      Show the current app-data directory\n" +
        "  ls [-a] [path]           List files and folders\n" +
        "  cd [path]                Change directory (confined to app data)\n" +
        "  cat <file>               Display a small text file\n" +
        "  connect                  Connect to the app database (read-only)\n" +
        "  disconnect               Disconnect from the app database\n" +
        "  sessions list [--limit N] List recent sessions (1-100)\n" +
        "  sessions show <id>       Show session details and recent messages\n" +
        "  clear                    Clear the terminal display\n" +
        "\nThis is an app console, not an operating-system shell. File access is limited to the app data directory.";
}

internal sealed record AppConsoleResult(string Output, string Prompt);

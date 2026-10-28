using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using oaiResponsesAvalonia.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace oaiResponsesAvalonia.Services;

[Flags]
// Console-only permissions; these do not define or replace the AI/developer-tool access policy.
internal enum ConsoleOperationCapability
{
    None = 0,
    List = 1,
    Read = 2,
    Search = 4,
    ContextSwitch = 8,
    DatabaseRead = 16
}

internal enum ConsoleContext
{
    Application,
    Database,
    Workspace
}

/// <summary>
/// Implements constrained application commands, not an operating-system shell. Commands are dispatched by the active filesystem or database context.
/// </summary>
internal sealed class AppConsoleService
{
    private const int MaximumFileBytes = 32 * 1024;
    private const int MaximumListItems = 200;
    private const int MaximumDatabaseOutputCharacters = 200_000;
    private readonly string _rootDirectory;
    private string _currentDirectory;
    private readonly bool _workspaceEnabled;
    private readonly string? _workspaceRootDirectory;
    private string? _workspaceCurrentDirectory;
    private string? _connectedDatabasePath;


    public ConsoleContext ActiveContext { get; private set; } = ConsoleContext.Application;
    private int? _databaseSessionId;
    private int? _databaseMessageId;
    private bool _databaseMediaDirectory;
    private int? _databaseMediaId;
    private bool _databaseConnected;

    public AppConsoleService(bool developerToolsEnabled = false, string? workspaceRoot = null)
    {
        _rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataDirectory));
        Directory.CreateDirectory(_rootDirectory);
        _currentDirectory = _rootDirectory;

        string? normalizedWorkspaceRoot = !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot))
            : null;
        _workspaceEnabled = developerToolsEnabled && normalizedWorkspaceRoot is not null && !IsReparsePoint(normalizedWorkspaceRoot);
        _workspaceRootDirectory = _workspaceEnabled
            ? normalizedWorkspaceRoot
            : null;
        _workspaceCurrentDirectory = _workspaceRootDirectory;
    }

    public string Prompt => ActiveContext switch
    {
        ConsoleContext.Database => $"db[{Path.GetFileName(_connectedDatabasePath)}]:{GetDatabaseLocation()} $ ",
        ConsoleContext.Workspace => $"workspace:{GetRelativeCurrentDirectory(workspace: true)} $ ",
        _ => $"app:~{GetRelativeCurrentDirectory()} $ "
    };

    private string CurrentFilesystemRoot => ActiveContext == ConsoleContext.Workspace
        ? _workspaceRootDirectory ?? throw new InvalidOperationException("workspace context is not configured.")
        : _rootDirectory;

    private string CurrentFilesystemDirectory => ActiveContext == ConsoleContext.Workspace
        ? _workspaceCurrentDirectory ?? CurrentFilesystemRoot
        : _currentDirectory;

    private ConsoleOperationCapability AllowedCapabilities => ActiveContext switch
    {
        ConsoleContext.Workspace => ConsoleOperationCapability.List | ConsoleOperationCapability.Read | ConsoleOperationCapability.Search | ConsoleOperationCapability.ContextSwitch,
        ConsoleContext.Database => ConsoleOperationCapability.List | ConsoleOperationCapability.Search | ConsoleOperationCapability.ContextSwitch | ConsoleOperationCapability.DatabaseRead,
        _ => ConsoleOperationCapability.List | ConsoleOperationCapability.Read | ConsoleOperationCapability.Search | ConsoleOperationCapability.ContextSwitch | ConsoleOperationCapability.DatabaseRead
    };

    public AppConsoleResult Execute(string commandLine)
    {
        try
        {
            List<string> args = Tokenize(commandLine);
            if (args.Count == 0)
                return new AppConsoleResult(string.Empty, Prompt);

            string pagerMode = string.Empty;
            string[]? pipedGrepArguments = null;
            int pipeIndex = args.IndexOf("|");
            if (pipeIndex >= 0)
            {
                if (pipeIndex == 0 || args.IndexOf("|", pipeIndex + 1) >= 0 || pipeIndex == args.Count - 1)
                    throw new InvalidOperationException("usage: <command> | grep <search-term> OR <command> | more|less");

                string[] pipeCommand = args.Skip(pipeIndex + 1).ToArray();
                if (pipeCommand[0].Equals("grep", StringComparison.OrdinalIgnoreCase))
                {
                    if (pipeCommand.Length == 1)
                        throw new InvalidOperationException("usage: <command> | grep <search-term>");
                    pipedGrepArguments = pipeCommand.Skip(1).ToArray();
                }
                else if (pipeCommand.Length == 1 && pipeCommand[0].ToLowerInvariant() is "more" or "less")
                {
                    pagerMode = pipeCommand[0].ToLowerInvariant();
                }
                else
                {
                    throw new InvalidOperationException("usage: <command> | grep <search-term> OR <command> | more|less");
                }
                args = args.Take(pipeIndex).ToList();
            }

            if (pipedGrepArguments?.Any(argument => argument.Equals("-h", StringComparison.OrdinalIgnoreCase) || argument.Equals("--help", StringComparison.OrdinalIgnoreCase)) == true)
                return new AppConsoleResult(GetCommandHelp(["grep"]), Prompt, PagerMode: pagerMode);

            int helpIndex = args.FindIndex(argument => argument.Equals("-h", StringComparison.OrdinalIgnoreCase) || argument.Equals("--help", StringComparison.OrdinalIgnoreCase));
            if (helpIndex >= 0)
            {
                string[] helpArgs = args.Where((_, index) => index != helpIndex).ToArray();
                return new AppConsoleResult(GetCommandHelp(helpArgs), Prompt, PagerMode: pagerMode);
            }

            string command = args[0].ToLowerInvariant();
            bool grepReadsFile = command == "grep" && args.Skip(1).Count(argument => !argument.StartsWith("-", StringComparison.Ordinal)) > 1;
            EnsureCommandCapability(command, grepReadsFile);
            if (pipedGrepArguments is not null)
                EnsureCapability(ConsoleOperationCapability.Search, "grep");
            string output = command switch
            {
                "help" => GetContextHelpText(),
                "pwd" => ActiveContext == ConsoleContext.Database ? GetDatabaseLocation() : CurrentFilesystemDirectory,
                "ls" => ActiveContext == ConsoleContext.Database ? ListDatabaseLocation(args.Skip(1).ToArray()) : ListDirectory(args.Skip(1).ToArray()),
                "cd" => ActiveContext == ConsoleContext.Database ? ChangeDatabaseLocation(args.Skip(1).ToArray()) : ChangeDirectory(args.Skip(1).ToArray()),
                "cat" when ActiveContext != ConsoleContext.Database => ReadFile(args.Skip(1).ToArray()),
                "cat" => throw new InvalidOperationException("cat is a filesystem-context command. Switch to application or workspace context to use it."),
                "grep" => Grep(args.Skip(1).ToArray(), null),
                "workspace" => EnterWorkspace(args.Skip(1).ToArray()),
                "app" or "application" => SwitchToApplication(args.Skip(1).ToArray()),
                "database" => SwitchToDatabase(args.Skip(1).ToArray()),
                "connect" => ConnectDatabase(args.Skip(1).ToArray()),
                "disconnect" => DisconnectDatabase(args.Skip(1).ToArray()),
                "sessions" => HandleSessions(args.Skip(1).ToArray()),
                "find" => FindSessions(args.Skip(1).ToArray()),
                "tables" => HandleTables(args.ToArray()),
                "describe" => HandleDescribe(args.Skip(1).ToArray()),
                "messages" => HandleMessages(args.Skip(1).ToArray()),
                "show" when ActiveContext == ConsoleContext.Database => ShowCurrentDatabaseMessage(args.Skip(1).ToArray()),
                "columns" when ActiveContext == ConsoleContext.Database && args.Count == 1 => ListCurrentMessageColumns(),
                "columns" when ActiveContext == ConsoleContext.Database => throw new InvalidOperationException("usage: columns"),
                "columns" => throw new InvalidOperationException("columns is available only in the database context."),
                "show" => throw new InvalidOperationException("show is available only in the database context."),
                "db" => HandleDatabaseAlias(args.Skip(1).ToArray()),
                "clear" => "\u001b[2J\u001b[H",
                "quit" or "exit" => "Goodbye.",
                _ => $"{command}: command not found. Type 'help' to see available commands."
            };

            if (pipedGrepArguments is not null)
                output = Grep(pipedGrepArguments, output);

            bool closeWindow = command is "quit" or "exit";
            bool knownCommand = command is "help" or "pwd" or "ls" or "cd" or "cat" or "grep" or
                "connect" or "disconnect" or "workspace" or "app" or "application" or "database" or "sessions" or "find" or "tables" or "describe" or "messages" or "show" or "columns" or "db" or "clear" or "quit" or "exit";
            string kind = !knownCommand ? "error" : command is "connect" or "disconnect" or "workspace" or "app" or "application" or "database" ? "success" : "normal";
            return new AppConsoleResult(output, closeWindow ? string.Empty : Prompt, closeWindow, kind, pagerMode);
        }
        catch (Exception ex)
        {
            return new AppConsoleResult($"error: {ex.Message}", Prompt, Kind: "error");
        }
    }

    private void EnsureCommandCapability(string command, bool grepReadsFile = false)
    {
        if (command == "connect" && ActiveContext != ConsoleContext.Application)
            throw new InvalidOperationException("switch to application context before connecting to a database.");
        if (command is "sessions" or "find" or "tables" or "describe" or "messages" or "show" or "columns" && ActiveContext != ConsoleContext.Database)
            throw new InvalidOperationException($"'{command}' requires the database context. Use 'database' to switch contexts.");

        ConsoleOperationCapability required = command switch
        {
            "ls" => ConsoleOperationCapability.List,
            "cat" => ConsoleOperationCapability.Read,
            "grep" => ConsoleOperationCapability.Search | (grepReadsFile ? ConsoleOperationCapability.Read : ConsoleOperationCapability.None),
            "connect" or "disconnect" or "workspace" or "app" or "application" or "database" => ConsoleOperationCapability.ContextSwitch,
            "sessions" or "find" or "tables" or "describe" or "messages" or "show" or "columns" => ConsoleOperationCapability.DatabaseRead,
            "db" => ConsoleOperationCapability.DatabaseRead,
            _ => ConsoleOperationCapability.None
        };

        EnsureCapability(required, command);
    }

    private void EnsureCapability(ConsoleOperationCapability required, string operation)
    {
        if ((AllowedCapabilities & required) != required)
            throw new InvalidOperationException($"'{operation}' is not available in the {ActiveContext.ToString().ToLowerInvariant()} context.");
    }

    private string EnterWorkspace(string[] args)
    {
        if (args.Length != 0)
            throw new InvalidOperationException("usage: workspace");
        if (!_workspaceEnabled || _workspaceRootDirectory is null)
            throw new InvalidOperationException("workspace context is unavailable. Enable Developer Tools and configure an existing workspace root first.");

        ActiveContext = ConsoleContext.Workspace;
        return $"workspace context active at {_workspaceRootDirectory} (read-only).";
    }

    private string SwitchToApplication(string[] args)
    {
        if (args.Length != 0)
            throw new InvalidOperationException("usage: app");
        ActiveContext = ConsoleContext.Application;
        return "application-data context active.";
    }

    private string SwitchToDatabase(string[] args)
    {
        if (args.Length != 0)
            throw new InvalidOperationException("usage: database");
        EnsureDatabaseConnected();
        ActiveContext = ConsoleContext.Database;
        return $"database context active: {Path.GetFileName(_connectedDatabasePath)}.";
    }

    private string HandleDatabaseAlias(string[] args)
    {
        if (args.Length == 0 || (args.Length == 1 && args[0].Equals("help", StringComparison.OrdinalIgnoreCase)))
            return GetDatabaseHelpText();
        return HandleDatabaseBrowser(args);
    }

    private string GetContextHelpText() => ActiveContext switch
    {
        ConsoleContext.Database => GetDatabaseHelpText(),
        ConsoleContext.Workspace => WorkspaceHelpText,
        _ => FilesystemHelpText
    };

    private string GetCommandHelp(string[] args)
    {
        if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            return GetContextHelpText();

        string command = args[0].ToLowerInvariant();
        bool databaseAlias = command == "db";
        if (databaseAlias)
        {
            args = args.Skip(1).ToArray();
            command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
            if (command == "help")
                return GetDatabaseHelpText();
        }

        string synopsis = command switch
        {
            "ls" when ActiveContext == ConsoleContext.Database || databaseAlias => "usage: ls [--columns N[,N...]]\nLists sessions at /, messages inside a session, or fields inside a message.",
            "ls" => "usage: ls [-a] [-l] [path]\nLists entries in the current filesystem context (application data or workspace). -a includes hidden entries; -l shows type, size, and last-modified time.",
            "grep" => "usage: grep <search-term> <file> OR <command> | grep <search-term>\nSearches literal text, case-insensitively, with line numbers and colored matches by default. File search is available in application and workspace contexts; piped output can be searched in database context too. This is an application command, not an operating-system grep.",
            "cd" when ActiveContext == ConsoleContext.Database || databaseAlias => "usage: cd <session-id|message-id|media|..|/>\nNavigates sessions, messages, and a message's media records; does not change filesystem directories.",
            "cd" => "usage: cd [path]\nChanges directory within the current filesystem context.",
            "pwd" when ActiveContext == ConsoleContext.Database || databaseAlias => "usage: pwd\nShows the current database location.",
            "pwd" => "usage: pwd\nShows the current app-data directory.",
            "show" when ActiveContext == ConsoleContext.Database || databaseAlias => "usage: show [--column <name>|--field <name>]\nRequires a message location. Without an option, displays all message fields; with a column, displays one value and pretty-prints JSON.",
            "columns" when ActiveContext == ConsoleContext.Database || databaseAlias => "usage: columns\nRequires a message or media-record location. Lists field names and their 1-based indexes.",
            "connect" => "usage: connect [database-file]\nConnects read-only to a database file under app data and switches this terminal to database context.",
            "disconnect" => "usage: disconnect\nDisconnects and returns this terminal to application-data context.",
            "cat" => "usage: cat <file>\nDisplays a small text file in the current filesystem context.",
            "workspace" => "usage: workspace\nEnters the configured workspace context in read-only mode. Requires Developer Tools to be enabled and an existing workspace root.",
            "app" or "application" => "usage: app\nReturns to the application-data context.",
            "database" => "usage: database\nSwitches to the currently connected database context.",
            "find" => "usage: find sessions [--sid ID] [--has-media] [--role VALUE] [--content TEXT] [--model VALUE] [--reasoning-level VALUE] [--tool VALUE] [--same-message]\nFilters combine with AND. By default filters may match different messages; --same-message requires one message to satisfy all message filters. Results show session IDs and matching message IDs in one column per message filter; content is not printed.",
            "sessions" when ActiveContext == ConsoleContext.Database => "usage: sessions list [--limit N] | sessions show <id>\nLists recent sessions or displays session details.",
            "tables" when ActiveContext == ConsoleContext.Database => "usage: tables\nLists database tables read-only.",
            "describe" when ActiveContext == ConsoleContext.Database => "usage: describe <table>\nShows database column details.",
            "messages" when ActiveContext == ConsoleContext.Database => "usage: messages show <id> [--field <column>]\nDisplays stored message fields or one selected field.",
            "help" => GetContextHelpText(),
            _ => "No help is available for '" + command + "' in the current context.\n" + GetContextHelpText()
        };
        return "Application console command help (not an operating-system shell command).\nContext: " +
            (ActiveContext == ConsoleContext.Database ? $"database '{Path.GetFileName(_connectedDatabasePath)}' at {GetDatabaseLocation()}" : ActiveContext == ConsoleContext.Workspace ? $"workspace at {CurrentFilesystemDirectory}" : databaseAlias ? "database (not connected)" : $"application data at {_currentDirectory}") +
            "\n" + synopsis;
    }

    private string ListDirectory(string[] args)
    {
        bool showHidden = args.Contains("-a", StringComparer.Ordinal);
        bool detailed = args.Contains("-l", StringComparer.Ordinal);
        string[] paths = args.Where(arg => arg is not ("-a" or "-l")).ToArray();
        if (args.Any(arg => arg.StartsWith("-", StringComparison.Ordinal) && arg is not ("-a" or "-l")))
            throw new InvalidOperationException("usage: ls [-a] [-l] [path]");
        if (paths.Length > 1)
            throw new InvalidOperationException("usage: ls [-a] [-l] [path]");

        bool workspace = ActiveContext == ConsoleContext.Workspace;
        string directory = paths.Length == 0 ? CurrentFilesystemDirectory : ResolvePath(paths[0], workspace);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"not a directory: {paths.FirstOrDefault() ?? directory}");
        EnsureNoReparsePoints(directory, workspace);

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

            bool isDirectory = Directory.Exists(path);
            if (detailed)
            {
                long size = isDirectory ? 0 : new FileInfo(path).Length;
                DateTime modified = isDirectory ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
                entries.Add($"{(isDirectory ? "dir" : "file"),-4} {size,12} {modified:yyyy-MM-dd HH:mm}  {name}{(isDirectory ? "/" : string.Empty)}");
            }
            else
            {
                entries.Add(isDirectory ? name + "/" : name);
            }
            if (entries.Count >= MaximumListItems)
            {
                entries.Add($"... listing limited to {MaximumListItems} entries");
                break;
            }
        }

        if (entries.Count == 0)
            return "(empty)";
        return detailed
            ? "Type  Size (bytes)  Last modified     Name\n" + string.Join("\n", entries)
            : string.Join("\n", entries);
    }

    private string ChangeDirectory(string[] args)
    {
        if (args.Length > 1)
            throw new InvalidOperationException("usage: cd [path]");

        bool workspace = ActiveContext == ConsoleContext.Workspace;
        string root = workspace ? CurrentFilesystemRoot : _rootDirectory;
        string target = args.Length == 0 || args[0] == "~" ? root : ResolvePath(args[0], workspace);
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException($"no such directory: {args.FirstOrDefault()}");

        EnsureNoReparsePoints(target, workspace);
        if (workspace)
            _workspaceCurrentDirectory = target;
        else
            _currentDirectory = target;
        return string.Empty;
    }

    private string ReadFile(string[] args)
    {
        if (args.Length != 1)
            throw new InvalidOperationException("usage: cat <file>");

        bool workspace = ActiveContext == ConsoleContext.Workspace;
        string filePath = ResolvePath(args[0], workspace);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"no such file: {args[0]}");

        EnsureNoReparsePoints(filePath, workspace);
        var info = new FileInfo(filePath);
        if (info.Length > MaximumFileBytes)
            throw new InvalidOperationException($"file is larger than the {MaximumFileBytes / 1024} KiB console limit.");

        byte[] bytes = File.ReadAllBytes(filePath);
        if (bytes.Contains((byte)0))
            throw new InvalidOperationException("binary files are not displayed by cat.");

        return Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Searches application-console output or a confined text file. This is a deliberately small, literal-search command, not a system grep process.
    /// </summary>
    private string Grep(string[] args, string? input)
    {
        var operands = new List<string>();
        foreach (string argument in args)
        {
            if (argument is "-n" or "-i")
                continue; // Line numbers and case-insensitive matching are the console defaults.
            if (argument.StartsWith("-", StringComparison.Ordinal))
                throw new InvalidOperationException("usage: grep <search-term> [file]; supported options: -n -i (both enabled by default)");
            operands.Add(argument);
        }

        if (operands.Count is < 1 or > 2)
            throw new InvalidOperationException("usage: grep <search-term> [file], or <command> | grep <search-term>");
        if (input is not null && operands.Count != 1)
            throw new InvalidOperationException("when filtering piped output, provide only the search term: <command> | grep <search-term>");

        string searchTerm = operands[0];
        if (string.IsNullOrEmpty(searchTerm))
            throw new InvalidOperationException("search term cannot be empty.");

        if (input is null)
        {
            if (operands.Count != 2)
                throw new InvalidOperationException("grep needs a file in filesystem context or input from a pipe; example: ls | grep notes");
            if (ActiveContext == ConsoleContext.Database)
                throw new InvalidOperationException("grep file search is available only in application or workspace context; in database context, pipe a command such as ls to grep.");
            input = ReadFile([operands[1]]);
        }

        string[] lines = input.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var matches = new List<string>();
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];
            if (line.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            matches.Add($"{lineIndex + 1}:" + ColorizeMatches(line, searchTerm));
        }
        return matches.Count == 0 ? "(no matches)" : string.Join("\n", matches);
    }

    private static string ColorizeMatches(string line, string searchTerm)
    {
        const string highlight = "\u001b[1;31m";
        const string reset = "\u001b[0m";
        var output = new StringBuilder();
        int cursor = 0;
        while (cursor < line.Length)
        {
            int match = line.IndexOf(searchTerm, cursor, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                output.Append(line, cursor, line.Length - cursor);
                break;
            }
            output.Append(line, cursor, match - cursor);
            output.Append(highlight).Append(line, match, searchTerm.Length).Append(reset);
            cursor = match + searchTerm.Length;
        }
        return output.ToString();
    }

    private string ConnectDatabase(string[] args)
    {
        if (args.Length > 1)
            throw new InvalidOperationException("usage: connect [database-file]");
        if (_databaseConnected)
        {
            ActiveContext = ConsoleContext.Database;
            return $"already connected to {Path.GetFileName(_connectedDatabasePath)} (read-only); database context is active. Use disconnect before switching databases.";
        }

        string databasePath = args.Length == 0 ? AppDbContext.DatabaseFilePath : ResolvePath(args[0]);
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("application database was not found", databasePath);

        EnsureNoReparsePoints(databasePath);
        using AppDbContext db = CreateReadOnlyDbContext(databasePath);
        if (!db.Database.CanConnect())
            throw new InvalidOperationException($"could not connect to database file '{Path.GetFileName(databasePath)}'.");
        _connectedDatabasePath = databasePath;
        _databaseSessionId = null;
        _databaseMediaDirectory = false;
        _databaseMediaId = null;
        _databaseMessageId = null;
        
        
        _databaseConnected = true;
        ActiveContext = ConsoleContext.Database;
        return $"connected to {Path.GetFileName(databasePath)} (read-only). Database context is active; use 'ls' to browse.";
    }

    private string DisconnectDatabase(string[] args)
    {
        if (args.Length > 0)
            throw new InvalidOperationException("usage: disconnect");
        if (!_databaseConnected)
        {
            ActiveContext = ConsoleContext.Application;
            return "not connected; returned to application-data context.";
        }

        _databaseConnected = false;
        ActiveContext = ConsoleContext.Application;
        _connectedDatabasePath = null;
        _databaseSessionId = null;
        _databaseMediaDirectory = false;
        _databaseMediaId = null;
        _databaseMessageId = null;
        
        
        return "disconnected from the database.";
    }

    private string HandleSessions(string[] args)
    {
        if (args.Length == 0)
            throw new InvalidOperationException("usage: sessions list [--limit N] | sessions show <id>");
        if (!_databaseConnected)
            throw new InvalidOperationException("connect to the application database first.");

        using AppDbContext db = CreateReadOnlyDbContext(ConnectedDatabasePath);

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
            .Select(message => new { message.Id, message.Role, message.Content, message.Timestamp })
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
            lines.Add($"[message {message.Id} · {message.Timestamp.ToLocalTime():HH:mm:ss}] {message.Role}: {content}");
        }

        return string.Join("\n", lines);
    }

    private string FindSessions(string[] args)
    {
        EnsureDatabaseConnected();
        if (args.Length == 0 || args[0].Equals("-h", StringComparison.OrdinalIgnoreCase) || args[0].Equals("--help", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("usage: find sessions [--sid ID] [--has-media] [--role VALUE] [--content TEXT] [--model VALUE] [--reasoning-level VALUE] [--tool VALUE] [--same-message]");
        if (!args[0].Equals("sessions", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("usage: find sessions [filters]");

        int? sessionId = null;
        bool hasMedia = false;
        bool sameMessage = false;
        string? role = null, content = null, model = null, reasoningLevel = null, tool = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            string flag = args[i];
            string canonicalFlag = flag.ToLowerInvariant() switch
            {
                "--content-contains" => "--content",
                "--model-used" => "--model",
                "--active-tool" or "--active-tools" => "--tool",
                _ => flag.ToLowerInvariant()
            };
            if (!seen.Add(canonicalFlag))
                throw new InvalidOperationException($"duplicate option: {flag}");
            switch (flag.ToLowerInvariant())
            {
                case "--same-message": sameMessage = true; break;
                case "--has-media": hasMedia = true; break;
                case "--sid":
                    sessionId = ParseFindValue(args, ref i, flag, positiveInteger: true) is string sidText && int.TryParse(sidText, out int sid) ? sid : throw new InvalidOperationException("--sid requires a positive session ID.");
                    break;
                case "--role": role = ParseFindValue(args, ref i, flag); break;
                case "--content":
                case "--content-contains": content = ParseFindValue(args, ref i, flag); break;
                case "--model":
                case "--model-used": model = ParseFindValue(args, ref i, flag); break;
                case "--reasoning-level": reasoningLevel = ParseFindValue(args, ref i, flag); break;
                case "--tool":
                case "--active-tool":
                case "--active-tools": tool = ParseFindValue(args, ref i, flag); break;
                default: throw new InvalidOperationException($"unknown find option '{flag}'. Use 'find -h' for usage.");
            }
        }

        if (!hasMedia && role is null && content is null && model is null && reasoningLevel is null && tool is null && sessionId is null)
            throw new InvalidOperationException("specify at least one filter. Use 'find -h' for usage.");

        using AppDbContext db = CreateReadOnlyDbContext(ConnectedDatabasePath);
        var messages = db.Messages.AsNoTracking()
            .Where(message => sessionId == null || message.ChatSessionId == sessionId.Value)
            .Select(message => new FindMessage(message.Id, message.ChatSessionId, message.Role, message.Content, message.ModelUsed, message.ReasoningLevel, message.ActiveTools))
            .ToList();
        var mediaCounts = db.Media.AsNoTracking()
            .GroupBy(media => media.ChatMessageId)
            .Select(group => new { MessageId = group.Key, Count = group.Count() })
            .ToDictionary(item => item.MessageId, item => item.Count);

        var filters = new List<(string Header, Func<FindMessage, bool> Matches)>();
        if (hasMedia) filters.Add(("--has-media", message => mediaCounts.ContainsKey(message.Id)));
        if (role is not null) filters.Add(("--role", message => string.Equals(message.Role, role, StringComparison.OrdinalIgnoreCase)));
        if (content is not null) filters.Add(("--content", message => message.Content?.Contains(content, StringComparison.OrdinalIgnoreCase) == true));
        if (model is not null) filters.Add(("--model", message => string.Equals(message.ModelUsed, model, StringComparison.OrdinalIgnoreCase)));
        if (reasoningLevel is not null) filters.Add(("--reasoning-level", message => string.Equals(message.ReasoningLevel, reasoningLevel, StringComparison.OrdinalIgnoreCase)));
        if (tool is not null) filters.Add(("--tool", message => ActiveToolMatches(message.ActiveTools, tool)));

        var sessions = db.Sessions.AsNoTracking()
            .Where(item => sessionId == null || item.Id == sessionId.Value)
            .Select(item => new { item.Id })
            .ToList();
        var matchingRows = new List<string[]>();
        foreach (var session in sessions.OrderBy(item => item.Id))
        {
            FindMessage[] sessionMessages = messages.Where(message => message.SessionId == session.Id).OrderBy(message => message.Id).ToArray();
            var matchesByFilter = filters.Select(filter => sessionMessages.Where(filter.Matches).ToArray()).ToArray();
            if (filters.Count > 0 && matchesByFilter.Any(matches => matches.Length == 0))
                continue;

            FindMessage[] sameMessageMatches = [];
            if (sameMessage && filters.Count > 0)
            {
                sameMessageMatches = sessionMessages.Where(message => filters.All(filter => filter.Matches(message))).ToArray();
                if (sameMessageMatches.Length == 0)
                    continue;
            }

            if (filters.Count == 0)
            {
                matchingRows.Add(new[] { session.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            else
            {
                FindMessage[] evidenceMessages = sameMessage
                    ? sameMessageMatches
                    : matchesByFilter.SelectMany(matches => matches).DistinctBy(message => message.Id).OrderBy(message => message.Id).ToArray();
                foreach (FindMessage message in evidenceMessages)
                {
                    var row = new List<string> { session.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                    for (int filterIndex = 0; filterIndex < filters.Count; filterIndex++)
                    {
                        bool isMatch = sameMessage || filters[filterIndex].Matches(message);
                        row.Add(isMatch ? message.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
                    }
                    matchingRows.Add(row.ToArray());
                    if (matchingRows.Count >= MaximumListItems)
                        break;
                }
            }
            if (matchingRows.Count >= MaximumListItems)
                break;
        }

        if (matchingRows.Count == 0)
            return "no matching sessions found.";

        var headers = new List<string> { "SessionId" };
        headers.AddRange(filters.Select(filter => filter.Header));
        return FormatFindResults(headers.ToArray(), matchingRows);
    }

    private static string ParseFindValue(string[] args, ref int index, string option, bool positiveInteger = false)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new InvalidOperationException($"{option} requires a value.");
        string value = args[++index];
        if (positiveInteger && (!int.TryParse(value, out int id) || id <= 0))
            throw new InvalidOperationException("--sid requires a positive session ID.");
        return value;
    }

    private static string FormatFindResults(string[] headers, List<string[]> rows)
    {
        int[] widths = headers.Select((header, index) => Math.Max(header.Length, rows.Max(row => row[index].Length))).ToArray();
        var output = new List<string> { string.Join("  ", headers.Select((header, index) => header.PadRight(widths[index]))).TrimEnd() };
        output.Add(string.Join("  ", widths.Select(width => new string('-', width))));
        output.AddRange(rows.Select(row => string.Join("  ", row.Select((value, index) => value.PadRight(widths[index]))).TrimEnd()));
        if (rows.Count >= MaximumListItems)
            output.Add($"... results limited to {MaximumListItems} rows");
        return string.Join("\n", output);
    }

    private static IEnumerable<string> ParseActiveToolNames(string? activeTools)
    {
        if (string.IsNullOrWhiteSpace(activeTools)) return [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(activeTools);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                return document.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray();
        }
        catch (JsonException) { }
        return activeTools.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim().Trim('"').Trim('[', ']')).Where(item => item.Length > 0).ToArray();
    }

    private static bool ActiveToolMatches(string? activeTools, string requestedTool) =>
        ParseActiveToolNames(activeTools).Any(item => string.Equals(item, requestedTool, StringComparison.OrdinalIgnoreCase));

    private sealed record FindMessage(int Id, int SessionId, string Role, string Content, string ModelUsed, string ReasoningLevel, string ActiveTools);

    private string HandleTables(string[] args)
    {
        if (args.Length != 1)
            throw new InvalidOperationException("usage: tables");
        EnsureDatabaseConnected();

        using SqliteConnection connection = OpenReadOnlyDatabase();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name COLLATE NOCASE;";
        using SqliteDataReader reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        return tables.Count == 0 ? "no tables found." : string.Join("\n", tables);
    }

    private string HandleDescribe(string[] args)
    {
        if (args.Length != 1)
            throw new InvalidOperationException("usage: describe <table>");
        EnsureDatabaseConnected();

        using SqliteConnection connection = OpenReadOnlyDatabase();
        string tableName = FindTableName(connection, args[0]);
        List<DatabaseColumn> columns = ReadTableColumns(connection, tableName);
        if (columns.Count == 0)
            return $"table '{tableName}' has no columns.";

        var lines = new List<string>
        {
            $"{tableName} columns:",
            "Name  Type  NotNull  Default  PrimaryKey"
        };
        lines.AddRange(columns.Select(column =>
            $"{column.Name}  {column.Type}  {(column.NotNull ? "yes" : "no")}  {column.DefaultValue ?? "(none)"}  {(column.PrimaryKey ? "yes" : "no")}"));
        return string.Join("\n", lines);
    }

    private string HandleMessages(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[0], "show", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(args[1], out int id) || id <= 0 ||
            (args.Length != 2 && (args.Length != 4 || args[2] != "--field" || string.IsNullOrWhiteSpace(args[3]))))
        {
            throw new InvalidOperationException("usage: messages show <id> [--field <column>]");
        }
        EnsureDatabaseConnected();

        using SqliteConnection connection = OpenReadOnlyDatabase();
        string tableName = FindTableName(connection, "Messages");
        List<DatabaseColumn> columns = ReadTableColumns(connection, tableName);
        if (!columns.Any(column => string.Equals(column.Name, "Id", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Messages table has no Id column.");

        string? selectedField = args.Length == 4 ? args[3] : null;
        if (selectedField is not null && !columns.Any(column => string.Equals(column.Name, selectedField, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"unknown Messages column '{selectedField}'. Use 'describe Messages' to see available columns.");

        string query = selectedField is null
            ? $"SELECT * FROM {QuoteIdentifier(tableName)} WHERE {QuoteIdentifier(columns.First(column => string.Equals(column.Name, "Id", StringComparison.OrdinalIgnoreCase)).Name)} = $id LIMIT 1;"
            : $"SELECT {QuoteIdentifier(columns.First(column => string.Equals(column.Name, selectedField, StringComparison.OrdinalIgnoreCase)).Name)} FROM {QuoteIdentifier(tableName)} WHERE {QuoteIdentifier(columns.First(column => string.Equals(column.Name, "Id", StringComparison.OrdinalIgnoreCase)).Name)} = $id LIMIT 1;";
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = query;
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return $"message {id} not found.";

        if (selectedField is not null)
        {
            string actualName = reader.GetName(0);
            string value = FormatDatabaseValue(actualName, reader.GetValue(0));
            if (value.Length > MaximumDatabaseOutputCharacters)
                return $"{actualName} is {value.Length:N0} characters; it exceeds the console display limit of {MaximumDatabaseOutputCharacters:N0} characters.";
            return $"Message {id} · {actualName}\n{value}";
        }

        var output = new StringBuilder($"Message {id}\n");
        for (int i = 0; i < reader.FieldCount; i++)
        {
            string name = reader.GetName(i);
            string value = FormatDatabaseValue(name, reader.GetValue(i));
            string entry = $"\n{name}:\n{value}\n";
            if (output.Length + entry.Length > MaximumDatabaseOutputCharacters)
            {
                output.Append($"\nOutput reached the {MaximumDatabaseOutputCharacters:N0}-character display limit. Use 'messages show {id} --field {name}' to inspect this column individually.");
                break;
            }
            output.Append(entry);
        }
        return output.ToString().TrimEnd();
    }

    private string HandleDatabaseBrowser(string[] args)
    {
        EnsureDatabaseConnected();
        if (args.Length == 0)
            throw new InvalidOperationException("usage: ls [--columns 1,3] | cd <id|..|/> | pwd | show [--column column] | columns");

        return args[0].ToLowerInvariant() switch
        {
            "ls" => ListDatabaseLocation(args.Skip(1).ToArray()),
            "cd" => ChangeDatabaseLocation(args.Skip(1).ToArray()),
            "pwd" when args.Length == 1 => GetDatabaseLocation(),
            "show" => ShowCurrentDatabaseMessage(args.Skip(1).ToArray()),
            "columns" when args.Length == 1 => ListCurrentMessageColumns(),
            _ => throw new InvalidOperationException("usage: ls [--columns 1,3] | cd <id|..|/> | pwd | show [--column column] | columns")
        };
    }

    private string ListDatabaseLocation(string[] args)
    {
        List<int>? selectedColumns = ParseColumnArguments(args);
        if (_databaseMediaId is int mediaId)
            return ListMediaFields(mediaId, selectedColumns);
        if (_databaseMediaDirectory && _databaseMessageId is int parentMessageId)
            return ListMessageMedia(parentMessageId, selectedColumns);
        using AppDbContext db = CreateReadOnlyDbContext(ConnectedDatabasePath);
        if (_databaseMessageId is int messageId)
        {
            return ListMessageFields(messageId, selectedColumns);
        }

        if (_databaseSessionId is int sessionId)
            return ListSessionMessages(sessionId, selectedColumns);
        return ListDatabaseSessions(db, selectedColumns);
    }

    private string ChangeDatabaseLocation(string[] args)
    {
        if (args.Length != 1)
            throw new InvalidOperationException("usage: cd <session-id|message-id|media|..|/>");
        string target = args[0];
        if (target is "/" or "~")
        {
            _databaseSessionId = null;
        _databaseMediaDirectory = false;
        _databaseMediaId = null;
            _databaseMessageId = null;
        
        
            return string.Empty;
        }
        if (target == ".." && _databaseMediaId.HasValue)
        {
            _databaseMediaId = null;
            return string.Empty;
        }
        if (target == ".." && _databaseMediaDirectory)
        {
            _databaseMediaDirectory = false;
            return string.Empty;
        }
        if (target == "..")
        {
            if (_databaseMediaId.HasValue) _databaseMediaId = null;
                
            else if (_databaseMediaDirectory) _databaseMediaDirectory = false;
                
            else if (_databaseMessageId.HasValue)
                _databaseMessageId = null;
        
        
            else if (_databaseSessionId.HasValue)
                _databaseSessionId = null;
        _databaseMediaDirectory = false;
        _databaseMediaId = null;
            return string.Empty;
        }
        if (_databaseMessageId.HasValue && !_databaseMediaDirectory && target.Equals("media", StringComparison.OrdinalIgnoreCase))
        {
            _databaseMediaDirectory = true;
            return string.Empty;
        }
        if (!int.TryParse(target, out int id) || id <= 0)
            throw new InvalidOperationException("database navigation expects a numeric session or message id.");

        using SqliteConnection connection = OpenReadOnlyDatabase();
        if (_databaseMediaDirectory && _databaseMessageId is int parentMessageId)
        {
            string mediaTable = FindTableName(connection, "Media");
            List<DatabaseColumn> mediaColumns = ReadTableColumns(connection, mediaTable);
            string mediaIdColumn = FindColumn(mediaColumns, "Id").Name;
            string mediaMessageColumn = FindColumn(mediaColumns, "ChatMessageId").Name;
            using SqliteCommand mediaCommand = connection.CreateCommand();
            mediaCommand.CommandText = $"SELECT 1 FROM {QuoteIdentifier(mediaTable)} WHERE {QuoteIdentifier(mediaIdColumn)} = $id AND {QuoteIdentifier(mediaMessageColumn)} = $messageId LIMIT 1;";
            mediaCommand.Parameters.AddWithValue("$id", id);
            mediaCommand.Parameters.AddWithValue("$messageId", parentMessageId);
            if (mediaCommand.ExecuteScalar() is null)
                throw new DirectoryNotFoundException($"media {id} not found for message {parentMessageId}.");
            _databaseMediaId = id;
            return string.Empty;
        }

        if (_databaseSessionId is null)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM Sessions WHERE Id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteScalar() is null)
                throw new DirectoryNotFoundException($"session {id} not found.");
            _databaseSessionId = id;
            return string.Empty;
        }

        string messagesTable = FindTableName(connection, "Messages");
        List<DatabaseColumn> columns = ReadTableColumns(connection, messagesTable);
        string idColumn = FindColumn(columns, "Id").Name;
        string sessionColumn = FindColumn(columns, "ChatSessionId").Name;
        using SqliteCommand messageCommand = connection.CreateCommand();
        messageCommand.CommandText = $"SELECT 1 FROM {QuoteIdentifier(messagesTable)} WHERE {QuoteIdentifier(idColumn)} = $id AND {QuoteIdentifier(sessionColumn)} = $sessionId LIMIT 1;";
        messageCommand.Parameters.AddWithValue("$id", id);
        messageCommand.Parameters.AddWithValue("$sessionId", _databaseSessionId.Value);
        if (messageCommand.ExecuteScalar() is null)
            throw new DirectoryNotFoundException($"message {id} not found in session {_databaseSessionId}.");
        _databaseMessageId = id;
        return string.Empty;
    }

    private string ListDatabaseSessions(AppDbContext db, List<int>? selectedColumns)
    {
        var sessions = db.Sessions.AsNoTracking()
            .OrderByDescending(session => session.LastUsedAt)
            .Take(MaximumListItems)
            .Select(session => new { session.Id, session.LastUsedAt, MessageCount = session.Messages.Count, session.Title })
            .ToList();
        var rows = sessions.Select(session => new[]
        {
            session.Id.ToString(),
            session.LastUsedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            session.MessageCount.ToString(),
            OneLine(session.Title, 80)
        }).ToList();
        return FormatBrowserTable(new[] { "Id", "LastUsedAt", "MessageCount", "Title" }, rows, selectedColumns);
    }

    private string ListSessionMessages(int sessionId, List<int>? selectedColumns)
    {
        using SqliteConnection connection = OpenReadOnlyDatabase();
        string table = FindTableName(connection, "Messages");
        List<DatabaseColumn> columns = ReadTableColumns(connection, table);
        string idColumn = FindColumn(columns, "Id").Name;
        string sessionColumn = FindColumn(columns, "ChatSessionId").Name;
        string timeColumn = columns.FirstOrDefault(column => string.Equals(column.Name, "Timestamp", StringComparison.OrdinalIgnoreCase))?.Name ?? idColumn;
        string[] headers = columns.Select(column => column.Name).Append("MediaIds").ToArray();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(table)} WHERE {QuoteIdentifier(sessionColumn)} = $sessionId ORDER BY {QuoteIdentifier(timeColumn)} DESC LIMIT {MaximumListItems};";
        command.Parameters.AddWithValue("$sessionId", sessionId);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<string[]>();
        while (reader.Read())
        {
            var row = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
                row[i] = OneLine(FormatDatabaseValue(reader.GetName(i), reader.GetValue(i)), 90);
            rows.Add(row);
        }
        reader.Close();
        int messageIdIndex = Array.FindIndex(headers, header => string.Equals(header, "Id", StringComparison.OrdinalIgnoreCase));
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            int rowMessageId = int.Parse(rows[rowIndex][messageIdIndex], System.Globalization.CultureInfo.InvariantCulture);
            rows[rowIndex] = rows[rowIndex].Append(ListMediaIds(connection, rowMessageId)).ToArray();
        }
        if (selectedColumns is null)
        {
            selectedColumns = new List<int>();
            foreach (string preferred in new[] { "Id", "Role", "Timestamp", "Content", "MediaIds" })
            {
                int index = Array.FindIndex(headers, header => string.Equals(header, preferred, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    selectedColumns.Add(index + 1);
            }
            if (selectedColumns.Count == 0)
                selectedColumns.Add(1);
        }
        return FormatBrowserTable(headers, rows, selectedColumns);
    }

    private string ListMessageMedia(int messageId, List<int>? selectedColumns)
    {
        using SqliteConnection connection = OpenReadOnlyDatabase();
        string table = FindTableName(connection, "Media");
        List<DatabaseColumn> columns = ReadTableColumns(connection, table);
        string messageColumn = FindColumn(columns, "ChatMessageId").Name;
        string idColumn = FindColumn(columns, "Id").Name;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(table)} WHERE {QuoteIdentifier(messageColumn)} = $messageId ORDER BY {QuoteIdentifier(idColumn)};";
        command.Parameters.AddWithValue("$messageId", messageId);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<string[]>();
        while (reader.Read())
        {
            var row = new string[reader.FieldCount];
            for (int index = 0; index < reader.FieldCount; index++)
                row[index] = OneLine(FormatDatabaseValue(reader.GetName(index), reader.GetValue(index)), 100);
            rows.Add(row);
        }
        return FormatBrowserTable(columns.Select(column => column.Name).ToArray(), rows, selectedColumns);
    }

    private string ListMediaFields(int mediaId, List<int>? selectedColumns)
    {
        using SqliteConnection connection = OpenReadOnlyDatabase();
        string table = FindTableName(connection, "Media");
        List<DatabaseColumn> columns = ReadTableColumns(connection, table);
        string idColumn = FindColumn(columns, "Id").Name;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(table)} WHERE {QuoteIdentifier(idColumn)} = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", mediaId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return $"media {mediaId} not found.";
        if (selectedColumns?.Any(index => index > reader.FieldCount) == true)
            throw new InvalidOperationException($"column number out of range; this media record has {reader.FieldCount} fields.");
        var lines = new List<string> { "#  Column                 Value" };
        for (int index = 0; index < reader.FieldCount; index++)
        {
            string name = reader.GetName(index);
            if (selectedColumns is null || selectedColumns.Contains(index + 1))
                lines.Add($"{index + 1,2} {name,-22} {OneLine(FormatDatabaseValue(name, reader.GetValue(index)), 100)}");
        }
        return string.Join("\n", lines);
    }

    private static string ListMediaIds(SqliteConnection connection, int messageId)
    {
        try
        {
            string table = FindTableName(connection, "Media");
            List<DatabaseColumn> columns = ReadTableColumns(connection, table);
            string idColumn = FindColumn(columns, "Id").Name;
            string messageColumn = FindColumn(columns, "ChatMessageId").Name;
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT {QuoteIdentifier(idColumn)} FROM {QuoteIdentifier(table)} WHERE {QuoteIdentifier(messageColumn)} = $messageId ORDER BY {QuoteIdentifier(idColumn)};";
            command.Parameters.AddWithValue("$messageId", messageId);
            var ids = new List<string>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                ids.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            return ids.Count == 0 ? "—" : string.Join(",", ids);
        }
        catch (InvalidOperationException)
        {
            return "—";
        }
    }

    private string ListMessageFields(int messageId, List<int>? selectedColumns)
    {
        using SqliteConnection connection = OpenReadOnlyDatabase();
        string table = FindTableName(connection, "Messages");
        List<DatabaseColumn> columns = ReadTableColumns(connection, table);
        string idColumn = FindColumn(columns, "Id").Name;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(table)} WHERE {QuoteIdentifier(idColumn)} = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", messageId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return $"message {messageId} not found.";
        if (selectedColumns?.Any(index => index > reader.FieldCount) == true)
            throw new InvalidOperationException($"column number out of range; this message has {reader.FieldCount} fields.");
        var lines = new List<string> { "#  Column                 Value" };
        for (int i = 0; i < reader.FieldCount; i++)
        {
            string name = reader.GetName(i);
            if (selectedColumns is null || selectedColumns.Contains(i + 1))
                lines.Add($"{i + 1,2} {name,-22} {OneLine(FormatDatabaseValue(name, reader.GetValue(i)), 100)}");
        }
        reader.Close();
        string mediaIds = ListMediaIds(connection, messageId);
        lines.Add(string.Empty);
        if (mediaIds == "—")
        {
            lines.Add("Related media: none");
        }
        else
        {
            int mediaCount = mediaIds.Split(',').Length;
            lines.Add($"Related media ({mediaCount}) · Media.ChatMessageId → Messages.Id: {mediaIds}");
            lines.Add("Use 'cd media' to browse these records.");
        }
        return string.Join("\n", lines);
    }

    private string ShowCurrentDatabaseMessage(string[] args)
    {
        if (_databaseMediaId is int mediaId)
            return ShowCurrentMedia(mediaId, args);
        return ShowCurrentDatabaseMessageCore(args);
    }

    private string ShowCurrentMedia(int mediaId, string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] is not ("--field" or "--column")))
            throw new InvalidOperationException("usage: show [--column <field>]");
        using SqliteConnection connection = OpenReadOnlyDatabase();
        string table = FindTableName(connection, "Media");
        List<DatabaseColumn> columns = ReadTableColumns(connection, table);
        string idColumn = FindColumn(columns, "Id").Name;
        string? selectedField = args.Length == 2 ? args[1] : null;
        if (selectedField is not null && !columns.Any(column => string.Equals(column.Name, selectedField, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"unknown Media column '{selectedField}'.");
        string selection = selectedField is null ? "*" : QuoteIdentifier(columns.First(column => string.Equals(column.Name, selectedField, StringComparison.OrdinalIgnoreCase)).Name);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {selection} FROM {QuoteIdentifier(table)} WHERE {QuoteIdentifier(idColumn)} = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", mediaId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return $"media {mediaId} not found.";
        if (selectedField is not null)
            return $"Media {mediaId} · {reader.GetName(0)}\n{FormatDatabaseValue(reader.GetName(0), reader.GetValue(0))}";
        var output = new StringBuilder($"Media {mediaId}\n");
        for (int index = 0; index < reader.FieldCount; index++)
            output.Append($"\n{reader.GetName(index)}:\n{FormatDatabaseValue(reader.GetName(index), reader.GetValue(index))}\n");
        return output.ToString().TrimEnd();
    }

    private string ShowCurrentDatabaseMessageCore(string[] args)
    {
        if (_databaseMessageId is not int messageId)
            throw new InvalidOperationException("navigate to a message with 'cd <message-id>' first.");
        if (args.Length == 0)
            return HandleMessages(["show", messageId.ToString()]);
        if (args.Length == 2 && args[0] is "--field" or "--column")
            return HandleMessages(["show", messageId.ToString(), "--field", args[1]]);
        throw new InvalidOperationException("usage: show [--column column]");
    }

    private string ListCurrentMessageColumns()
    {
        if (_databaseMediaId.HasValue || _databaseMediaDirectory)
        {
            using SqliteConnection mediaConnection = OpenReadOnlyDatabase();
            string mediaTable = FindTableName(mediaConnection, "Media");
            List<DatabaseColumn> mediaColumns = ReadTableColumns(mediaConnection, mediaTable);
            return string.Join("\n", mediaColumns.Select((column, index) => $"{index + 1,2}  {column.Name}"));
        }
        if (_databaseMessageId is not int messageId)
            throw new InvalidOperationException("navigate to a message with 'cd <message-id>' first.");
        using SqliteConnection connection = OpenReadOnlyDatabase();
        string table = FindTableName(connection, "Messages");
        List<DatabaseColumn> columns = ReadTableColumns(connection, table);
        return string.Join("\n", columns.Select((column, index) => $"{index + 1,2}  {column.Name}"));
    }

    private static List<int>? ParseColumnArguments(string[] args)
    {
        if (args.Length == 0)
            return null;
        if (args.Length != 2 || args[0] != "--columns")
            throw new InvalidOperationException("usage: ls [--columns 1,3,4]");
        var result = new List<int>();
        foreach (string part in args[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out int index) || index < 1 || result.Contains(index))
                throw new InvalidOperationException("column selection must contain unique, comma-separated 1-based numbers (use columns for message fields).");
            result.Add(index);
        }
        if (result.Count == 0)
            throw new InvalidOperationException("provide at least one column number.");
        return result;
    }

    private static string FormatBrowserTable(string[] headers, List<string[]> rows, List<int>? selectedColumns)
    {
        if (rows.Count == 0)
            return "(empty)";
        int[] indexes = selectedColumns?.ToArray() ?? Enumerable.Range(1, headers.Length).ToArray();
        if (indexes.Any(index => index > headers.Length))
            throw new InvalidOperationException($"column number out of range; this listing has {headers.Length} columns.");
        var output = new List<string> { "#  " + string.Join("  ", indexes.Select(index => $"{index}:{headers[index - 1]}")) };
        for (int row = 0; row < rows.Count; row++)
            output.Add($"{row + 1,2} " + string.Join("  ", indexes.Select(index => rows[row][index - 1])));
        if (rows.Count >= MaximumListItems)
            output.Add($"... listing limited to {MaximumListItems} rows");
        return string.Join("\n", output);
    }

    private static DatabaseColumn FindColumn(List<DatabaseColumn> columns, string name) =>
        columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Messages table is missing required column '{name}'.");

    private string GetDatabaseLocation()
    {
        if (_databaseMessageId is int messageId)
        {
            if (_databaseMediaId is int mediaId)
                return $"/sessions/{_databaseSessionId}/messages/{messageId}/media/{mediaId}";
            if (_databaseMediaDirectory)
                return $"/sessions/{_databaseSessionId}/messages/{messageId}/media";
            return $"/sessions/{_databaseSessionId}/messages/{messageId}";
        }
        return _databaseSessionId is int sessionId ? $"/sessions/{sessionId}" : "/";
    }

    private string ConnectedDatabasePath => _connectedDatabasePath ?? throw new InvalidOperationException("connect to a database first.");

    private void EnsureDatabaseConnected()
    {
        if (!_databaseConnected)
            throw new InvalidOperationException("connect to the application database first.");
    }

    private SqliteConnection OpenReadOnlyDatabase()
    {
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ConnectedDatabasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static string FindTableName(SqliteConnection connection, string requestedName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name COLLATE NOCASE;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(0);
            if (string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        throw new InvalidOperationException($"unknown table '{requestedName}'. Use 'tables' to see available tables.");
    }

    private static List<DatabaseColumn> ReadTableColumns(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
        using SqliteDataReader reader = command.ExecuteReader();
        var columns = new List<DatabaseColumn>();
        while (reader.Read())
        {
            columns.Add(new DatabaseColumn(
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4)),
                reader.GetInt32(5) != 0));
        }
        return columns;
    }

    private static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string FormatDatabaseValue(string columnName, object value)
    {
        if (value is DBNull)
            return "(NULL)";
        if (value is byte[] bytes)
            return $"0x{Convert.ToHexString(bytes)}";

        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(text) && (columnName.EndsWith("Json", StringComparison.OrdinalIgnoreCase) ||
            text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')))
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(text);
                text = JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (JsonException)
            {
                // Preserve malformed or non-JSON values exactly as stored.
            }
        }
        return MakeControlCharactersVisible(text);
    }

    private static string MakeControlCharactersVisible(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character == '\n' || character == '\r' || character == '\t' || !char.IsControl(character))
                output.Append(character);
            else
                output.Append($"\\\\u{(int)character:x4}");
        }
        return output.ToString();
    }

    private sealed record DatabaseColumn(string Name, string Type, bool NotNull, string? DefaultValue, bool PrimaryKey);

    public AppConsoleCompletionResult GetPathCompletions(string commandLine)
    {
        int tokenStart = FindCurrentTokenStart(commandLine);
        string rawToken = commandLine[tokenStart..];
        string prefix = commandLine[..tokenStart];

        List<string> precedingArgs;
        try
        {
            precedingArgs = Tokenize(prefix);
        }
        catch
        {
            return new AppConsoleCompletionResult(tokenStart, []);
        }

        if (precedingArgs.Count == 0)
        {
            string typedCommand = DecodeCompletionToken(rawToken);
            string[] commands = ActiveContext switch
            {
                ConsoleContext.Database => ["help", "pwd", "ls", "cd", "grep", "disconnect", "sessions", "find", "tables", "describe", "messages", "show", "columns", "db", "workspace", "app", "clear", "quit", "exit"],
                ConsoleContext.Workspace => ["help", "pwd", "ls", "cd", "cat", "grep", "workspace", "app", "database", "disconnect", "clear", "quit", "exit"],
                _ => ["help", "pwd", "ls", "cd", "cat", "grep", "connect", "disconnect", "workspace", "database", "clear", "quit", "exit"]
            };
            return new AppConsoleCompletionResult(tokenStart, commands.Where(item => item.StartsWith(typedCommand, StringComparison.OrdinalIgnoreCase)).ToArray());
        }

        string command = precedingArgs[0].ToLowerInvariant();
        if (command == "find")
        {
            string typed = DecodeCompletionToken(rawToken);
            string[] findArgs = precedingArgs.Skip(1).ToArray();
            if (findArgs.Length == 0)
                return new AppConsoleCompletionResult(tokenStart, new[] { "sessions" }.Where(item => item.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToArray());
            if (!findArgs[0].Equals("sessions", StringComparison.OrdinalIgnoreCase))
                return new AppConsoleCompletionResult(tokenStart, []);

            string? valueOption = findArgs.Length > 1 ? findArgs[^1].ToLowerInvariant() : null;
            string[] valueOptions = ["--sid", "--role", "--content", "--content-contains", "--model", "--model-used", "--reasoning-level", "--tool", "--active-tool", "--active-tools"];
            if (valueOption is not null && valueOptions.Contains(valueOption, StringComparer.OrdinalIgnoreCase))
            {
                if (ActiveContext != ConsoleContext.Database || valueOption is "--content" or "--content-contains")
                    return new AppConsoleCompletionResult(tokenStart, []);
                try
                {
                    using AppDbContext db = CreateReadOnlyDbContext(ConnectedDatabasePath);
                    IEnumerable<string> values = valueOption == "--sid"
                        ? db.Sessions.AsNoTracking().Select(item => item.Id.ToString()).ToList()
                        : valueOption == "--role"
                            ? db.Messages.AsNoTracking().Select(item => item.Role).Distinct().ToList()
                            : valueOption is "--model" or "--model-used"
                                ? db.Messages.AsNoTracking().Select(item => item.ModelUsed).Distinct().ToList()
                                : valueOption == "--reasoning-level"
                                    ? db.Messages.AsNoTracking().Select(item => item.ReasoningLevel).Distinct().ToList()
                                    : db.Messages.AsNoTracking().Select(item => item.ActiveTools).Distinct().ToList()
                                        .SelectMany(ParseActiveToolNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    return new AppConsoleCompletionResult(tokenStart, values.Where(value => !string.IsNullOrWhiteSpace(value) && value.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(50).ToArray());
                }
                catch { return new AppConsoleCompletionResult(tokenStart, []); }
            }

            string[] findFlags = ["--sid", "--has-media", "--role", "--content", "--model", "--reasoning-level", "--tool", "--same-message"];
            return new AppConsoleCompletionResult(tokenStart, findFlags.Where(item => item.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToArray());
        }
        if (command == "db" || (ActiveContext == ConsoleContext.Database && command == "cd"))
        {
            string typed = DecodeCompletionToken(rawToken);
            if (command == "db" && precedingArgs.Count == 1)
            {
                string[] subcommands = ["help", "ls", "cd", "pwd", "show", "columns"];
                return new AppConsoleCompletionResult(tokenStart, subcommands.Where(item => item.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToArray());
            }
            if (ActiveContext == ConsoleContext.Database && ((command == "db" && precedingArgs.Count == 2 && precedingArgs[1].Equals("cd", StringComparison.OrdinalIgnoreCase)) || (command == "cd" && precedingArgs.Count == 1)))
            {
                string typedId = DecodeCompletionToken(rawToken);
                if (_databaseMessageId.HasValue && !_databaseMediaDirectory)
                    return new AppConsoleCompletionResult(tokenStart, new[] { "..", "media" }.Where(item => item.StartsWith(typedId, StringComparison.OrdinalIgnoreCase)).ToArray());
                if (_databaseMediaDirectory && _databaseMessageId is int parentMessageId)
                {
                    using SqliteConnection mediaConnection = OpenReadOnlyDatabase();
                    string mediaTable = FindTableName(mediaConnection, "Media");
                    List<DatabaseColumn> mediaColumns = ReadTableColumns(mediaConnection, mediaTable);
                    using SqliteCommand mediaIdsCommand = mediaConnection.CreateCommand();
                    mediaIdsCommand.CommandText = $"SELECT {QuoteIdentifier(FindColumn(mediaColumns, "Id").Name)} FROM {QuoteIdentifier(mediaTable)} WHERE {QuoteIdentifier(FindColumn(mediaColumns, "ChatMessageId").Name)} = $messageId ORDER BY {QuoteIdentifier(FindColumn(mediaColumns, "Id").Name)} DESC LIMIT 100;";
                    mediaIdsCommand.Parameters.AddWithValue("$messageId", parentMessageId);
                    var mediaCandidates = new List<string> { ".." };
                    using SqliteDataReader mediaReader = mediaIdsCommand.ExecuteReader();
                    while (mediaReader.Read())
                    {
                        string mediaId = Convert.ToString(mediaReader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                        if (mediaId.StartsWith(typedId, StringComparison.Ordinal)) mediaCandidates.Add(mediaId);
                    }
                    return new AppConsoleCompletionResult(tokenStart, mediaCandidates);
                }
                try
                {
                    using SqliteConnection connection = OpenReadOnlyDatabase();
                    using SqliteCommand commandForIds = connection.CreateCommand();
                    if (_databaseSessionId is int sessionId)
                    {
                        string table = FindTableName(connection, "Messages");
                        List<DatabaseColumn> columns = ReadTableColumns(connection, table);
                        commandForIds.CommandText = $"SELECT {QuoteIdentifier(FindColumn(columns, "Id").Name)} FROM {QuoteIdentifier(table)} WHERE {QuoteIdentifier(FindColumn(columns, "ChatSessionId").Name)} = $sessionId ORDER BY {QuoteIdentifier(FindColumn(columns, "Id").Name)} DESC LIMIT 100;";
                        commandForIds.Parameters.AddWithValue("$sessionId", sessionId);
                    }
                    else
                    {
                        commandForIds.CommandText = "SELECT Id FROM Sessions ORDER BY LastUsedAt DESC LIMIT 100;";
                    }
                    using SqliteDataReader reader = commandForIds.ExecuteReader();
                    var databaseCandidates = new List<string> { "..", "/" };
                    while (reader.Read())
                    {
                        string id = Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                        if (id.StartsWith(typed, StringComparison.Ordinal)) databaseCandidates.Add(id);
                    }
                    return new AppConsoleCompletionResult(tokenStart, databaseCandidates);
                }
                catch { return new AppConsoleCompletionResult(tokenStart, ["..", "/"]); }
            }
            return new AppConsoleCompletionResult(tokenStart, []);
        }
        if (ActiveContext == ConsoleContext.Database && command != "cd")
            return new AppConsoleCompletionResult(tokenStart, []);
        if (command is not ("cd" or "cat" or "ls" or "connect"))
            return new AppConsoleCompletionResult(tokenStart, []);

        string typedPath = DecodeCompletionToken(rawToken);
        if (command == "ls" && typedPath.StartsWith("-", StringComparison.Ordinal) && typedPath.IndexOfAny(['/', (char)92]) < 0)
            return new AppConsoleCompletionResult(tokenStart, []);

        int separatorIndex = Math.Max(typedPath.LastIndexOf('/'), typedPath.LastIndexOf((char)92));
        string directoryPart = separatorIndex < 0 ? string.Empty : typedPath[..(separatorIndex + 1)];
        string namePrefix = separatorIndex < 0 ? typedPath : typedPath[(separatorIndex + 1)..];
        string directory;

        try
        {
            bool workspace = ActiveContext == ConsoleContext.Workspace;
            directory = string.IsNullOrEmpty(directoryPart) ? CurrentFilesystemDirectory : ResolvePath(directoryPart, workspace);
        }
        catch
        {
            return new AppConsoleCompletionResult(tokenStart, []);
        }

        if (!Directory.Exists(directory))
            return new AppConsoleCompletionResult(tokenStart, []);

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var candidates = new List<string>();
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory)
                         .OrderBy(path => !Directory.Exists(path))
                         .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(entry);
                if (!name.StartsWith(namePrefix, comparison) || IsReparsePoint(entry))
                    continue;
                if ((name.StartsWith(".", StringComparison.Ordinal) || IsHidden(entry)) && !typedPath.Contains("/.", StringComparison.Ordinal) && !typedPath.Contains("\\\\.", StringComparison.Ordinal))
                    continue;

                string separator = typedPath.Contains((char)92) && !typedPath.Contains('/') ? "\\\\" : "/";
                string completedPath = directoryPart + name + (Directory.Exists(entry) ? separator : string.Empty);
                candidates.Add(EscapeCompletionPath(completedPath));
                if (candidates.Count >= MaximumListItems)
                    break;
            }
        }
        catch
        {
            return new AppConsoleCompletionResult(tokenStart, []);
        }

        return new AppConsoleCompletionResult(tokenStart, candidates);
    }

    private static int FindCurrentTokenStart(string line)
    {
        bool escaped = false;
        char quote = (char)0;
        int start = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (character == (char)92 && quote != (char)39)
            {
                escaped = true;
                continue;
            }
            if (quote != (char)0)
            {
                if (character == quote)
                    quote = (char)0;
                continue;
            }
            if (character is (char)39 or '\"')
            {
                quote = character;
                continue;
            }
            if (char.IsWhiteSpace(character))
                start = i + 1;
        }
        return start;
    }

    private static string DecodeCompletionToken(string token)
    {
        token = token.TrimStart((char)39, '\"');
        return token.Replace("\\\\ ", " ", StringComparison.Ordinal);
    }

    private static string EscapeCompletionPath(string path) => path.Replace(" ", "\\\\ ", StringComparison.Ordinal);

    private string ResolvePath(string userPath, bool workspace = false)
    {
        if (string.IsNullOrWhiteSpace(userPath))
            throw new InvalidOperationException("path cannot be empty.");

        string root = workspace ? CurrentFilesystemRoot : _rootDirectory;
        string currentDirectory = workspace ? CurrentFilesystemDirectory : _currentDirectory;
        string normalized = userPath.Replace('/', Path.DirectorySeparatorChar);
        string candidate;
        if (userPath == "~")
        {
            candidate = root;
        }
        else if (userPath.StartsWith("~/", StringComparison.Ordinal) || userPath.StartsWith("~\\", StringComparison.Ordinal))
        {
            candidate = Path.Combine(root, normalized[2..]);
        }
        else if (userPath.StartsWith("/", StringComparison.Ordinal))
        {
            candidate = Path.Combine(root, normalized.TrimStart(Path.DirectorySeparatorChar));
        }
        else if (Path.IsPathRooted(normalized))
        {
            throw new InvalidOperationException("absolute host paths are not available; use a path relative to the current context root.");
        }
        else
        {
            candidate = Path.Combine(currentDirectory, normalized);
        }

        string fullPath = Path.GetFullPath(candidate);
        string relativePath = Path.GetRelativePath(root, fullPath);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.IsPathRooted(relativePath) || relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException(workspace ? "path is outside the configured workspace." : "path is outside the app data directory.");

        EnsureNoReparsePoints(fullPath, workspace);
        return fullPath;
    }

    private void EnsureNoReparsePoints(string path, bool workspace = false)
    {
        string root = workspace ? CurrentFilesystemRoot : _rootDirectory;
        string fullPath = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fullPath.Equals(root, comparison))
            return;

        string relative = Path.GetRelativePath(root, fullPath);
        string current = root;
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

    private string GetRelativeCurrentDirectory(bool workspace = false)
    {
        string root = workspace ? CurrentFilesystemRoot : _rootDirectory;
        string directory = workspace ? CurrentFilesystemDirectory : _currentDirectory;
        string relative = Path.GetRelativePath(root, directory);
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

    private const string FilesystemHelpText =
        "Application console — not a general-purpose or operating-system shell. Commands are implemented by this application; they do not run Linux, PowerShell, or other shell commands.\n" +
        "Context: application data. Filesystem access is confined to the app-data directory.\n\n" +
        "  help                         Show help for the current context\n" +
        "  pwd                          Show the current app-data directory\n" +
        "  ls [-a] [-l] [path]          List files; -l shows type, byte size, and modified time\n" +
        "  cd [path]                    Change directory within app data\n" +
        "  cat <file>                   Display a small text file\n" +
        "  grep <term> <file>           Search text (case-insensitive, numbered, colored)\n" +
        "  <command> | grep <term>      Filter command output\n" +
        "  connect [database-file]     Connect read-only; switches to database context\n" +
        "  workspace                   Enter the configured workspace (read-only)\n" +
        "  quit                         Close this terminal window\n\n" +
        "Use app to return here from another context. Workspace access requires Developer Tools and a configured existing root.\n" +
        "grep searches literal text; -n and -i are enabled by default, and matches are colored. It is an application command, not system grep.\n" +
        "Long output: append | more or | less (Space/Down to advance, q to quit).";

    private const string WorkspaceHelpText =
        "Application console — workspace context (read-only). This is not a general-purpose or operating-system shell.\n" +
        "Filesystem access is confined to the configured workspace root; symbolic links and reparse points are not followed.\n\n" +
        "  help                         Show workspace help\n" +
        "  pwd                          Show the current workspace directory\n" +
        "  ls [-a] [-l] [path]          List workspace files and directories\n" +
        "  cd [path]                    Change directory within the workspace\n" +
        "  cat <file>                   Display a small text file\n" +
        "  grep <term> <file>           Search workspace text\n" +
        "  <command> | grep <term>      Filter command output\n" +
        "  app                          Return to application-data context\n" +
        "  database                     Switch to the connected database context\n" +
        "  disconnect                   Disconnect the database and return to app context\n\n" +
        "Workspace commands are application-provided, not operating-system commands. File creation, modification, builds, and process execution are not available here yet.";

    private string GetDatabaseHelpText() =>
        "Application console — not a general-purpose or operating-system shell. Commands are implemented by this application; they do not run Linux, PowerShell, or other shell commands.\n" +
        $"Context: {(_databaseConnected ? $"database '{Path.GetFileName(_connectedDatabasePath)}' at {GetDatabaseLocation()}" : "database (not connected)")}. {(_databaseConnected ? "Use workspace or app to switch contexts; the database connection remains available until disconnect." : "Run connect [database-file] from app context to enter this context.")}\n\n" +
        "  help                         Show help for the current context\n" +
        "  ls [--columns N[,N...]]      List sessions, messages, fields, or media records at the current location\n" +
        "  <command> | grep <term>      Search output with numbered, case-insensitive, colored matches\n" +
        "  find sessions [filters]      Search sessions by message fields and media; see find -h\n" +
        "  cd <id|media|..|/>           Enter a session, message, or its media; move up or to root\n" +
        "  pwd                          Show the current database location\n" +
        "  show [--column <name>]       Show fields of the current message or media record\n" +
        "  columns                      List message/media fields and 1-based indexes\n" +
        "  sessions list [--limit N]    List recent sessions (1-100)\n" +
        "  sessions show <id>           Show session details and recent messages\n" +
        "  tables                       List database tables (read-only)\n" +
        "  describe <table>             Show database column details\n" +
        "  messages show <id> [--field <column>]  Show message data\n" +
        "  workspace                   Switch to the configured workspace (read-only)\n" +
        "  app                          Return to application-data context\n" +
        "  disconnect                   Disconnect and return to application-data context\n\n" +
        "At /, ls lists sessions. Inside a session, ls lists messages (including related MediaIds). Inside a message, ls lists fields; cd media enters its related Media records, if any.\n" +
        "Use ls --columns 1,4 to select 1-based columns in the current listing. Use columns for message-field indexes.\n" +
        "find sessions filters: --has-media, --role, --content, --model, --reasoning-level, --tool, and --sid. Filters combine with AND; --same-message requires them to match one message. Results show matching message IDs, sorted by session ID.\n" +
        "show at a message or media record displays all fields; --column (or --field) selects one field and pretty-prints JSON.\n" +
        "The optional 'db' prefix remains accepted (for example, db ls), but is not required.\n" +
        "Use ls -h or show --help for command help. grep searches literal text in piped application output (also in filesystem context); -n and -i are defaults and matches are colored. This is not a system shell grep. Long output: append | more or | less; Space/Down advances, q quits.";



    /* Obsolete fixed help text retained only as a reference; active help is context-sensitive above.
    private const string HelpText =
        "Available commands:\n" +
        "  help                     Show this help\n" +
        "  pwd                      Show the current app-data directory\n" +
        "  ls [-a] [path]           List files and folders\n" +
        "  cd [path]                Change directory (confined to app data)\n" +
        "  cat <file>               Display a small text file\n" +
        "  connect [file]           Connect to a database file in app data (read-only)\n" +
        "  disconnect               Disconnect from the database\n" +
        "  db help                  Explain database browsing commands and locations\n" +
        "  db ls [--columns 1,3]    Browse sessions, messages, or message fields\n" +
        "  db cd <id|..|/>          Navigate into a session/message or move up\n" +
        "  db pwd                  Show the current database location\n" +
        "  db show [--column name]  Show a message or one of its fields\n" +
        "  db columns              List message field names and 1-based indexes\n" +
        "  sessions list [--limit N] List recent sessions (1-100)\n" +
        "  sessions show <id>       Show session details and recent messages\n" +
        "  tables                   List database tables (read-only)\n" +
        "  describe <table>         Show database column details\n" +
        "  messages show <id>       Show every stored message field\n" +
        "  messages show <id> --field <column>  Show one message field\n" +
        "  <command> | more|less    Page long output (Space/Down; q to quit)\n" +
        "  clear                    Clear the terminal display\n" +
        "  quit                     End the console session and close the window\n" +
        "\nThis is an app console, not an operating-system shell. File access is limited to the app data directory.";
    */
}

internal sealed record AppConsoleResult(string Output, string Prompt, bool CloseWindow = false, string Kind = "normal", string PagerMode = "");

internal sealed record AppConsoleCompletionResult(int ReplaceFrom, IReadOnlyList<string> Candidates);

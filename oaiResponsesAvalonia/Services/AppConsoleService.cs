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

internal sealed class AppConsoleService
{
    private const int MaximumFileBytes = 32 * 1024;
    private const int MaximumListItems = 200;
    private const int MaximumDatabaseOutputCharacters = 200_000;
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
                "tables" => HandleTables(args.ToArray()),
                "describe" => HandleDescribe(args.Skip(1).ToArray()),
                "messages" => HandleMessages(args.Skip(1).ToArray()),
                "clear" => "\u001b[2J\u001b[H",
                "quit" or "exit" => "Goodbye.",
                _ => $"{command}: command not found. Type 'help' to see available commands."
            };

            bool closeWindow = command is "quit" or "exit";
            bool knownCommand = command is "help" or "pwd" or "ls" or "cd" or "cat" or
                "connect" or "disconnect" or "sessions" or "tables" or "describe" or "messages" or "clear" or "quit" or "exit";
            string kind = !knownCommand ? "error" : command is "connect" or "disconnect" ? "success" : "normal";
            return new AppConsoleResult(output, closeWindow ? string.Empty : Prompt, closeWindow, kind);
        }
        catch (Exception ex)
        {
            return new AppConsoleResult($"error: {ex.Message}", Prompt, Kind: "error");
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

    private void EnsureDatabaseConnected()
    {
        if (!_databaseConnected)
            throw new InvalidOperationException("connect to the application database first.");
    }

    private static SqliteConnection OpenReadOnlyDatabase()
    {
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppDbContext.DatabaseFilePath,
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
        if (columnName.EndsWith("Json", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(text))
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
            return new AppConsoleCompletionResult(tokenStart, []);

        string command = precedingArgs[0].ToLowerInvariant();
        if (command is not ("cd" or "cat" or "ls"))
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
            directory = string.IsNullOrEmpty(directoryPart) ? _currentDirectory : ResolvePath(directoryPart);
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
        "  tables                   List database tables (read-only)\n" +
        "  describe <table>         Show database column details\n" +
        "  messages show <id>       Show every stored message field\n" +
        "  messages show <id> --field <column>  Show one message field\n" +
        "  clear                    Clear the terminal display\n" +
        "  quit                     End the console session and close the window\n" +
        "\nThis is an app console, not an operating-system shell. File access is limited to the app data directory.";
}

internal sealed record AppConsoleResult(string Output, string Prompt, bool CloseWindow = false, string Kind = "normal");

internal sealed record AppConsoleCompletionResult(int ReplaceFrom, IReadOnlyList<string> Candidates);

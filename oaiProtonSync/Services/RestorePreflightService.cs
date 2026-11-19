using Microsoft.Data.Sqlite;

namespace oaiProtonSync.Services;

public sealed record ConversationCounts(long Sessions, long Messages, long MediaReferences);

/// <summary>Reads summary counts only. It never restores or modifies either database.</summary>
public sealed class RestorePreflightService
{
    public async Task<ConversationCounts> ReadCountsAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("The database for the comparison was not found.", databasePath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // A single statement sees a consistent SQLite read snapshot for all three counts.
        command.CommandText = "SELECT (SELECT COUNT(*) FROM Sessions), (SELECT COUNT(*) FROM Messages), (SELECT COUNT(*) FROM Media);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("The database summary returned no results.");
        return new ConversationCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }
}

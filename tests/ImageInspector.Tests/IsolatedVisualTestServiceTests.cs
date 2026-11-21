using Microsoft.Data.Sqlite;
using oaiResponsesAvalonia.Services;

namespace ImageInspector.Tests;

public sealed class IsolatedVisualTestServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public void CreateCopy_CopiesDatabaseAndMediaWithoutChangingSource()
    {
        string restore = CreateRestore("media/photo.png", includeFile: true);
        string copy = IsolatedVisualTestService.CreateCopy(restore, RestoreRoot, VisualRoot);
        Assert.NotEqual(restore, copy);
        Assert.Equal("example", File.ReadAllText(Path.Combine(copy, "images", "photo.png")));
        File.WriteAllText(Path.Combine(copy, "images", "photo.png"), "changed copy");
        Assert.Equal("example", File.ReadAllText(Path.Combine(restore, "images", "photo.png")));
        Assert.Equal("media/photo.png", ReadPath(Path.Combine(copy, "localhistory.db")));
        Assert.Equal("media/photo.png", ReadPath(Path.Combine(restore, "localhistory.db")));
    }

    [Theory]
    [InlineData("media/missing.png", false)]
    [InlineData("/outside/file.png", false)]
    [InlineData("media/../file.png", false)]
    public void CreateCopy_RejectsMissingOrUnsafeMediaAndRemovesIncompleteCopy(string path, bool includeFile)
    {
        string restore = CreateRestore(path, includeFile);
        Assert.Throws<InvalidDataException>(() => IsolatedVisualTestService.CreateCopy(restore, RestoreRoot, VisualRoot));
        Assert.Empty(Directory.GetDirectories(VisualRoot));
    }

    [Fact]
    public void CreateCopy_RejectsFolderOutsideRestoreRoot()
    {
        string unrelated = Path.Combine(_root, $"restore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(unrelated);
        Assert.Throws<InvalidDataException>(() => IsolatedVisualTestService.CreateCopy(unrelated, RestoreRoot, VisualRoot));
    }

    private string RestoreRoot => Path.Combine(_root, "restores");
    private string VisualRoot => Path.Combine(_root, "visual");

    private string CreateRestore(string mediaPath, bool includeFile)
    {
        string restore = Path.Combine(RestoreRoot, $"restore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(restore, "images"));
        Directory.CreateDirectory(Path.Combine(restore, "files"));
        File.WriteAllText(Path.Combine(restore, "manifest.json"), "{}");
        if (includeFile) File.WriteAllText(Path.Combine(restore, "images", "photo.png"), "example");
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(restore, "localhistory.db"), Pooling = false };
        using var connection = new SqliteConnection(cs.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Media (LocalPath TEXT NOT NULL); INSERT INTO Media VALUES ($path);";
        command.Parameters.AddWithValue("$path", mediaPath);
        command.ExecuteNonQuery();
        return restore;
    }

    private static string ReadPath(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(cs.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT LocalPath FROM Media";
        return (string)command.ExecuteScalar()!;
    }

    public void Dispose() => Directory.Delete(_root, true);
}

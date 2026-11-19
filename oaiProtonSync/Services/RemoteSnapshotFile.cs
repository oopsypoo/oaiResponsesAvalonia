namespace oaiProtonSync.Services;

public sealed record RemoteSnapshotFile(string Name, string RemotePath)
{
    public string DisplayName => Name;
}

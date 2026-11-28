using System;
using System.Threading;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services;

/// <summary>Coordinates regular-file sync before the database snapshot operation.</summary>
public sealed class ProtonSyncWorkflow
{
    private static readonly SemaphoreSlim WorkflowLock = new(1, 1);
    private readonly AppSettings _settings;
    private readonly IRemoteStore _remote;
    private readonly string? _databasePath;

    public ProtonSyncWorkflow(AppSettings settings)
        : this(settings, new ProtonDriveRemoteStore(settings.ProtonDriveCliPath))
    {
    }

    public ProtonSyncWorkflow(AppSettings settings, IRemoteStore remote, string? databasePath = null)
    {
        _settings = settings;
        _remote = remote;
        _databasePath = databasePath;
    }

    public Task<ProtonSyncWorkflowResult> PullAsync(Action<string> log, CancellationToken cancellationToken = default) =>
        RunAsync(push: false, log, cancellationToken);

    public Task<ProtonSyncWorkflowResult> PushAsync(Action<string> log, CancellationToken cancellationToken = default) =>
        RunAsync(push: true, log, cancellationToken);

    private async Task<ProtonSyncWorkflowResult> RunAsync(bool push, Action<string> log, CancellationToken cancellationToken)
    {
        if (!_settings.ProtonDriveSyncEnabled)
            return new ProtonSyncWorkflowResult(ProtonFileSyncStatus.Failed, "Proton Sync is disabled in settings.");

        if (!await WorkflowLock.WaitAsync(0, cancellationToken))
            return new ProtonSyncWorkflowResult(ProtonFileSyncStatus.Failed, "Another Proton Sync workflow is already running.");

        try
        {
            string direction = push ? "push" : "pull";
            log($"Starting automatic Proton Drive {direction}: regular files first, database snapshot second.{Environment.NewLine}");

            var fileSync = new ProtonFileSyncService(_settings, _remote);
            ProtonFileSyncResult files = push
                ? await fileSync.PushAsync(log, cancellationToken)
                : await fileSync.PullAsync(log, cancellationToken);

            log($"Regular files: {files.Status}: {files.Summary}{Environment.NewLine}");
            if (files.Status != ProtonFileSyncStatus.Ok)
            {
                return new ProtonSyncWorkflowResult(files.Status,
                    $"Regular-file {direction} did not complete successfully; database sync was not attempted. {files.Summary}");
            }

            var databaseSync = new ProtonDatabaseSyncService(_settings, _remote, _databasePath);
            ProtonFileSyncResult database = push
                ? await databaseSync.PushAsync(log, cancellationToken)
                : await databaseSync.PullAsync(log, cancellationToken);

            log($"Database: {database.Status}: {database.Summary}{Environment.NewLine}");
            return new ProtonSyncWorkflowResult(database.Status,
                $"Regular files completed. Database {direction}: {database.Summary}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log($"Automatic Proton Drive sync failed: {ex}{Environment.NewLine}");
            return new ProtonSyncWorkflowResult(ProtonFileSyncStatus.Failed, ex.Message);
        }
        finally
        {
            WorkflowLock.Release();
        }
    }
}

public sealed record ProtonSyncWorkflowResult(ProtonFileSyncStatus Status, string Summary);

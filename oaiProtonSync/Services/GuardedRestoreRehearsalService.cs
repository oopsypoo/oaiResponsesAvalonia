using oaiResponsesAvalonia.Services;

namespace oaiProtonSync.Services;

public sealed record GuardedRestoreRehearsalResult(
    string Workspace, string ReplacedDirectory, string RecoveryDirectory,
    string OriginalSnapshotPath, RestoredAppCompatibilityResult Original,
    RestoredAppCompatibilityResult Replacement);

/// <summary>
/// Rehearses replacement and rollback only inside a newly created disposable workspace.
/// This is not a live-data restore and must not be used as one.
/// </summary>
public sealed class GuardedRestoreRehearsalService
{
    private readonly string _root;
    private readonly ConversationSnapshotService _snapshotService;

    public GuardedRestoreRehearsalService()
        : this(Path.Combine(AppPaths.DataDirectory, "ProtonSync", "GuardedRestoreTests"), new ConversationSnapshotService()) { }

    internal GuardedRestoreRehearsalService(string root, ConversationSnapshotService snapshotService)
    {
        _root = root;
        _snapshotService = snapshotService;
    }

    public Task<GuardedRestoreRehearsalResult> RunAsync(string replacementArchive, Action<string> report,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(replacementArchive, report, null, cancellationToken);

    // The hook allows automated tests to simulate failures at the replacement boundary.
    internal async Task<GuardedRestoreRehearsalResult> RunCoreAsync(string replacementArchive, Action<string> report,
        Action<string>? checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementArchive);
        ArgumentNullException.ThrowIfNull(report);
        if (!File.Exists(replacementArchive))
            throw new FileNotFoundException("The downloaded snapshot was not found.", replacementArchive);

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_root);
        string workspace = Path.Combine(_root, $"rehearsal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        string current = Path.Combine(workspace, "current");
        string recovery = Path.Combine(workspace, "recovery");
        string rejected = Path.Combine(workspace, "rejected-replacement");
        report($"Disposable rehearsal workspace: {workspace}");
        report("Creating a fresh local snapshot to represent the data being replaced; live data will not be modified…");

        var baselineArchive = await _snapshotService.CreateAsync(report, cancellationToken);
        report($"Original-data snapshot retained at: {baselineArchive.ArchivePath}");
        var restore = new IsolatedSnapshotRestoreService(workspace);
        var compatibility = new RestoredAppCompatibilityService();

        report("Preparing and validating the disposable original data…");
        var original = await restore.RestoreAsync(baselineArchive.ArchivePath, report, cancellationToken);
        var originalCounts = await compatibility.CheckAsync(original, cancellationToken);
        Directory.Move(original.RestoreDirectory, current);

        report("Preparing and validating the replacement snapshot in a separate staging directory…");
        var staged = await restore.RestoreAsync(replacementArchive, report, cancellationToken);
        await compatibility.CheckAsync(staged, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Invoke("before-backup");

        // All folders are siblings in this disposable workspace. Preserve the entire original
        // directory as the recovery copy, and never delete it even if replacement succeeds.
        Directory.Move(current, recovery);
        try
        {
            report($"Original disposable data retained for recovery: {recovery}");
            checkpoint?.Invoke("after-backup");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staged.RestoreDirectory, current);
            checkpoint?.Invoke("after-replacement");
            cancellationToken.ThrowIfCancellationRequested();

            report("Reopening the replaced disposable database and checking all media references…");
            var installed = staged with
            {
                RestoreDirectory = current,
                DatabasePath = Path.Combine(current, "localhistory.db"),
                ImagesDirectory = Path.Combine(current, "images"),
                FilesDirectory = Path.Combine(current, "files")
            };
            var replacementCounts = await compatibility.CheckAsync(installed, cancellationToken);
            checkpoint?.Invoke("after-validation");
            cancellationToken.ThrowIfCancellationRequested();
            report("Disposable replacement validated. The original disposable data and local snapshot are retained.");
            return new GuardedRestoreRehearsalResult(workspace, current, recovery, baselineArchive.ArchivePath,
                originalCounts, replacementCounts);
        }
        catch (Exception error)
        {
            try
            {
                // Preserve the replacement for inspection rather than deleting it. Do not use
                // the cancelled token during rollback.
                if (Directory.Exists(current))
                    Directory.Move(current, rejected);
                Directory.Move(recovery, current);
                report($"Rehearsal failed or stopped; original disposable data restored at: {current}");
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException($"Rehearsal failed and automatic rollback failed. Inspect and retain workspace: {workspace}", error, rollbackError);
            }
            throw;
        }
    }
}

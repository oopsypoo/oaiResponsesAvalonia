using Microsoft.EntityFrameworkCore;
using oaiResponsesAvalonia.Data;

namespace oaiProtonSync.Services;

/// <summary>
/// Identity maintenance for newly created disposable baselines only. This is not
/// a migration of a live database and does not implement sync or deletion markers.
/// The caller creates the identity tables and invokes this inside its transaction.
/// </summary>
internal static class ExperimentalSyncIdentityService
{
    // SQLite evaluates randomblob separately for each inserted row. The format is
    // UUID v4: the fixed 4 denotes the version and 8/9/a/b denote the variant.
    private const string NewUuid = "lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-' || substr('89ab', (random() & 3) + 1, 1) || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6)))";

    internal static async Task InstallBaselineTriggersAsync(AppDbContext db, CancellationToken token)
    {
        await db.Database.ExecuteSqlRawAsync($"""
            CREATE TRIGGER ExperimentalSessionIdentity_Insert AFTER INSERT ON Sessions
            BEGIN
                INSERT INTO ExperimentalSessionIdentity (SessionId, SyncId)
                VALUES (NEW.Id, {NewUuid});
            END;
            """, token);
        await db.Database.ExecuteSqlRawAsync($"""
            CREATE TRIGGER ExperimentalMessageIdentity_Insert AFTER INSERT ON Messages
            BEGIN
                INSERT INTO ExperimentalMessageIdentity (MessageId, SyncId)
                VALUES (NEW.Id, {NewUuid});
            END;
            """, token);
        // Do not silently discard an identity before a deletion protocol exists.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER ExperimentalSessionIdentity_BlockDelete BEFORE DELETE ON Sessions
            BEGIN
                SELECT RAISE(ABORT, 'Deletion is disabled for experimental sync baselines; deletion tracking is not implemented.');
            END;
            """, token);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER ExperimentalMessageIdentity_BlockDelete BEFORE DELETE ON Messages
            BEGIN
                SELECT RAISE(ABORT, 'Deletion is disabled for experimental sync baselines; deletion tracking is not implemented.');
            END;
            """, token);
    }
}

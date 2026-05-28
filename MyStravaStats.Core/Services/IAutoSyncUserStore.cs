using MyStravaStats.Core.Models;

namespace MyStravaStats.Core.Services;

public interface IAutoSyncUserStore
{
    bool IsConfigured { get; }

    Task SaveEnabledUserAsync(StravaAuthSession authSession, string? acceptedScope, CancellationToken cancellationToken);

    Task DeleteUserAsync(long athleteId, CancellationToken cancellationToken);

    Task<AutoSyncUserRecord?> GetUserAsync(long athleteId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AutoSyncUserRecord>> GetEnabledUsersAsync(CancellationToken cancellationToken);

    StravaAuthSession UnprotectAuthSession(AutoSyncUserRecord record);

    Task MarkSyncSuccessAsync(
        AutoSyncUserRecord record,
        StravaAuthSession authSession,
        CancellationToken cancellationToken);

    Task MarkSyncFailureAsync(
        AutoSyncUserRecord record,
        string errorMessage,
        bool requiresReauthorization,
        CancellationToken cancellationToken);
}

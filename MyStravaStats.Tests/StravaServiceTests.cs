using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyStravaStats.Core.Models;
using MyStravaStats.Core.Options;
using MyStravaStats.Core.Services;
using MyStravaStatsWebApp.Services;
using Xunit;

namespace MyStravaStats.Tests;

public sealed class StravaServiceTests
{
    [Fact]
    public async Task StopAutoSyncDeletesOnlyAutoSyncRecord()
    {
        var httpContext = new DefaultHttpContext
        {
            Session = new TestSession()
        };
        var sessionStore = new StravaSessionStore();
        sessionStore.SaveAuth(httpContext, new StravaAuthSession
        {
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            ExpiresAtUnixTimeSeconds = 4_102_444_800,
            AthleteId = 123
        });

        var autoSyncStore = new RecordingAutoSyncUserStore();
        var service = CreateStravaService(httpContext, sessionStore, autoSyncStore);

        await service.StopAutoSyncAsync(httpContext, CancellationToken.None);

        Assert.Equal(123, autoSyncStore.DeletedAthleteId);
        Assert.Null(autoSyncStore.SavedAuthSession);
    }

    private static StravaService CreateStravaService(
        HttpContext httpContext,
        StravaSessionStore sessionStore,
        RecordingAutoSyncUserStore autoSyncStore)
    {
        var stravaOptions = Options.Create(new StravaOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret"
        });
        var apiClient = new StravaApiClient(
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("Unexpected HTTP request."))),
            stravaOptions);

        return new StravaService(
            stravaOptions,
            new HttpContextAccessor { HttpContext = httpContext },
            sessionStore,
            apiClient,
            new StravaStatsService(apiClient, NullLogger<StravaStatsService>.Instance),
            new StatsBlobStorageService(
                Options.Create(new StatsBlobStorageOptions()),
                NullLogger<StatsBlobStorageService>.Instance),
            autoSyncStore,
            NullLogger<StravaService>.Instance);
    }

    private sealed class RecordingAutoSyncUserStore : IAutoSyncUserStore
    {
        public bool IsConfigured => true;

        public long? DeletedAthleteId { get; private set; }

        public StravaAuthSession? SavedAuthSession { get; private set; }

        public Task SaveEnabledUserAsync(StravaAuthSession authSession, string? acceptedScope, CancellationToken cancellationToken)
        {
            SavedAuthSession = authSession;
            return Task.CompletedTask;
        }

        public Task DeleteUserAsync(long athleteId, CancellationToken cancellationToken)
        {
            DeletedAthleteId = athleteId;
            return Task.CompletedTask;
        }

        public Task<AutoSyncUserRecord?> GetUserAsync(long athleteId, CancellationToken cancellationToken)
        {
            return Task.FromResult<AutoSyncUserRecord?>(null);
        }

        public Task<IReadOnlyList<AutoSyncUserRecord>> GetEnabledUsersAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<AutoSyncUserRecord>>(Array.Empty<AutoSyncUserRecord>());
        }

        public StravaAuthSession UnprotectAuthSession(AutoSyncUserRecord record)
        {
            throw new NotSupportedException();
        }

        public Task MarkSyncSuccessAsync(AutoSyncUserRecord record, StravaAuthSession authSession, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task MarkSyncFailureAsync(AutoSyncUserRecord record, string errorMessage, bool requiresReauthorization, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}

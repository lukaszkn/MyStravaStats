using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyStravaStats.Core.Models;
using MyStravaStats.Core.Options;

namespace MyStravaStats.Core.Services;

public sealed class AutoSyncBlobStorageService : IAutoSyncUserStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly BlobContainerClient? _containerClient;
    private readonly AutoSyncTokenProtector _tokenProtector;
    private readonly ILogger<AutoSyncBlobStorageService> _logger;

    public AutoSyncBlobStorageService(
        IOptions<StatsBlobStorageOptions> storageOptions,
        IOptions<AutoSyncOptions> autoSyncOptions,
        AutoSyncTokenProtector tokenProtector,
        ILogger<AutoSyncBlobStorageService> logger)
    {
        _tokenProtector = tokenProtector;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(storageOptions.Value.ConnectionString))
        {
            var containerName = string.IsNullOrWhiteSpace(autoSyncOptions.Value.ContainerName)
                ? "auto-sync-users"
                : autoSyncOptions.Value.ContainerName.Trim();

            _containerClient = new BlobContainerClient(storageOptions.Value.ConnectionString, containerName);
        }
    }

    public bool IsConfigured => _containerClient is not null && _tokenProtector.IsConfigured;

    public async Task SaveEnabledUserAsync(
        StravaAuthSession authSession,
        string? acceptedScope,
        CancellationToken cancellationToken)
    {
        if (_containerClient is null)
        {
            throw new InvalidOperationException("Azure stats blob storage is not configured.");
        }

        if (!_tokenProtector.IsConfigured)
        {
            throw new InvalidOperationException("Auto sync token encryption is not configured. Set AUTO_SYNC_TOKEN_ENCRYPTION_KEY.");
        }

        if (authSession.AthleteId is null)
        {
            throw new InvalidOperationException("Athlete id is required to enable auto sync.");
        }

        var now = DateTimeOffset.UtcNow;
        var existing = await GetUserAsync(authSession.AthleteId.Value, cancellationToken);
        var record = existing ?? new AutoSyncUserRecord
        {
            AthleteId = authSession.AthleteId.Value,
            CreatedAtUtc = now,
            ProtectedAuthSession = string.Empty
        };

        record.AthleteName = string.IsNullOrWhiteSpace(authSession.AthleteDisplayName)
            ? $"Athlete {authSession.AthleteId.Value}"
            : authSession.AthleteDisplayName;
        record.AcceptedScope = acceptedScope ?? authSession.AcceptedScope;
        record.IsEnabled = true;
        record.RequiresReauthorization = false;
        record.UpdatedAtUtc = now;
        record.LastSyncStatus = "Enabled";
        record.LastError = null;
        record.ProtectedAuthSession = ProtectAuthSession(authSession, acceptedScope);

        await UploadRecordAsync(record, cancellationToken);

        _logger.LogInformation("Enabled auto sync for athlete {AthleteId}.", authSession.AthleteId.Value);
    }

    public async Task DeleteUserAsync(long athleteId, CancellationToken cancellationToken)
    {
        if (_containerClient is null)
        {
            throw new InvalidOperationException("Azure stats blob storage is not configured.");
        }

        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await _containerClient.DeleteBlobIfExistsAsync(GetBlobName(athleteId), cancellationToken: cancellationToken);

        _logger.LogInformation("Disabled auto sync for athlete {AthleteId}.", athleteId);
    }

    public async Task<AutoSyncUserRecord?> GetUserAsync(long athleteId, CancellationToken cancellationToken)
    {
        if (_containerClient is null)
        {
            return null;
        }

        try
        {
            var blobClient = _containerClient.GetBlobClient(GetBlobName(athleteId));
            var blobExists = await blobClient.ExistsAsync(cancellationToken);
            if (!blobExists.Value)
            {
                return null;
            }

            var download = await blobClient.DownloadContentAsync(cancellationToken);
            return download.Value.Content.ToObjectFromJson<AutoSyncUserRecord>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to read auto-sync user record for athlete {AthleteId}.", athleteId);
            return null;
        }
    }

    public async Task<IReadOnlyList<AutoSyncUserRecord>> GetEnabledUsersAsync(CancellationToken cancellationToken)
    {
        if (_containerClient is null)
        {
            return Array.Empty<AutoSyncUserRecord>();
        }

        var containerExists = await _containerClient.ExistsAsync(cancellationToken);
        if (!containerExists.Value)
        {
            return Array.Empty<AutoSyncUserRecord>();
        }

        var records = new List<AutoSyncUserRecord>();

        await foreach (var blobItem in _containerClient.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            if (!blobItem.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var record = await DownloadRecordAsync(blobItem.Name, cancellationToken);
            if (record is { IsEnabled: true, RequiresReauthorization: false })
            {
                records.Add(record);
            }
        }

        return records;
    }

    public StravaAuthSession UnprotectAuthSession(AutoSyncUserRecord record)
    {
        var payload = _tokenProtector.Unprotect<AutoSyncAuthSessionPayload>(record.ProtectedAuthSession);
        return new StravaAuthSession
        {
            AccessToken = payload.AccessToken,
            RefreshToken = payload.RefreshToken,
            ExpiresAtUnixTimeSeconds = payload.ExpiresAtUnixTimeSeconds,
            AthleteId = payload.AthleteId ?? record.AthleteId,
            AthleteFirstName = payload.AthleteFirstName,
            AthleteLastName = payload.AthleteLastName,
            AcceptedScope = payload.AcceptedScope ?? record.AcceptedScope
        };
    }

    public async Task MarkSyncSuccessAsync(
        AutoSyncUserRecord record,
        StravaAuthSession authSession,
        CancellationToken cancellationToken)
    {
        record.ProtectedAuthSession = ProtectAuthSession(authSession);
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;
        record.LastSyncedAtUtc = record.UpdatedAtUtc;
        record.LastSyncStatus = "Synced";
        record.LastError = null;
        record.RequiresReauthorization = false;

        await UploadRecordAsync(record, cancellationToken);
    }

    public async Task MarkSyncFailureAsync(
        AutoSyncUserRecord record,
        string errorMessage,
        bool requiresReauthorization,
        CancellationToken cancellationToken)
    {
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;
        record.LastSyncStatus = requiresReauthorization ? "Needs reauthorization" : "Failed";
        record.LastError = errorMessage;
        record.RequiresReauthorization = requiresReauthorization;

        await UploadRecordAsync(record, cancellationToken);
    }

    private async Task<AutoSyncUserRecord?> DownloadRecordAsync(string blobName, CancellationToken cancellationToken)
    {
        try
        {
            var blobClient = _containerClient!.GetBlobClient(blobName);
            var download = await blobClient.DownloadContentAsync(cancellationToken);

            return download.Value.Content.ToObjectFromJson<AutoSyncUserRecord>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipping unreadable auto-sync user blob {BlobName}.", blobName);
            return null;
        }
    }

    private async Task UploadRecordAsync(AutoSyncUserRecord record, CancellationToken cancellationToken)
    {
        if (_containerClient is null)
        {
            throw new InvalidOperationException("Azure stats blob storage is not configured.");
        }

        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobClient = _containerClient.GetBlobClient(GetBlobName(record.AthleteId));
        var payload = BinaryData.FromObjectAsJson(record, JsonOptions);

        await blobClient.UploadAsync(payload, overwrite: true, cancellationToken);
        await blobClient.SetHttpHeadersAsync(
            new BlobHttpHeaders
            {
                ContentType = "application/json"
            },
            cancellationToken: cancellationToken);
    }

    private string ProtectAuthSession(StravaAuthSession authSession, string? withAcceptedScope = null)
    {
        return _tokenProtector.Protect(new AutoSyncAuthSessionPayload
        {
            AccessToken = authSession.AccessToken,
            RefreshToken = authSession.RefreshToken,
            ExpiresAtUnixTimeSeconds = authSession.ExpiresAtUnixTimeSeconds,
            AthleteId = authSession.AthleteId,
            AthleteFirstName = authSession.AthleteFirstName,
            AthleteLastName = authSession.AthleteLastName,
            AcceptedScope = withAcceptedScope ?? authSession.AcceptedScope
        });
    }

    private static string GetBlobName(long athleteId)
    {
        return $"{athleteId}.json";
    }
}

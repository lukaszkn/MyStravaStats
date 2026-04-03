using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using MyStravaStatsWebApp.Models;
using MyStravaStatsWebApp.Options;

namespace MyStravaStatsWebApp.Services;

public sealed class StatsBlobStorageService
{
    private const string ContainerName = "stats";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly BlobContainerClient? _containerClient;
    private readonly ILogger<StatsBlobStorageService> _logger;

    public StatsBlobStorageService(
        IOptions<StatsBlobStorageOptions> options,
        ILogger<StatsBlobStorageService> logger)
    {
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(options.Value.ConnectionString))
        {
            _containerClient = new BlobContainerClient(options.Value.ConnectionString, ContainerName);
        }
    }

    public bool IsConfigured => _containerClient is not null;

    public async Task<AthleteCompetitionTable> GetCompetitionTableAsync(int year, CancellationToken cancellationToken)
    {
        if (_containerClient is null)
        {
            return new AthleteCompetitionTable
            {
                IsConfigured = false,
                Year = year
            };
        }

        try
        {
            var containerExists = await _containerClient.ExistsAsync(cancellationToken);
            if (!containerExists.Value)
            {
                return new AthleteCompetitionTable
                {
                    IsConfigured = true,
                    Year = year
                };
            }

            var documents = new List<AthleteStatsBlobDocument>();

            await foreach (var blobItem in _containerClient.GetBlobsAsync(cancellationToken: cancellationToken))
            {
                if (!blobItem.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var document = await DownloadAthleteStatsDocumentAsync(blobItem.Name, cancellationToken);
                if (document is null || document.Year != year)
                {
                    continue;
                }

                documents.Add(document);
            }

            return BuildCompetitionTable(year, documents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to read athlete competition data from blob storage.");

            return new AthleteCompetitionTable
            {
                IsConfigured = true,
                Year = year,
                ErrorMessage = $"Unable to load competition stats: {ex.Message}"
            };
        }
    }

    public async Task UploadDashboardStateAsync(StravaDashboardState dashboardState, CancellationToken cancellationToken)
    {
        if (_containerClient is null)
        {
            throw new InvalidOperationException("Azure stats blob storage is not configured.");
        }

        if (dashboardState.AthleteId is null)
        {
            throw new InvalidOperationException("Athlete id is required to upload athlete-specific stats.");
        }

        var document = new AthleteStatsBlobDocument
        {
            AthleteId = dashboardState.AthleteId.Value,
            AthleteName = dashboardState.AthleteName,
            Year = dashboardState.Year,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            DashboardState = dashboardState
        };

        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobClient = _containerClient.GetBlobClient($"{dashboardState.AthleteId.Value}.json");
        var payload = BinaryData.FromObjectAsJson(document, JsonOptions);

        await blobClient.UploadAsync(payload, overwrite: true, cancellationToken);
        await blobClient.SetHttpHeadersAsync(
            new BlobHttpHeaders
            {
                ContentType = "application/json"
            },
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Uploaded dashboard stats for athlete {AthleteId} to blob {BlobName}.",
            dashboardState.AthleteId.Value,
            blobClient.Name);
    }

    private async Task<AthleteStatsBlobDocument?> DownloadAthleteStatsDocumentAsync(string blobName, CancellationToken cancellationToken)
    {
        try
        {
            var blobClient = _containerClient!.GetBlobClient(blobName);
            var download = await blobClient.DownloadContentAsync(cancellationToken);

            return download.Value.Content.ToObjectFromJson<AthleteStatsBlobDocument>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipping unreadable athlete stats blob {BlobName}.", blobName);
            return null;
        }
    }

    private static AthleteCompetitionTable BuildCompetitionTable(int year, IEnumerable<AthleteStatsBlobDocument> documents)
    {
        var athleteStats = documents
            .Where(document => document.AthleteId > 0)
            .GroupBy(document => document.AthleteId)
            .Select(group => group
                .OrderByDescending(document => document.GeneratedAtUtc)
                .First())
            .Select(document => new CompetitionAthleteData(
                document.AthleteId,
                ResolveAthleteName(document),
                document.GeneratedAtUtc,
                document.DashboardState.OverallTotals.DistanceMeters,
                ResolveMonthlyDistances(document.DashboardState)))
            .OrderByDescending(entry => entry.YearlyDistanceMeters)
            .ThenBy(entry => entry.AthleteName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var leaderDistanceMeters = athleteStats.FirstOrDefault()?.YearlyDistanceMeters ?? 0d;
        var athletes = new List<AthleteCompetitionColumn>(athleteStats.Length);
        double? previousDistance = null;
        var currentRank = 0;

        for (var index = 0; index < athleteStats.Length; index++)
        {
            var athlete = athleteStats[index];

            if (previousDistance is null || !AreDistancesEqual(athlete.YearlyDistanceMeters, previousDistance.Value))
            {
                currentRank = index + 1;
            }

            athletes.Add(new AthleteCompetitionColumn
            {
                AthleteId = athlete.AthleteId,
                AthleteName = athlete.AthleteName,
                GeneratedAtUtc = athlete.GeneratedAtUtc,
                YearlyDistanceMeters = athlete.YearlyDistanceMeters,
                Rank = currentRank,
                IsLeader = leaderDistanceMeters > 0d && AreDistancesEqual(athlete.YearlyDistanceMeters, leaderDistanceMeters)
            });

            previousDistance = athlete.YearlyDistanceMeters;
        }

        var monthlyDistancesByAthleteId = athleteStats.ToDictionary(
            athlete => athlete.AthleteId,
            athlete => athlete.MonthlyDistances);

        var rows = Enumerable.Range(1, 12)
            .Select(month =>
            {
                var distancesByAthleteId = athletes.ToDictionary(
                    athlete => athlete.AthleteId,
                    athlete => monthlyDistancesByAthleteId.TryGetValue(athlete.AthleteId, out var monthlyDistances) &&
                        monthlyDistances.TryGetValue(month, out var distance)
                            ? distance
                            : 0d);

                var winningDistance = distancesByAthleteId.Count == 0
                    ? 0d
                    : distancesByAthleteId.Values.Max();

                var leadingAthleteIds = winningDistance > 0d
                    ? distancesByAthleteId
                        .Where(pair => AreDistancesEqual(pair.Value, winningDistance))
                        .Select(pair => pair.Key)
                        .ToArray()
                    : Array.Empty<long>();

                return new AthleteCompetitionMonthRow
                {
                    Month = month,
                    DistancesByAthleteId = distancesByAthleteId,
                    LeadingAthleteIds = leadingAthleteIds
                };
            })
            .ToArray();

        return new AthleteCompetitionTable
        {
            IsConfigured = true,
            Year = year,
            Athletes = athletes,
            Rows = rows
        };
    }

    private static IReadOnlyDictionary<int, double> ResolveMonthlyDistances(StravaDashboardState dashboardState)
    {
        var monthlyDistances = dashboardState.ActivityTypeTable.Rows.ToDictionary(
            row => row.Month,
            row => row.GroupTotals.Values.Sum(totals => totals.DistanceMeters));

        foreach (var month in Enumerable.Range(1, 12))
        {
            monthlyDistances.TryAdd(month, 0d);
        }

        return monthlyDistances;
    }

    private static string ResolveAthleteName(AthleteStatsBlobDocument document)
    {
        if (!string.IsNullOrWhiteSpace(document.AthleteName))
        {
            return document.AthleteName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(document.DashboardState.AthleteName))
        {
            return document.DashboardState.AthleteName.Trim();
        }

        return $"Athlete {document.AthleteId}";
    }

    private static bool AreDistancesEqual(double left, double right)
    {
        return Math.Abs(left - right) < 0.01d;
    }

    private sealed record CompetitionAthleteData(
        long AthleteId,
        string AthleteName,
        DateTimeOffset GeneratedAtUtc,
        double YearlyDistanceMeters,
        IReadOnlyDictionary<int, double> MonthlyDistances);
}

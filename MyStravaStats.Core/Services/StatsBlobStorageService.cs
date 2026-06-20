using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyStravaStats.Core.Models;
using MyStravaStats.Core.Options;

namespace MyStravaStats.Core.Services;

public sealed class StatsBlobStorageService
{
    private const string ContainerName = "stats";

    private static readonly string[] TrendPalette =
    [
        "#0d6efd",
        "#dc3545",
        "#198754",
        "#fd7e14",
        "#6f42c1",
        "#20c997",
        "#d63384",
        "#0dcaf0",
        "#ffc107",
        "#6610f2"
    ];

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
                if (!IsDashboardSnapshotBlobName(blobItem.Name))
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

    public async Task<AthleteTrendChart> GetTrendChartAsync(int year, CancellationToken cancellationToken)
    {
        if (_containerClient is null)
        {
            return new AthleteTrendChart
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
                return new AthleteTrendChart
                {
                    IsConfigured = true,
                    Year = year
                };
            }

            var documents = new List<AthleteTrendBlobDocument>();

            await foreach (var blobItem in _containerClient.GetBlobsAsync(cancellationToken: cancellationToken))
            {
                if (IsDashboardSnapshotBlobName(blobItem.Name))
                {
                    var snapshotDocument = await DownloadAthleteStatsDocumentAsync(blobItem.Name, cancellationToken);
                    if (snapshotDocument is null || snapshotDocument.Year != year || snapshotDocument.DashboardState.TrendPoints.Count == 0)
                    {
                        continue;
                    }

                    documents.Add(AthleteTrendBlobDocument.FromDashboardState(
                        snapshotDocument.DashboardState,
                        snapshotDocument.GeneratedAtUtc));
                    continue;
                }

                if (!blobItem.Name.StartsWith(GetTrendBlobPrefix(year), StringComparison.Ordinal) ||
                    !blobItem.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var trendDocument = await DownloadAthleteTrendDocumentAsync(blobItem.Name, cancellationToken);
                if (trendDocument is null || trendDocument.Year != year || trendDocument.AthleteId <= 0 || trendDocument.Points.Count == 0)
                {
                    continue;
                }

                documents.Add(trendDocument);
            }

            return BuildTrendChart(year, documents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to read athlete trend data from blob storage.");

            return new AthleteTrendChart
            {
                IsConfigured = true,
                Year = year,
                ErrorMessage = $"Unable to load trend stats: {ex.Message}"
            };
        }
    }

    public async Task UploadDashboardStateAsync(StravaDashboardState dashboardState, CancellationToken cancellationToken)
    {
        if (_containerClient is null)
        {
            throw new InvalidOperationException("Azure stats blob storage is not configured.");
        }

        var generatedAtUtc = DateTimeOffset.UtcNow;
        var document = AthleteStatsBlobDocument.FromDashboardState(dashboardState, generatedAtUtc);
        var trendDocument = AthleteTrendBlobDocument.FromDashboardState(dashboardState, generatedAtUtc);

        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var dashboardBlobName = $"{document.AthleteId}.json";
        var trendBlobName = GetTrendBlobName(trendDocument.Year, trendDocument.AthleteId);

        await UploadJsonAsync(dashboardBlobName, document, cancellationToken);
        await UploadJsonAsync(trendBlobName, trendDocument, cancellationToken);

        _logger.LogInformation(
            "Uploaded dashboard stats for athlete {AthleteId} to blob {BlobName}.",
            document.AthleteId,
            dashboardBlobName);

        _logger.LogInformation(
            "Uploaded trend stats for athlete {AthleteId} to blob {BlobName}.",
            trendDocument.AthleteId,
            trendBlobName);
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

    private async Task<AthleteTrendBlobDocument?> DownloadAthleteTrendDocumentAsync(string blobName, CancellationToken cancellationToken)
    {
        try
        {
            var blobClient = _containerClient!.GetBlobClient(blobName);
            var download = await blobClient.DownloadContentAsync(cancellationToken);

            return download.Value.Content.ToObjectFromJson<AthleteTrendBlobDocument>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipping unreadable athlete trend blob {BlobName}.", blobName);
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
                ResolveYearlyActivityTypeDistances(document.DashboardState),
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
                YearlyActivityTypeDistancesMeters = athlete.YearlyActivityTypeDistancesMeters,
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

    private static AthleteTrendChart BuildTrendChart(int year, IEnumerable<AthleteTrendBlobDocument> documents)
    {
        var athletes = documents
            .Where(document => document.AthleteId > 0 && document.Points.Count > 0)
            .GroupBy(document => document.AthleteId)
            .Select(group => group
                .OrderByDescending(document => document.GeneratedAtUtc)
                .ThenByDescending(document => document.Points
                    .OrderBy(point => point.RecordedAt)
                    .Last()
                    .TotalKilometers)
                .First())
            .Select(document => new AthleteTrendSeries
            {
                AthleteId = document.AthleteId,
                AthleteName = ResolveAthleteName(document),
                GeneratedAtUtc = document.GeneratedAtUtc,
                Color = GetTrendColor(document.AthleteId),
                Points = document.Points
                    .OrderBy(point => point.RecordedAt)
                    .ToArray()
            })
            .OrderByDescending(series => series.Points.Last().TotalKilometers)
            .ThenBy(series => series.AthleteName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new AthleteTrendChart
        {
            IsConfigured = true,
            Year = year,
            Athletes = athletes
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

    private static IReadOnlyDictionary<string, double> ResolveYearlyActivityTypeDistances(StravaDashboardState dashboardState)
    {
        return dashboardState.ActivityTypeTable.TotalsByGroup
            .Where(pair => pair.Value.DistanceMeters > 0d)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.DistanceMeters,
                StringComparer.Ordinal);
    }

    private static string ResolveAthleteName(AthleteStatsBlobDocument document)
    {
        if (!string.IsNullOrWhiteSpace(document.AthleteName))
        {
            return document.AthleteName;
        }

        if (!string.IsNullOrWhiteSpace(document.DashboardState.AthleteName))
        {
            return document.DashboardState.AthleteName;
        }

        return $"Athlete {document.AthleteId}";
    }

    private static string ResolveAthleteName(AthleteTrendBlobDocument document)
    {
        if (!string.IsNullOrWhiteSpace(document.AthleteName))
        {
            return document.AthleteName;
        }

        return $"Athlete {document.AthleteId}";
    }

    public static bool IsDashboardSnapshotBlobName(string blobName)
    {
        if (!blobName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || blobName.Contains('/'))
        {
            return false;
        }

        var athleteIdText = Path.GetFileNameWithoutExtension(blobName);
        return long.TryParse(athleteIdText, out var athleteId) && athleteId > 0;
    }

    public static string GetTrendColor(long athleteId)
    {
        var normalizedAthleteId = athleteId < 0
            ? (ulong)(-(athleteId + 1)) + 1
            : (ulong)athleteId;
        var paletteIndex = (int)(normalizedAthleteId % (ulong)TrendPalette.Length);

        return TrendPalette[paletteIndex];
    }

    private async Task UploadJsonAsync<T>(
        string blobName,
        T document,
        CancellationToken cancellationToken)
    {
        var blobClient = _containerClient!.GetBlobClient(blobName);
        var payload = BinaryData.FromObjectAsJson(document, JsonOptions);

        await blobClient.UploadAsync(payload, overwrite: true, cancellationToken);
        await blobClient.SetHttpHeadersAsync(
            new BlobHttpHeaders
            {
                ContentType = "application/json"
            },
            cancellationToken: cancellationToken);
    }

    private static string GetTrendBlobPrefix(int year)
    {
        return $"trends/{year}/";
    }

    private static string GetTrendBlobName(int year, long athleteId)
    {
        return $"{GetTrendBlobPrefix(year)}{athleteId}.json";
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
        IReadOnlyDictionary<string, double> YearlyActivityTypeDistancesMeters,
        IReadOnlyDictionary<int, double> MonthlyDistances);
}

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Timer;
using Microsoft.Extensions.Logging;
using MyStravaStats.Core.Services;

namespace MyStravaStats.AutoSyncFunction;

public sealed class StravaAutoSyncFunction(
    StravaAutoSyncService autoSyncService,
    ILogger<StravaAutoSyncFunction> logger)
{
    [Function(nameof(StravaAutoSyncFunction))]
    public async Task Run(
        [TimerTrigger("%AutoSyncSchedule%")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Strava auto sync timer fired at {StartedAtUtc}.", DateTimeOffset.UtcNow);

        await autoSyncService.RunAsync(cancellationToken);

        if (timerInfo.ScheduleStatus is not null)
        {
            logger.LogInformation(
                "Next Strava auto sync timer run is scheduled for {NextRun}.",
                timerInfo.ScheduleStatus.Next);
        }
    }
}

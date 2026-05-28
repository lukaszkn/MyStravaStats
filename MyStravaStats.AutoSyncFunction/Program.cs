using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyStravaStats.Core.Options;
using MyStravaStats.Core.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddOptions<StravaOptions>()
            .Configure(options =>
            {
                options.ClientId = context.Configuration["STRAVA_CLIENT_ID"];
                options.ClientSecret = context.Configuration["STRAVA_CLIENT_SECRET"];
            });

        services.AddOptions<StatsBlobStorageOptions>()
            .Configure(options =>
            {
                options.ConnectionString = context.Configuration["AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING"];
            });

        services.AddOptions<AutoSyncOptions>()
            .Configure(options =>
            {
                options.TokenEncryptionKey = context.Configuration["AUTO_SYNC_TOKEN_ENCRYPTION_KEY"];
            });

        services.AddSingleton<StatsBlobStorageService>();
        services.AddSingleton<AutoSyncTokenProtector>();
        services.AddSingleton<AutoSyncBlobStorageService>();
        services.AddSingleton<IAutoSyncUserStore>(provider => provider.GetRequiredService<AutoSyncBlobStorageService>());
        services.AddHttpClient<StravaApiClient>();
        services.AddTransient<StravaStatsService>();
        services.AddTransient<StravaAutoSyncService>();
    })
    .Build();

host.Run();

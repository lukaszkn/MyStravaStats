using MyStravaStatsWebApp.Components;
using MyStravaStats.Core.Options;
using MyStravaStats.Core.Services;
using MyStravaStatsWebApp.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".MyStravaStats.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.IdleTimeout = TimeSpan.FromDays(30);
    options.Cookie.MaxAge = TimeSpan.FromDays(30);
});

builder.Services.AddOptions<StravaOptions>()
    .Configure<IConfiguration>((options, configuration) =>
    {
        options.ClientId = configuration["STRAVA_CLIENT_ID"];
        options.ClientSecret = configuration["STRAVA_CLIENT_SECRET"];
    });

builder.Services.AddOptions<StatsBlobStorageOptions>()
    .Configure<IConfiguration>((options, configuration) =>
    {
        options.ConnectionString = configuration["AZURE_STATS_BLOB_STORAGE_CONNECTION_STRING"];
    });

builder.Services.AddOptions<AutoSyncOptions>()
    .Configure<IConfiguration>((options, configuration) =>
    {
        options.TokenEncryptionKey = configuration["AUTO_SYNC_TOKEN_ENCRYPTION_KEY"];
    });

builder.Services.AddSingleton<StravaSessionStore>();
builder.Services.AddSingleton<StatsBlobStorageService>();
builder.Services.AddSingleton<AutoSyncTokenProtector>();
builder.Services.AddSingleton<AutoSyncBlobStorageService>();
builder.Services.AddSingleton<IAutoSyncUserStore>(provider => provider.GetRequiredService<AutoSyncBlobStorageService>());
builder.Services.AddHttpClient<StravaApiClient>();
builder.Services.AddTransient<StravaStatsService>();
builder.Services.AddTransient<StravaService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseSession();
app.UseAntiforgery();

app.MapGet("/strava/login", (HttpContext httpContext, StravaService stravaService, ILogger<Program> logger) =>
{
    if (!stravaService.IsConfigured)
    {
        return Results.Redirect("/?error=" + Uri.EscapeDataString("Strava credentials are missing. Set STRAVA_CLIENT_ID and STRAVA_CLIENT_SECRET."));
    }

    var enableAutoSync = string.Equals(httpContext.Request.Query["autoSync"], "true", StringComparison.OrdinalIgnoreCase);

    try
    {
        var authorizationUrl = stravaService.BuildAuthorizationUrl(httpContext, enableAutoSync);
        return Results.Redirect(authorizationUrl);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Strava authorization start failed.");
        return Results.Redirect("/?error=" + Uri.EscapeDataString(ex.Message));
    }
});

app.MapGet("/strava/callback", async (HttpContext httpContext, StravaService stravaService, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var authorizationError = httpContext.Request.Query["error"].ToString();
    if (!string.IsNullOrWhiteSpace(authorizationError))
    {
        return Results.Redirect("/?error=" + Uri.EscapeDataString($"Strava authorization failed: {authorizationError}."));
    }

    var code = httpContext.Request.Query["code"].ToString();
    var state = httpContext.Request.Query["state"].ToString();
    var scope = httpContext.Request.Query["scope"].ToString();

    try
    {
        await stravaService.HandleCallbackAsync(httpContext, code, state, scope, cancellationToken);
        return Results.Redirect("/");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Strava callback processing failed.");
        return Results.Redirect("/?error=" + Uri.EscapeDataString(ex.Message));
    }
});

app.MapGet("/strava/logout", (HttpContext httpContext, StravaService stravaService) =>
{
    stravaService.Logout(httpContext);
    return Results.Redirect("/");
});

app.MapPost("/strava/auto-sync/stop", async (HttpContext httpContext, StravaService stravaService, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    try
    {
        await stravaService.StopAutoSyncAsync(httpContext, cancellationToken);
        return Results.Redirect("/");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unable to stop Strava auto sync.");
        return Results.Redirect("/?error=" + Uri.EscapeDataString(ex.Message));
    }
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MyStravaStatsWebApp.Models;

namespace MyStravaStatsWebApp.Services;

public sealed class StravaSessionStore
{
    private const string AuthSessionKey = "Strava.Auth";
    private const string OAuthStateKey = "Strava.OAuthState";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void SaveAuth(HttpContext httpContext, StravaAuthSession authSession)
    {
        httpContext.Session.SetString(AuthSessionKey, JsonSerializer.Serialize(authSession, JsonOptions));
    }

    public StravaAuthSession? GetAuth(HttpContext httpContext)
    {
        var value = httpContext.Session.GetString(AuthSessionKey);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<StravaAuthSession>(value, JsonOptions);
    }

    public void ClearAuth(HttpContext httpContext)
    {
        httpContext.Session.Remove(AuthSessionKey);
    }

    public void SaveState(HttpContext httpContext, string state)
    {
        httpContext.Session.SetString(OAuthStateKey, state);
    }

    public string? GetState(HttpContext httpContext)
    {
        return httpContext.Session.GetString(OAuthStateKey);
    }

    public void ClearState(HttpContext httpContext)
    {
        httpContext.Session.Remove(OAuthStateKey);
    }

    public void ClearAll(HttpContext httpContext)
    {
        ClearState(httpContext);
        ClearAuth(httpContext);
    }
}

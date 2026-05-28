using System.Net;

namespace MyStravaStats.Core.Services;

public sealed class StravaApiException : InvalidOperationException
{
    public StravaApiException(string message, HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }

    public bool RequiresReauthorization =>
        StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

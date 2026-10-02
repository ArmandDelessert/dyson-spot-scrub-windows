using System.Net;

namespace Dyss.Core;

public class DysonApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public string? ResponseBody { get; }

    public DysonApiException(string message, HttpStatusCode? statusCode = null, string? body = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = body;
    }
}

public sealed class DysonAuthException : DysonApiException
{
    public DysonAuthException(string message, HttpStatusCode? statusCode = null, string? body = null)
        : base(message, statusCode, body) { }
}

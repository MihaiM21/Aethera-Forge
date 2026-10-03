using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Aethera.Api.Http;

/// <summary>
/// Gives every request a correlation id: the inbound <c>X-Request-Id</c> when it is a safe token (1-100 characters of
/// <c>A-Z a-z 0-9 . _ : -</c>), otherwise the W3C trace id of the request (or a new GUID). The id becomes
/// <c>HttpContext.TraceIdentifier</c> (so it is the <c>traceId</c> of error bodies and the audit request id), a logging scope
/// property and the <c>X-Request-Id</c> response header. <c>traceparent</c> is echoed when the request has an activity.
/// </summary>
public sealed partial class RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
{
    public const string HeaderName = "X-Request-Id";
    public const string LogScopeKey = "RequestId";

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,100}$")]
    private static partial Regex SafeId();

    public static bool IsSafe(string? value) => value is not null && SafeId().IsMatch(value);

    public async Task InvokeAsync(HttpContext context)
    {
        var inbound = context.Request.Headers[HeaderName].ToString();
        var id = IsSafe(inbound)
            ? inbound
            : Activity.Current?.TraceId.ToString() is { Length: > 0 } traceId && traceId != "00000000000000000000000000000000"
                ? traceId
                : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = id;

        // OnStarting, not an immediate set: the exception handler clears the response (and its headers) before writing 500s.
        context.Response.OnStarting(static state =>
        {
            var http = (HttpContext)state;
            http.Response.Headers[HeaderName] = http.TraceIdentifier;
            if (Activity.Current?.Id is { } traceparent && !http.Response.Headers.ContainsKey("traceparent"))
                http.Response.Headers["traceparent"] = traceparent;
            return Task.CompletedTask;
        }, context);

        using (logger.BeginScope(new Dictionary<string, object> { [LogScopeKey] = id }))
            await next(context);
    }
}

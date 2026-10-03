using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Http.Errors;

/// <summary>Wires RFC 9457 problem bodies: every error carries <c>type</c>, <c>title</c>, <c>status</c>, <c>code</c> and <c>traceId</c>.</summary>
public static class ProblemDetailsSetup
{
    public const string ContentType = "application/problem+json";

    public static IServiceCollection AddAetheraProblemDetails(this IServiceCollection services)
    {
        // Registered before AddProblemDetails so it is the writer picked first: the API always answers errors in
        // problem+json, whatever the Accept header says.
        services.AddSingleton<IProblemDetailsWriter, AlwaysJsonProblemDetailsWriter>();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = Customize);
        services.AddExceptionHandler<ApiExceptionHandler>();
        return services;
    }

    /// <summary>Fills in whatever the producer did not set: <c>code</c> (from the status), <c>type</c>, <c>title</c>, <c>instance</c>, <c>traceId</c>.</summary>
    internal static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var http = context.HttpContext;

        problem.Status ??= http.Response.StatusCode;
        var status = problem.Status.Value;

        if (!problem.Extensions.TryGetValue("code", out var existing) || existing is not string code || code.Length == 0)
        {
            code = ProblemCodes.ForStatus(status);
            problem.Extensions["code"] = code;
        }

        // Framework-produced problems carry generic titles/types (RFC 9110 links); ours are fixed per code.
        problem.Type = $"urn:aethera:problem:{code}";
        if (string.IsNullOrEmpty(problem.Title) || problem.Title == ReasonPhrase(status))
            problem.Title = ProblemCodes.TitleOf(code);
        problem.Instance ??= http.Request.Path.Value;
        problem.Extensions["traceId"] = http.TraceIdentifier;
    }

    private static string? ReasonPhrase(int status) => Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } p ? p : null;
}

/// <summary>Writes problem bodies as <c>application/problem+json</c> using the app's JSON options (camelCase).</summary>
internal sealed class AlwaysJsonProblemDetailsWriter(
    IOptions<ProblemDetailsOptions> problemOptions, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions) : IProblemDetailsWriter
{
    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        problemOptions.Value.CustomizeProblemDetails?.Invoke(context);
        var response = context.HttpContext.Response;
        response.StatusCode = context.ProblemDetails.Status ?? response.StatusCode;
        var options = jsonOptions.Value.SerializerOptions;
        return new ValueTask(response.WriteAsJsonAsync(
            context.ProblemDetails, options, ProblemDetailsSetup.ContentType, context.HttpContext.RequestAborted));
    }
}

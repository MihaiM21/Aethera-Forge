using Microsoft.AspNetCore.Mvc;

namespace Aethera.Api.Http.Errors;

/// <summary>One field-level problem of a 4xx body: <c>pointer</c> (JSON Pointer into the body) or <c>parameter</c> (query/path), plus a short validator <c>code</c>.</summary>
public sealed record FieldError(
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Pointer,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Parameter,
    string Code, string Message)
{
    public static FieldError AtPointer(string pointer, string code, string message) => new(pointer, null, code, message);

    public static FieldError ForParameter(string parameter, string code, string message) => new(null, parameter, code, message);
}

/// <summary>
/// An error response, as a value. It is an <see cref="IResult"/> (return it from an endpoint) and, wrapped in
/// <see cref="ApiProblemException"/>, can be thrown from deeper code. Build instances with <see cref="ApiProblems"/>.
/// </summary>
public sealed class ApiProblem(int status, string code, string? detail = null, string? title = null) : IResult
{
    public int Status { get; } = status;

    /// <summary>Stable machine-readable code, <c>area.reason</c>.</summary>
    public string Code { get; } = code;

    /// <summary>Occurrence-specific text for humans. Never contains secrets, SQL or stack traces.</summary>
    public string? Detail { get; } = detail;

    public string Title { get; } = title ?? ProblemCodes.TitleOf(code);

    /// <summary>Additional top-level members (<c>errors</c>, <c>requiredScope</c>, ...).</summary>
    public IDictionary<string, object?> Extensions { get; } = new Dictionary<string, object?>();

    /// <summary>Response headers to add (<c>Retry-After</c>, <c>WWW-Authenticate</c>).</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

    public ApiProblem WithExtension(string name, object? value)
    {
        Extensions[name] = value;
        return this;
    }

    public ApiProblem WithHeader(string name, string value)
    {
        Headers[name] = value;
        return this;
    }

    public ProblemDetails ToProblemDetails()
    {
        var problem = new ProblemDetails { Status = Status, Title = Title, Detail = Detail };
        problem.Extensions["code"] = Code;
        foreach (var (name, value) in Extensions) problem.Extensions[name] = value;
        return problem;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = Status;
        foreach (var (name, value) in Headers) httpContext.Response.Headers[name] = value;
        var service = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await service.WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = ToProblemDetails() });
    }
}

/// <summary>Carries an <see cref="ApiProblem"/> out of code that cannot return a result. Mapped to the response by the exception handler.</summary>
public sealed class ApiProblemException(ApiProblem problem) : Exception($"{problem.Code}: {problem.Detail}")
{
    public ApiProblem Problem { get; } = problem;
}

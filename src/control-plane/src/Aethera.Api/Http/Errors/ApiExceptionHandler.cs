using Aethera.Domain;
using Microsoft.AspNetCore.Diagnostics;

namespace Aethera.Api.Http.Errors;

/// <summary>
/// Maps every unhandled exception to a problem body in one place (ADR 0003): <see cref="ApiProblemException"/>, domain rule
/// violations, malformed requests and database conflicts get their own status; anything else is <c>500 internal.error</c>
/// without exception details (those only go to the log).
/// </summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
            return true; // the client went away; nobody is listening for a body

        var problem = exception switch
        {
            ApiProblemException api => api.Problem,
            DomainRuleException rule => new ApiProblem(StatusCodes.Status422UnprocessableEntity, ProblemCodes.DomainRuleViolation, rule.Message),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => new ApiProblem(
                StatusCodes.Status413PayloadTooLarge, ProblemCodes.RequestTooLarge, "The request body is too large."),
            BadHttpRequestException bad => new ApiProblem(
                bad.StatusCode, ProblemCodes.ForStatus(bad.StatusCode), "The request could not be parsed."),
            _ => DbErrorTranslator.Translate(exception),
        };

        if (problem is null)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path.Value);
            problem = ApiProblems.Internal();
        }
        else
        {
            logger.LogInformation("Request failed with {Code} ({Status}): {ExceptionType}", problem.Code, problem.Status, exception.GetType().Name);
        }

        await problem.ExecuteAsync(httpContext);
        return true;
    }
}

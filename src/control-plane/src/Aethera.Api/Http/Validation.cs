using System.Text;
using System.Text.Json;
using Aethera.Api.Http.Errors;
using FluentValidation;
using FluentValidation.Results;

namespace Aethera.Api.Http;

/// <summary>Where the validated value came from, which decides how failures are located in the response.</summary>
public enum ValidationSource
{
    /// <summary>JSON request body: failures carry a JSON Pointer (<c>/domains/0/host</c>).</summary>
    Body,

    /// <summary>Query/path parameters bound into an object (<c>[AsParameters]</c>): failures carry a <c>parameter</c> name.</summary>
    Query,
}

/// <summary>Endpoint metadata: the endpoint validates an argument of this type (adds the 422 response to OpenAPI).</summary>
public sealed record ValidationMetadata(Type ArgumentType);

public static class ValidationExtensions
{
    /// <summary>
    /// Validates the endpoint argument of type <typeparamref name="T"/> with the registered <see cref="IValidator{T}"/> and answers
    /// <c>422 validation.failed</c> with the ADR 0003 <c>errors[]</c> shape instead of calling the handler. Validators in the
    /// API assembly are registered automatically; a missing validator is a programming error and fails the request with 500.
    /// <code>
    /// group.MapPost("/", Create).WithName("createProject").Validate&lt;CreateProjectRequest&gt;();
    /// </code>
    /// </summary>
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder builder, ValidationSource source = ValidationSource.Body)
        where T : class
    {
        builder.WithMetadata(new ValidationMetadata(typeof(T)));
        builder.AddEndpointFilter(async (context, next) =>
        {
            var argument = context.Arguments.OfType<T>().FirstOrDefault();
            if (argument is null) return await next(context); // the binder already rejected a missing body

            var validator = context.HttpContext.RequestServices.GetService<IValidator<T>>()
                ?? throw new InvalidOperationException($"No IValidator<{typeof(T).Name}> is registered.");
            var result = await validator.ValidateAsync(argument, context.HttpContext.RequestAborted);
            return result.IsValid ? await next(context) : ValidationErrorMapper.ToProblem(result, source);
        });
        return builder;
    }
}

/// <summary>Turns FluentValidation failures into the ADR 0003 <c>errors[]</c> entries.</summary>
public static class ValidationErrorMapper
{
    public static ApiProblem ToProblem(ValidationResult result, ValidationSource source) =>
        ApiProblems.Validation(ToFieldErrors(result, source));

    public static IReadOnlyList<FieldError> ToFieldErrors(ValidationResult result, ValidationSource source) =>
        result.Errors
            .Select(f => source == ValidationSource.Body
                ? FieldError.AtPointer(ToPointer(f.PropertyName), ToCode(f), f.ErrorMessage)
                : FieldError.ForParameter(ToParameter(f.PropertyName), ToCode(f), f.ErrorMessage))
            .DistinctBy(e => (e.Pointer, e.Parameter, e.Code, e.Message))
            .ToList();

    /// <summary><c>Domains[0].Host</c> becomes <c>/domains/0/host</c>; an empty property name (object-level rule) is the whole body (<c>""</c>).</summary>
    public static string ToPointer(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return "";
        var pointer = new StringBuilder();
        foreach (var part in propertyName.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = part.IndexOf('[');
            var name = bracket < 0 ? part : part[..bracket];
            if (name.Length > 0) pointer.Append('/').Append(Escape(JsonNamingPolicy.CamelCase.ConvertName(name)));
            while (bracket >= 0)
            {
                var close = part.IndexOf(']', bracket);
                if (close < 0) break;
                pointer.Append('/').Append(Escape(part[(bracket + 1)..close]));
                bracket = part.IndexOf('[', close);
            }
        }

        return pointer.ToString();
    }

    private static string ToParameter(string propertyName) =>
        string.Join('.', propertyName.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => JsonNamingPolicy.CamelCase.ConvertName(p)));

    private static string Escape(string segment) => segment.Replace("~", "~0").Replace("/", "~1");

    /// <summary>
    /// Stable short validator ids. FluentValidation's built-in error codes are mapped to the ADR vocabulary
    /// (<c>required</c>, <c>too_long</c>, <c>too_short</c>, <c>pattern</c>, <c>range</c>, <c>invalid_enum</c>, <c>not_unique</c>); a custom
    /// <c>.WithErrorCode("domain.invalid_host")</c> is passed through unchanged.
    /// </summary>
    private static string LengthCode(ValidationFailure failure)
    {
        var values = failure.FormattedMessagePlaceholderValues;
        return values is not null
            && values.TryGetValue("TotalLength", out var total) && values.TryGetValue("MinLength", out var min)
            && total is int t && min is int m && t < m
                ? "too_short"
                : "too_long";
    }

    internal static string ToCode(ValidationFailure failure) => failure.ErrorCode switch
    {
        "NotEmptyValidator" or "NotNullValidator" => "required",
        "MaximumLengthValidator" or "ExactLengthValidator" => "too_long",
        "LengthValidator" => LengthCode(failure),
        "MinimumLengthValidator" => "too_short",
        "RegularExpressionValidator" => "pattern",
        "InclusiveBetweenValidator" or "ExclusiveBetweenValidator" or "GreaterThanValidator" or "GreaterThanOrEqualValidator"
            or "LessThanValidator" or "LessThanOrEqualValidator" => "range",
        "EnumValidator" => "invalid_enum",
        "EmailValidator" or "AspNetCoreCompatibleEmailValidator" => "email",
        "" or null => "invalid",
        var other => other.EndsWith("Validator", StringComparison.Ordinal) ? "invalid" : other,
    };
}

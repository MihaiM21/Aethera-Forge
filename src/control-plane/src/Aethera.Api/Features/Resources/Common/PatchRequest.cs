using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using FluentValidation;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Resources;

/// <summary>
/// A JSON Merge Patch (RFC 7396) body. <see cref="Body"/> is the deserialized patch document (absent properties keep their defaults,
/// which are null for every patch DTO), <see cref="Has"/> tells whether a property was <em>present</em> (so <c>null</c> can mean
/// "clear"). Paths are camelCase and dotted for nested objects (<c>runtime.healthCheck.path</c>). Bind it as an endpoint parameter and
/// declare the body type for OpenAPI with <c>.Accepts&lt;T&gt;("application/merge-patch+json", "application/json")</c>.
/// </summary>
public sealed class PatchRequest<T> : IBindableFromHttpContext<PatchRequest<T>> where T : class
{
    public PatchRequest(T body, JsonObject raw)
    {
        Body = body;
        Raw = raw;
    }

    public T Body { get; }

    public JsonObject Raw { get; }

    /// <summary>True when the property is in the document (with any value, including null).</summary>
    public bool Has(string path) => Find(path, out _);

    /// <summary>True when the property is in the document with an explicit null.</summary>
    public bool IsNull(string path) => Find(path, out var node) && node is null;

    /// <summary>The raw JSON of a property (null when absent or null).</summary>
    public JsonNode? Get(string path) => Find(path, out var node) ? node : null;

    /// <summary>Names of the properties of the document's root.</summary>
    public IEnumerable<string> Keys => Raw.Select(p => p.Key);

    private bool Find(string path, out JsonNode? node)
    {
        node = Raw;
        foreach (var segment in path.Split('.'))
        {
            if (node is not JsonObject obj) return false;
            var key = obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, segment, StringComparison.OrdinalIgnoreCase));
            if (key is null) return false;
            node = obj[key];
        }

        return true;
    }

    public static async ValueTask<PatchRequest<T>?> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        var request = context.Request;
        if (!request.HasJsonContentType())
            throw new ApiProblemException(new ApiProblem(StatusCodes.Status415UnsupportedMediaType, ProblemCodes.UnsupportedMediaType,
                "Send the patch as application/merge-patch+json (or application/json)."));

        var options = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        JsonNode? node;
        try
        {
            node = await JsonNode.ParseAsync(request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            throw new ApiProblemException(ApiProblems.Malformed("The request body is not valid JSON."));
        }

        if (node is not JsonObject raw)
            throw new ApiProblemException(ApiProblems.Malformed("A merge patch must be a JSON object."));

        try
        {
            var body = raw.Deserialize<T>(options) ?? throw new ApiProblemException(ApiProblems.Malformed());
            return new PatchRequest<T>(body, raw);
        }
        catch (JsonException ex)
        {
            throw new ApiProblemException(ApiProblems.Malformed("The patch document has a property of the wrong type" +
                (ex is { Path: { Length: > 0 } path } ? $" at {path}." : ".")));
        }
    }
}

public static class PatchRequestExtensions
{
    /// <summary>
    /// Validates the patch body with the registered <see cref="IValidator{T}"/> (422 with JSON Pointers). Validators of patch DTOs must be
    /// written as "if present, then valid" (<c>.When(x =&gt; x.Name is not null)</c>); a present <c>null</c> for a required property is
    /// rejected with <see cref="PatchRequest{T}"/>-aware checks in the handler (<see cref="Required"/>).
    /// </summary>
    public static RouteHandlerBuilder ValidatePatch<T>(this RouteHandlerBuilder builder) where T : class
    {
        builder.WithMetadata(new ValidationMetadata(typeof(T)));
        builder.AddEndpointFilter(async (context, next) =>
        {
            var patch = context.Arguments.OfType<PatchRequest<T>>().FirstOrDefault();
            if (patch is null) return await next(context);
            var validator = context.HttpContext.RequestServices.GetRequiredService<IValidator<T>>();
            var result = await validator.ValidateAsync(patch.Body, context.HttpContext.RequestAborted);
            var errors = ValidationErrorMapper.ToFieldErrors(result, ValidationSource.Body).ToList();
            foreach (var name in patch.Keys)
            {
                if (patch.IsNull(name) && NonNullable(typeof(T), name))
                    errors.Add(FieldError.AtPointer("/" + name, "required", "Cannot be null."));
            }

            return errors.Count == 0 ? await next(context) : ApiProblems.Validation(errors);
        });
        return builder;
    }

    /// <summary>Throws 422 <c>required</c> for each listed property that the patch sets to null.</summary>
    public static void Required<T>(this PatchRequest<T> patch, params string[] paths) where T : class
    {
        var errors = paths.Where(patch.IsNull)
            .Select(p => FieldError.AtPointer("/" + p.Replace('.', '/'), "required", "Cannot be null."))
            .ToList();
        if (errors.Count > 0) throw new ApiProblemException(ApiProblems.Validation(errors));
    }

    // Properties marked [NonNullablePatch] cannot be cleared.
    private static bool NonNullable(Type type, string jsonName)
    {
        var property = type.GetProperties().FirstOrDefault(p => string.Equals(p.Name, jsonName, StringComparison.OrdinalIgnoreCase));
        return property?.GetCustomAttribute<NotClearableAttribute>() is not null;
    }
}

/// <summary>Marks a patch DTO property that must not be set to null (the value is required on the resource).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotClearableAttribute : Attribute;

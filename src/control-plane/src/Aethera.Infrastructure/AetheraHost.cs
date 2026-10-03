using System.Reflection;

namespace Aethera.Infrastructure;

/// <summary>Facts about the process hosting Aethera.</summary>
public static class AetheraHost
{
    /// <summary>The entry assembly <c>Microsoft.Extensions.ApiDescription.Server</c> runs the application under while it writes the OpenAPI document.</summary>
    public const string OpenApiGeneratorAssemblyName = "GetDocument.Insider";

    /// <summary>
    /// True when the application was started by the build-time OpenAPI generator (<c>dotnet build</c> -> <c>GetDocument.Insider</c>). That
    /// host builds the app and stops it again, without configuration: no database, Redis or master key exists, so hosted services (job
    /// workers, log ingestion, event relays) and start-up checks (master key, static web root) must do nothing under it.
    /// </summary>
    public static bool IsOpenApiGeneration => IsOpenApiGenerator(Assembly.GetEntryAssembly()?.GetName().Name);

    /// <summary>The detection rule, separated from the process so it can be tested.</summary>
    public static bool IsOpenApiGenerator(string? entryAssemblyName) =>
        string.Equals(entryAssemblyName, OpenApiGeneratorAssemblyName, StringComparison.Ordinal);
}

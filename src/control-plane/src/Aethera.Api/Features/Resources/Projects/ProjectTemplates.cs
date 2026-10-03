using Aethera.Domain;

namespace Aethera.Api.Features.Resources.Projects;

public sealed record ProjectTemplateEnvironment(string Name, string Slug, bool IsProduction);

/// <summary>
/// A placeholder workload a template creates. <c>Kind</c> is <c>application</c> (an unconfigured application of <c>SourceKind</c>, to be
/// pointed at a repository or image later) or <c>service</c> (a real service created from <c>ServiceTemplateKey</c>, with generated secrets).
/// </summary>
public sealed record ProjectTemplateWorkload(
    string Kind, string Name, string Slug, string? ServiceTemplateKey, ApplicationSourceKind? SourceKind, int? Port, bool IsHttp);

public sealed record ProjectTemplate(
    string Key, string Name, string Description, IReadOnlyList<ProjectTemplateEnvironment> Environments,
    IReadOnlyList<ProjectTemplateWorkload> Workloads);

/// <summary>The code-defined catalogue of project templates (<c>GET /project-templates</c>).</summary>
public static class ProjectTemplates
{
    private static readonly IReadOnlyList<ProjectTemplateEnvironment> Production = [new("Production", "production", true)];

    public static IReadOnlyList<ProjectTemplate> All { get; } =
    [
        new("empty", "Empty", "An empty project with a production environment.", Production, []),
        new("web-app", "Web App", "A frontend, a backend API, PostgreSQL and Redis.", Production,
        [
            App("Frontend", "frontend", ApplicationSourceKind.Git, 3000),
            App("Backend", "backend", ApplicationSourceKind.Git, 8080),
            Service("PostgreSQL", "postgres", "postgres"),
            Service("Redis", "redis", "redis"),
        ]),
        new("api", "API", "A backend API with a PostgreSQL database.", Production,
        [
            App("API", "api", ApplicationSourceKind.Git, 8080),
            Service("PostgreSQL", "postgres", "postgres"),
        ]),
        new("storage", "Storage", "PostgreSQL and MinIO object storage.", Production,
        [
            Service("PostgreSQL", "postgres", "postgres"),
            Service("MinIO", "minio", "minio"),
        ]),
        new("static-site", "Static site", "A static website built from a repository.", Production,
        [
            App("Website", "website", ApplicationSourceKind.Static, 80),
        ]),
    ];

    public static ProjectTemplate? Find(string? key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    private static ProjectTemplateWorkload App(string name, string slug, ApplicationSourceKind kind, int port) =>
        new("application", name, slug, null, kind, port, true);

    private static ProjectTemplateWorkload Service(string name, string slug, string serviceTemplateKey) =>
        new("service", name, slug, serviceTemplateKey, null, null, false);
}

namespace Aethera.Domain;

public class Project : SoftDeletableEntity
{
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? Description { get; set; }

    /// <summary>Key of the project template used to create the project (e.g. <c>web-app</c>), if any.</summary>
    public string? TemplateKey { get; set; }

    public List<ProjectEnvironment> Environments { get; set; } = [];
}

/// <summary>A deployment target grouping inside a project (development, staging, production, ...).</summary>
public class ProjectEnvironment : SoftDeletableEntity
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? Description { get; set; }

    /// <summary>Drives extra confirmation for destructive actions in the UI/API.</summary>
    public bool IsProduction { get; set; }

    public List<Workload> Workloads { get; set; } = [];
}

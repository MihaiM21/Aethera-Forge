namespace Aethera.Domain;

/// <summary>Top-level tenant. A single-user installation has exactly one (the default) organization.</summary>
public class Organization : MutableEntity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
}

namespace Aethera.Domain;

/// <summary>Top-level tenant. A single-user installation has exactly one (the default) organization.</summary>
public class Organization : MutableEntity
{
    public required string Name { get; set; }
    public required string Slug { get; set; }

    /// <summary>
    /// When true every member must use two-factor authentication to sign in to the UI (ADR 0013). A member without an authenticator can then only
    /// enrol one until they have it. API tokens are not affected.
    /// </summary>
    public bool RequireTwoFactor { get; set; }
}

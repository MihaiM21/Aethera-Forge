namespace Aethera.Api.Security;

/// <summary>
/// API token scopes. Scopes only ever <em>narrow</em> a token below its owner's role: effective permission is the scope check
/// AND the role check, and a browser session is bound by role only.
/// </summary>
public static class Scopes
{
    /// <summary>Read anything the role may read, except secrets.</summary>
    public const string Read = "read";

    /// <summary>Create, change and delete resources (implies <see cref="Read"/>), except secrets and servers.</summary>
    public const string Write = "write";

    /// <summary>Deploy, redeploy, rollback, restart, start, stop; cancel and retry jobs.</summary>
    public const string Deploy = "deploy";

    /// <summary>List secrets (names and metadata, never values).</summary>
    public const string SecretsRead = "secrets:read";

    /// <summary>Create, rotate and delete secrets, reveal values (implies <see cref="SecretsRead"/>).</summary>
    public const string SecretsWrite = "secrets:write";

    /// <summary>Create, change and delete servers, join tokens, agent install, prune.</summary>
    public const string ServersWrite = "servers:write";

    /// <summary>Users, roles, other people's tokens, instance settings, audit log. Also satisfies every other scope.</summary>
    public const string Admin = "admin";

    /// <summary>Wildcard, as issued by the UI for "full access" tokens. Satisfies every scope.</summary>
    public const string All = "*";

    public static IReadOnlyList<string> Known { get; } =
        [Read, Write, Deploy, SecretsRead, SecretsWrite, ServersWrite, Admin];

    /// <summary>True for a scope an API token may be created with (<see cref="Known"/> or <see cref="All"/>).</summary>
    public static bool IsValid(string scope) => scope == All || Known.Contains(scope);

    /// <summary>
    /// True when <paramref name="granted"/> satisfies <paramref name="required"/>: an exact match, the wildcard or
    /// <see cref="Admin"/>, or a broader scope (<c>write</c> covers <c>read</c>, <c>secrets:write</c> covers <c>secrets:read</c>).
    /// </summary>
    public static bool Satisfies(IReadOnlySet<string> granted, string required)
    {
        if (granted.Contains(required) || granted.Contains(All) || granted.Contains(Admin)) return true;
        return required switch
        {
            Read => granted.Contains(Write),
            SecretsRead => granted.Contains(SecretsWrite),
            _ => false,
        };
    }
}

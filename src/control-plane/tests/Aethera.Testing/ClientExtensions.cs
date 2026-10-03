using Aethera.Domain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Aethera.Testing;

public static class ClientExtensions
{
    /// <summary>
    /// A client authenticated (by <see cref="TestAuthHandler"/>) as a user with <paramref name="role"/>.
    /// <list type="bullet">
    /// <item><c>scopes: null</c> (default): a browser <b>session</b>: bound by role only, scope requirements do not apply.</item>
    /// <item><c>scopes: [...]</c>: an <b>API token</b> with exactly those scopes (an empty array is a token with no scopes).</item>
    /// </list>
    /// Pass <paramref name="identity"/> from <see cref="TestSeeder.SeedIdentityAsync(WebApplicationFactory{Program}, OrganizationRole)"/> when the
    /// code under test needs a real organization/user row (audit log, foreign keys); otherwise random ids are used.
    /// </summary>
    public static HttpClient CreateClientAs(
        this WebApplicationFactory<Program> factory,
        OrganizationRole role,
        IEnumerable<string>? scopes = null,
        SeededIdentity? identity = null,
        Guid? tokenId = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role.ToString());
        if (identity is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, identity.UserId.ToString());
            client.DefaultRequestHeaders.Add(TestAuthHandler.OrganizationHeader, identity.OrganizationId.ToString());
        }

        if (scopes is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.TokenHeader, (tokenId ?? Guid.NewGuid()).ToString());
            client.DefaultRequestHeaders.Add(TestAuthHandler.ScopesHeader, string.Join(',', scopes));
        }

        return client;
    }

    /// <summary>A client with no credentials.</summary>
    public static HttpClient CreateAnonymousClient(this WebApplicationFactory<Program> factory) => factory.CreateClient();
}

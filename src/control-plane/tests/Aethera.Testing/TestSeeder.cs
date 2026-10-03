using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Testing;

/// <summary>An organization with one user who is a member of it, as stored in the test database.</summary>
public sealed record SeededIdentity(Guid OrganizationId, Guid UserId, OrganizationRole Role);

public static class TestSeeder
{
    /// <summary>
    /// Inserts an organization, a user and the membership with <paramref name="role"/>. Names and emails are unique, so tests sharing a
    /// database never collide. Requires <c>AETHERA_TEST_DB</c>.
    /// </summary>
    public static async Task<SeededIdentity> SeedIdentityAsync(
        this WebApplicationFactory<Program> factory, OrganizationRole role = OrganizationRole.Owner)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        return await SeedIdentityAsync(db, role);
    }

    public static async Task<SeededIdentity> SeedIdentityAsync(AetheraDbContext db, OrganizationRole role = OrganizationRole.Owner)
    {
        var unique = Guid.NewGuid().ToString("N")[..12];
        var organization = new Organization { Name = "Org " + unique, Slug = "org-" + unique };
        var user = new User($"user-{unique}@example.com", "User " + unique);
        var member = new OrganizationMember { Organization = organization, User = user, Role = role };
        db.AddRange(organization, user, member);
        await db.SaveChangesAsync();
        return new SeededIdentity(organization.Id, user.Id, role);
    }
}

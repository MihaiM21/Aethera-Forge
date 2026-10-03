using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests;

/// <summary>
/// The <c>AddSecretPurpose</c> migration backfills the purpose of existing secrets from their ownership links (registry, server, git credential)
/// and from the way the registry endpoints and service templates name and describe the secrets they create.
/// </summary>
public sealed class SecretPurposeMigrationTests
{
    private const string Before = "20261003162628_AddJobOrganization";

    [RequiresDatabaseFact]
    public async Task Backfill_ClassifiesOwnedSecrets_AndLeavesEverythingElseAUserSecret()
    {
        await using var database = await TestDatabase.CreateAsync();
        Assert.NotNull(database);
        await using var services = TestDatabaseFixture.BuildServices(database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();

        // Rows as the code before this migration wrote them: every secret is simply "a secret".
        var org = new Organization { Name = "O", Slug = "o" };
        var project = new Project { Organization = org, Name = "P", Slug = "p" };
        var environment = new ProjectEnvironment { Project = project, Name = "prod", Slug = "prod" };
        var sshSecret = Secret(org, "deploy-key", "key");
        var server = new Server { OrganizationId = org.Id, Name = "s", Host = "s.example.com", SshCredentialSecretId = sshSecret.Id };
        var service = new Service { EnvironmentId = environment.Id, ServerId = server.Id, Name = "cache", Slug = "cache", TemplateKey = "redis", Image = "redis:7" };

        var linkedRegistrySecret = Secret(org, "registry/0123456789ab", "Credentials of registry 'ghcr'.");
        var unlinkedRegistrySecret = Secret(org, "registry/ffffffffffff", "from a removed link");
        var scopedLookalike = Secret(org, "registry/not-org-scoped", null, projectId: project.Id);
        var gitSecret = Secret(org, "github-token", "token");
        var generated = Secret(org, "REDIS_PASSWORD", "Generated for service 'cache'.", workloadId: service.Id);
        var generatedButUnused = Secret(org, "OTHER", "Generated for service 'cache'.", workloadId: service.Id);
        var scopedUser = Secret(org, "MINE", "my own", workloadId: service.Id);
        var plainOrg = Secret(org, "API_KEY", "mine");
        var plainProject = Secret(org, "PROJECT_KEY", null, projectId: project.Id);
        var registrySecretOfDeletedRegistry = Secret(org, "registry/aaaaaaaaaaaa", "gone");
        registrySecretOfDeletedRegistry.MarkDeleted(DateTimeOffset.UtcNow);

        db.AddRange(org, project, environment, sshSecret, server, service, linkedRegistrySecret, unlinkedRegistrySecret, scopedLookalike, gitSecret, generated,
            generatedButUnused, scopedUser, plainOrg, plainProject, registrySecretOfDeletedRegistry);
        db.Registries.Add(new Registry { OrganizationId = org.Id, Name = "ghcr", Url = "ghcr.io", PasswordSecretId = linkedRegistrySecret.Id });
        db.GitCredentials.Add(new GitCredential { OrganizationId = org.Id, Name = "gh", SecretId = gitSecret.Id });
        db.EnvironmentVariables.Add(new EnvironmentVariable { WorkloadId = service.Id, Key = "REDIS_PASSWORD", SecretId = generated.Id });
        await db.SaveChangesAsync();

        // Take the column away again (the migration's Down), then apply the migration to this data.
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);
        await migrator.MigrateAsync();

        db.ChangeTracker.Clear();
        var purposes = await db.Secrets.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(s => s.Name, s => s.Purpose);
        Assert.Equal(SecretPurpose.RegistryCredential, purposes["registry/0123456789ab"]); // linked from a registry
        Assert.Equal(SecretPurpose.RegistryCredential, purposes["registry/ffffffffffff"]); // organization-scoped, named like one
        Assert.Equal(SecretPurpose.RegistryCredential, purposes["registry/aaaaaaaaaaaa"]); // even when soft-deleted
        Assert.Equal(SecretPurpose.User, purposes["registry/not-org-scoped"]); // the naming rule needs organization scope
        Assert.Equal(SecretPurpose.SshCredential, purposes["deploy-key"]);
        Assert.Equal(SecretPurpose.GitCredential, purposes["github-token"]);
        Assert.Equal(SecretPurpose.ServiceGenerated, purposes["REDIS_PASSWORD"]);
        Assert.Equal(SecretPurpose.User, purposes["OTHER"]); // described as generated, but no variable of the service uses it
        Assert.Equal(SecretPurpose.User, purposes["MINE"]);
        Assert.Equal(SecretPurpose.User, purposes["API_KEY"]);
        Assert.Equal(SecretPurpose.User, purposes["PROJECT_KEY"]);

        // The column is stored as text with a default and a check constraint; a value outside the set is refused by the database.
        await using var connection = new Npgsql.NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var invalid = new Npgsql.NpgsqlCommand("UPDATE secrets SET purpose = 'root' WHERE name = 'API_KEY'", connection);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => invalid.ExecuteNonQueryAsync());
        Assert.Equal("ck_secrets_purpose", error.ConstraintName);
    }

    private static Secret Secret(Organization org, string name, string? description, Guid? projectId = null, Guid? workloadId = null) =>
        new() { Organization = org, Name = name, Description = description, ProjectId = projectId, WorkloadId = workloadId };
}

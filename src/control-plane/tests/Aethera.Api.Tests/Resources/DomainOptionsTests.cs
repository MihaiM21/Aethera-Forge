using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Features.Resources.DomainNames;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Resources;

/// <summary>bcrypt: the reference vectors of crypt_blowfish and Apache's htpasswd -B (so a Traefik reading the lines accepts them).</summary>
public class BcryptTests
{
    [Theory]
    [InlineData("U*U", "$2a$05$CCCCCCCCCCCCCCCCCCCCC.E5YPO9kmyuRGyh0XouQYb4YMJKvyOeW")]
    [InlineData("U*U*", "$2a$05$CCCCCCCCCCCCCCCCCCCCC.VGOzA784oUp/Z0DY336zx7pLYAy0lwK")]
    [InlineData("U*U*U", "$2a$05$XXXXXXXXXXXXXXXXXXXXXOAcXxm9kjPGEMsLznoKqmqw7tc8WCx4a")]
    [InlineData("", "$2a$05$CCCCCCCCCCCCCCCCCCCCC.7uG0VCzI2bS7j6ymqJi9CdcdxiRTWNy")]
    public void Verifies_the_reference_vectors(string password, string hash) => Assert.True(Bcrypt.Verify(password, hash));

    [Theory]
    [InlineData("correct horse", "$2y$04$6glR7u8jjqGodonIblz9GehPbEDf0kQJrJjVcoj4NFoovFXS/yYme")]
    public void Verifies_hashes_made_by_apache_htpasswd(string password, string hash) => Assert.True(Bcrypt.Verify(password, hash));

    [Fact]
    public void Hashes_have_the_modular_crypt_shape_and_verify_only_the_right_password()
    {
        var hash = Bcrypt.Hash("correct horse", cost: 6);
        Assert.Matches(@"^\$2y\$06\$[./A-Za-z0-9]{53}$", hash);
        Assert.Equal(60, hash.Length);
        Assert.True(Bcrypt.Verify("correct horse", hash));
        Assert.False(Bcrypt.Verify("correct horsf", hash));
        Assert.NotEqual(hash, Bcrypt.Hash("correct horse", cost: 6)); // random salt
        Assert.True(Aethera.Engine.Proxy.TraefikProxyProvider.IsValidHtpasswdLine("alice:" + hash));
    }

    [Fact]
    public void Rejects_malformed_hashes() =>
        Assert.All(new[] { "", "plain", "$2y$99$" + new string('a', 53), "$1$abc$" + new string('a', 40) }, h => Assert.False(Bcrypt.Verify("x", h)));
}

/// <summary>The domain options of ADR 0010 through the API: compose services, redirects, strip prefix, basic auth, DNS provider, custom labels.</summary>
[Collection(ResourcesCollection.Name)]
public sealed class DomainOptionsTests(ResourcesFixture fixture)
{
    private static string Host(string label = "app") => $"{label}-{Guid.NewGuid().ToString("N")[..8]}.example.com";

    private static async Task<string> ComposeAppAsync(Tenant tenant, string serverId, string environmentId)
    {
        var app = await tenant.Developer.CreateAsync("/api/v1/applications", new
        {
            name = Tenant.Unique("compose"), environmentId, serverId, sourceKind = "compose",
            gitSource = new { repositoryUrl = "https://github.com/acme/turnone.git", branch = "main" },
            compose = new { filePath = "deploy/docker-compose.yml" },
        });
        return app.Id();
    }

    // ---- compose service -------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_compose_applications_domain_needs_its_service_and_other_workloads_refuse_one()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, environmentId, _, plainApp) = await tenant.CreateStackAsync();
        var composeApp = await ComposeAppAsync(tenant, serverId, environmentId);

        Assert.Contains(("/composeService", "required"),
            await (await tenant.Developer.PostAsync($"/api/v1/applications/{composeApp}/domains", new { hostname = Host() })).ValidationErrorsAsync());
        Assert.Contains(("/composeService", "pattern"),
            await (await tenant.Developer.PostAsync($"/api/v1/applications/{composeApp}/domains", new { hostname = Host(), composeService = "web`) || Host(`x" })).ValidationErrorsAsync());
        Assert.Contains(("/composeService", "not_applicable"),
            await (await tenant.Developer.PostAsync($"/api/v1/applications/{plainApp}/domains", new { hostname = Host(), composeService = "web" })).ValidationErrorsAsync());

        var domain = await tenant.Developer.CreateAsync($"/api/v1/applications/{composeApp}/domains", new { hostname = Host(), composeService = "web", targetPort = 3000 });
        Assert.Equal("web", domain["composeService"]!.GetValue<string>());

        var moved = await tenant.Developer.PatchOkAsync($"/api/v1/domains/{domain.Id()}", new { composeService = "api" });
        Assert.Equal("api", moved["composeService"]!.GetValue<string>());
        Assert.Contains(("/composeService", "required"), await (await tenant.Developer.PatchAsync($"/api/v1/domains/{domain.Id()}", new { composeService = (string?)null })).ValidationErrorsAsync());
    }

    // ---- redirect, strip prefix ------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Redirect_and_strip_prefix_are_stored_validated_and_returned()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var target = Host("target");

        var created = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host("www"), redirectTo = target.ToUpperInvariant() + "." });
        Assert.Equal(target, created["redirectTo"]!.GetValue<string>());
        Assert.False(created["stripPathPrefix"]!.GetValue<bool>());

        var strip = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host("api"), pathPrefix = "/api", stripPathPrefix = true });
        Assert.True(strip["stripPathPrefix"]!.GetValue<bool>());
        var off = await tenant.Developer.PatchOkAsync($"/api/v1/domains/{strip.Id()}", new { stripPathPrefix = false });
        Assert.False(off["stripPathPrefix"]!.GetValue<bool>());
        var cleared = await tenant.Developer.PatchOkAsync($"/api/v1/domains/{created.Id()}", new { redirectTo = (string?)null });
        Assert.Null(cleared["redirectTo"]);

        foreach (var bad in new[] { "*.example.com", "a b.com", "http://example.com", "example.com/x", "evil.com`) || Host(`x", "192.168.1.1" })
            Assert.Contains(("/redirectTo", "domain.invalid_host"),
                await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = Host(), redirectTo = bad })).ValidationErrorsAsync());

        var self = Host("self");
        Assert.Contains(("/redirectTo", "invalid"), await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = self, redirectTo = self })).ValidationErrorsAsync());
        var errors = await (await tenant.Developer.PostAsync("/api/v1/domains", new
        {
            workloadId = appId, hostname = Host(), redirectTo = Host("t"), stripPathPrefix = true, basicAuth = new[] { new { username = "a", password = "longenough" } },
        })).ValidationErrorsAsync();
        Assert.Contains(("/basicAuth", "not_applicable"), errors);
        Assert.Contains(("/stripPathPrefix", "not_applicable"), errors);
    }

    // ---- DNS provider, wildcard ------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_wildcard_host_needs_a_dns_provider_of_the_organization()
    {
        var tenant = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var wildcard = $"*.wild-{Guid.NewGuid().ToString("N")[..6]}.example.com";

        Assert.Contains(("/dnsProviderId", "domain.wildcard_needs_dns_provider"),
            await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = wildcard })).ValidationErrorsAsync());
        Assert.Contains(("/dnsProviderId", "not_found"),
            await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = wildcard, dnsProviderId = Guid.NewGuid() })).ValidationErrorsAsync());
        var foreign = await other.SeedDnsProviderAsync();
        Assert.Contains(("/dnsProviderId", "not_found"),
            await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = wildcard, dnsProviderId = foreign })).ValidationErrorsAsync());

        var provider = await tenant.SeedDnsProviderAsync();
        var domain = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = wildcard, dnsProviderId = provider });
        Assert.Equal(provider, domain["dnsProviderId"]!.GetValue<string>());

        // The provider cannot be removed from a wildcard, nor can a plain host become a wildcard without one.
        Assert.Contains(("/dnsProviderId", "domain.wildcard_needs_dns_provider"),
            await (await tenant.Developer.PatchAsync($"/api/v1/domains/{domain.Id()}", new { dnsProviderId = (string?)null })).ValidationErrorsAsync());
        var plain = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host(), dnsProviderId = provider });
        Assert.Contains(("/dnsProviderId", "domain.wildcard_needs_dns_provider"),
            await (await tenant.Developer.PatchAsync($"/api/v1/domains/{plain.Id()}", new { hostname = wildcard.Replace("wild-", "other-"), dnsProviderId = (string?)null })).ValidationErrorsAsync());
        var cleared = await tenant.Developer.PatchOkAsync($"/api/v1/domains/{plain.Id()}", new { dnsProviderId = (string?)null });
        Assert.Null(cleared["dnsProviderId"]);
    }

    // ---- basic auth ------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Basic_auth_is_hashed_stored_as_a_managed_secret_and_never_returned()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();

        var response = await tenant.Developer.PostAsync("/api/v1/domains", new
        {
            workloadId = appId, hostname = Host(), basicAuth = new[] { new { username = "alice", password = "correct horse" }, new { username = "bob.b@x", password = "battery staple" } },
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain("correct horse", text);
        Assert.DoesNotContain("$2y$", text);
        var domain = JsonNode.Parse(text)!;
        Assert.Equal(["alice", "bob.b@x"], domain["basicAuthUsers"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Null(domain["basicAuth"]);

        // The lines are bcrypt htpasswd lines in a managed secret.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
            var vault = scope.ServiceProvider.GetRequiredService<SecretVault>();
            var entity = await db.Domains.AsNoTracking().FirstAsync(d => d.Id == Guid.Parse(domain.Id()));
            var secret = await db.Secrets.AsNoTracking().FirstAsync(s => s.Id == entity.BasicAuthSecretId);
            Assert.Equal(SecretPurpose.BasicAuth, secret.Purpose);
            var (_, value) = await vault.RevealAsync(secret, null, default);
            var lines = value.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.True(Bcrypt.Verify("correct horse", lines[0]["alice:".Length..]));
            Assert.True(Bcrypt.Verify("battery staple", lines[1]["bob.b@x:".Length..]));
            Assert.All(lines, l => Assert.True(Aethera.Engine.Proxy.TraefikProxyProvider.IsValidHtpasswdLine(l)));
        }

        // Reads and lists show the names only.
        var got = await tenant.Viewer.GetJsonAsync($"/api/v1/domains/{domain.Id()}");
        Assert.Equal(2, got["basicAuthUsers"]!.AsArray().Count);
        var listText = (await tenant.Viewer.GetAsync($"/api/v1/applications/{appId}/domains")).Content.ReadAsStringAsync().Result;
        Assert.DoesNotContain("$2y$", listText);
        Assert.DoesNotContain("correct horse", listText);

        // The managed secret cannot be reached through /secrets (listed without value; not editable).
        var secrets = await tenant.Admin.GetJsonAsync("/api/v1/secrets?limit=200");
        var managed = secrets["items"]!.AsArray().Single(s => s!["purpose"]?.GetValue<string>() == "basicAuth");
        Assert.Equal("domain", managed!["managedBy"]!["type"]!.GetValue<string>());
        Assert.DoesNotContain("$2y$", secrets.ToJsonString());
    }

    [RequiresDatabaseFact]
    public async Task Basic_auth_is_replaced_by_a_patch_and_removed_by_an_empty_list()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var domain = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host(), basicAuth = new[] { new { username = "alice", password = "correct horse" } } });

        var replaced = await tenant.Developer.PatchOkAsync($"/api/v1/domains/{domain.Id()}", new { basicAuth = new[] { new { username = "carol", password = "another pass" } } });
        Assert.Equal(["carol"], replaced["basicAuthUsers"]!.AsArray().Select(n => n!.GetValue<string>()));
        var untouched = await tenant.Developer.PatchOkAsync($"/api/v1/domains/{domain.Id()}", new { httpsEnabled = false });
        Assert.Equal(["carol"], untouched["basicAuthUsers"]!.AsArray().Select(n => n!.GetValue<string>()));

        var removed = await tenant.Developer.PatchOkAsync($"/api/v1/domains/{domain.Id()}", new { basicAuth = Array.Empty<object>() });
        Assert.Empty(removed["basicAuthUsers"]!.AsArray());
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        Assert.Null((await db.Domains.AsNoTracking().FirstAsync(d => d.Id == Guid.Parse(domain.Id()))).BasicAuthSecretId);
    }

    [RequiresDatabaseFact]
    public async Task Basic_auth_input_is_validated()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        async Task<IReadOnlyList<(string? Location, string Code)>> Errors(object users) =>
            await (await tenant.Developer.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = Host(), basicAuth = users })).ValidationErrorsAsync();

        Assert.Contains(("/basicAuth/0/password", "pattern"), await Errors(new[] { new { username = "alice", password = "short" } }));
        Assert.Contains(("/basicAuth/0/password", "pattern"), await Errors(new[] { new { username = "alice", password = new string('x', 73) } }));
        Assert.Contains(("/basicAuth/0/username", "pattern"), await Errors(new[] { new { username = "al:ice", password = "long enough" } }));
        Assert.Contains(("/basicAuth/0/username", "pattern"), await Errors(new[] { new { username = "al ice", password = "long enough" } }));
        Assert.Contains(("/basicAuth/0/username", "required"), await Errors(new[] { new { username = "", password = "long enough" } }));
        Assert.Contains(("/basicAuth", "duplicate"), await Errors(new[] { new { username = "a", password = "long enough" }, new { username = "a", password = "long enough 2" } }));
        Assert.Contains(("/basicAuth", "too_long"), await Errors(Enumerable.Range(0, 21).Select(i => new { username = $"u{i}", password = "long enough" }).ToArray()));
    }

    [RequiresDatabaseFact]
    public async Task Deleting_a_domain_removes_its_basic_auth_secret()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var domain = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host(), basicAuth = new[] { new { username = "a", password = "long enough" } } });
        Guid secretId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            secretId = (await scope.ServiceProvider.GetRequiredService<AetheraDbContext>().Domains.AsNoTracking().FirstAsync(d => d.Id == Guid.Parse(domain.Id()))).BasicAuthSecretId!.Value;

        Assert.Equal(HttpStatusCode.NoContent, (await tenant.Developer.DeleteAsync($"/api/v1/domains/{domain.Id()}")).StatusCode);
        await using var check = fixture.Factory.Services.CreateAsyncScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<AetheraDbContext>().Secrets.AnyAsync(s => s.Id == secretId));
    }

    [RequiresDatabaseFact]
    public async Task A_token_without_secrets_write_cannot_set_basic_auth()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var token = tenant.Token(OrganizationRole.Developer, "write");
        var response = await token.PostAsync("/api/v1/domains", new { workloadId = appId, hostname = Host(), basicAuth = new[] { new { username = "a", password = "long enough" } } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // Without basic auth the same token works.
        await token.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host() });
    }

    // ---- warnings --------------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Https_domains_warn_when_no_acme_email_is_configured()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var https = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host() });
        var http = await tenant.Developer.CreateAsync("/api/v1/domains", new { workloadId = appId, hostname = Host(), httpsEnabled = false });

        var warning = Assert.Single(https["warnings"]!.AsArray())!.GetValue<string>();
        Assert.Contains("No ACME email configured", warning);
        Assert.Contains("--acme-email", warning);
        Assert.Contains("Aethera:Proxy:AcmeEmail", warning);
        Assert.Empty(http["warnings"]!.AsArray());
    }

    // ---- custom labels ---------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task Custom_labels_are_administrator_only_keep_their_case_and_refuse_protected_keys()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var labels = new Dictionary<string, string>
        {
            ["traefik.http.middlewares.cors.headers.accessControlAllowOriginList"] = "https://x.example.com",
            ["traefik.http.routers.Custom.rule"] = "Host(`x.example.com`)",
        };

        await (await tenant.Developer.PatchAsync($"/api/v1/applications/{appId}", new { customLabels = labels })).AssertProblemAsync(403, "labels.requires_admin");
        var patched = await tenant.Admin.PatchOkAsync($"/api/v1/applications/{appId}", new { customLabels = labels });
        Assert.Equal("https://x.example.com", patched["customLabels"]!["traefik.http.middlewares.cors.headers.accessControlAllowOriginList"]!.GetValue<string>());
        Assert.NotNull(patched["customLabels"]!["traefik.http.routers.Custom.rule"]);

        // Reads are open to everyone, an unchanged map passes for a Developer, anything else does not.
        Assert.NotNull((await tenant.Viewer.GetJsonAsync($"/api/v1/applications/{appId}"))["customLabels"]!["traefik.http.routers.Custom.rule"]);
        await tenant.Developer.PatchOkAsync($"/api/v1/applications/{appId}", new { customLabels = labels });
        await tenant.Developer.PatchOkAsync($"/api/v1/applications/{appId}", new { description = "d" });
        await (await tenant.Developer.PatchAsync($"/api/v1/applications/{appId}", new { customLabels = new { a = "b" } })).AssertProblemAsync(403, "labels.requires_admin");

        foreach (var bad in new Dictionary<string, string>[]
                 {
                     new() { ["aethera.workload"] = "x" }, new() { ["traefik.docker.network"] = "host" }, new() { ["Aethera.managed"] = "false" },
                     new() { ["a b"] = "x" }, new() { ["a"] = "multi\nline" }, new() { ["a"] = new string('x', 2049) },
                 })
        {
            var errors = await (await tenant.Admin.PatchAsync($"/api/v1/applications/{appId}", new { customLabels = bad })).ValidationErrorsAsync();
            Assert.Contains(errors, e => e.Location == "/customLabels");
        }

        var cleared = await tenant.Admin.PatchOkAsync($"/api/v1/applications/{appId}", new { customLabels = (object?)null });
        Assert.Empty(cleared["customLabels"]!.AsObject());
    }

    // ---- compose source --------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_compose_application_can_switch_between_a_repository_file_and_pasted_content()
    {
        var tenant = await fixture.NewTenantAsync();
        var (serverId, environmentId, _, _) = await tenant.CreateStackAsync();
        var appId = await ComposeAppAsync(tenant, serverId, environmentId);

        var app = await tenant.Developer.GetJsonAsync($"/api/v1/applications/{appId}");
        Assert.Equal("deploy/docker-compose.yml", app["compose"]!["filePath"]!.GetValue<string>());

        var inline = await tenant.Developer.PatchOkAsync($"/api/v1/applications/{appId}", new { compose = new { filePath = (string?)null, inlineContent = "services:\n  web:\n    image: nginx\n" } });
        Assert.Null(inline["compose"]!["filePath"]);
        var back = await tenant.Developer.PatchOkAsync($"/api/v1/applications/{appId}", new { compose = new { filePath = "docker-compose.prod.yml", inlineContent = (string?)null } });
        Assert.Equal("docker-compose.prod.yml", back["compose"]!["filePath"]!.GetValue<string>());
        Assert.Null(back["compose"]!["inlineContent"]);

        Assert.Contains(("/compose/filePath", "pattern"),
            await (await tenant.Developer.PatchAsync($"/api/v1/applications/{appId}", new { compose = new { filePath = "../outside.yml" } })).ValidationErrorsAsync());
    }
}

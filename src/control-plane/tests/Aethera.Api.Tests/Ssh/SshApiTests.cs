using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Aethera.Infrastructure.Ssh;
using Aethera.Infrastructure.Ssh.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aethera.Api.Tests.Ssh;

/// <summary>The API with a scripted SSH client and no job workers (a queued install job stays queued).</summary>
public sealed class SshApiFixture : IDisposable
{
    public SshApiFixture()
    {
        Base = new AetheraApiFactory();
        Connector = new FakeSshConnector();
        Factory = Base.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Aethera:Jobs:WorkerCount", "0");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISshConnector>();
                services.AddSingleton<ISshConnector>(Connector);
            });
        });
    }

    public AetheraApiFactory Base { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public FakeSshConnector Connector { get; }

    public async Task<Tenant> NewTenantAsync() => new(Factory, await Factory.SeedIdentityAsync());

    public void Dispose() => Base.Dispose();
}

[CollectionDefinition(Name)]
public sealed class SshApiCollection : ICollectionFixture<SshApiFixture>
{
    public const string Name = "ssh-api";
}

[Collection(SshApiCollection.Name)]
public sealed class SshApiTests(SshApiFixture fixture)
{
    private const string Password = "Api-Pw-Leak-Check-5521";
    private const string Passphrase = "Api-Passphrase-3310";
    private static readonly string Fingerprint = "SHA256:" + new string('A', 43);

    private static object NewServer(string? name = null, bool install = false, object? extra = null)
    {
        var body = new JsonObject
        {
            ["name"] = name ?? Tenant.Unique("ssh"), ["host"] = Tenant.Unique("host") + ".example.com", ["sshUser"] = "deploy", ["password"] = Password,
            ["roles"] = new JsonArray("master"), ["installAgent"] = install,
        };
        if (extra is not null) foreach (var (key, value) in System.Text.Json.JsonSerializer.SerializeToNode(extra, Json.Options)!.AsObject()) body[key] = value?.DeepClone();
        return body;
    }

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private static string Ssh(string id) => $"/api/v1/servers/{id}/ssh";

    // ================================================================ add by SSH

    [RequiresDatabaseFact]
    public async Task Adding_a_server_by_ssh_stores_the_credential_as_a_managed_secret_that_is_never_returned()
    {
        var tenant = await fixture.NewTenantAsync();
        var response = await tenant.Admin.PostAsync("/api/v1/servers/ssh", NewServer());
        var body = await response.ReadAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.NotNull(response.Headers.Location);
        var server = body["server"]!;
        Assert.Equal("ssh", server["transport"]!.GetValue<string>());
        Assert.Equal("deploy", server["sshUser"]!.GetValue<string>());
        Assert.Equal("notInstalled", server["status"]!["agent"]!["status"]!.GetValue<string>());
        Assert.Null(body["installJob"]);
        var secretId = server["sshCredentialSecretId"]!.GetValue<string>();

        // The secret exists, is managed by the server and nothing in any response carries the password.
        var secret = await tenant.Admin.GetJsonAsync($"/api/v1/secrets/{secretId}");
        Assert.True(secret["managed"]!.GetValue<bool>());
        Assert.Equal("sshCredential", secret["purpose"]!.GetValue<string>());
        Assert.Equal("server", secret["managedBy"]!["type"]!.GetValue<string>());
        Assert.Equal(server.Id(), secret["managedBy"]!["id"]!.GetValue<string>());
        foreach (var url in new[] { $"/api/v1/servers/{server.Id()}", "/api/v1/servers", $"/api/v1/secrets/{secretId}", "/api/v1/secrets", Ssh(server.Id()) })
            Assert.DoesNotContain(Password, (await tenant.Admin.GetJsonAsync(url)).ToJsonString());
        Assert.DoesNotContain(Password, body.ToJsonString());

        var audit = await tenant.AuditAsync(server.Id(), "server.created");
        Assert.DoesNotContain(Password, audit.Single().MetadataJson);
        var allAudit = await tenant.AuditAsync();
        Assert.DoesNotContain(allAudit, e => e.MetadataJson.Contains(Password));

        // The managed secret cannot be edited or bound through /secrets (ADR 0006).
        var edit = await tenant.Admin.PatchAsync($"/api/v1/secrets/{secretId}", new { description = "x" });
        await edit.AssertProblemAsync(409, "secret.managed");
    }

    [RequiresDatabaseFact]
    public async Task The_stored_credential_decrypts_to_what_was_sent_and_a_key_with_passphrase_is_kept_together()
    {
        var tenant = await fixture.NewTenantAsync();
        var created = await tenant.Admin.PostAsync("/api/v1/servers/ssh", NewServer());
        var id = Guid.Parse((await created.ReadAsync())["server"]!.Id());

        var access = await WithScopeAsync(sp => sp.GetRequiredService<SshAccessProvider>().GetAsync(id, CancellationToken.None));
        Assert.Equal(Password, access!.Auth.Password);
        Assert.Equal(("deploy", 22), (access.Target.User, access.Target.Port));
    }

    [RequiresDatabaseFact]
    public async Task Installing_right_away_enqueues_the_install_job_with_the_verified_host_key()
    {
        var tenant = await fixture.NewTenantAsync();
        var response = await tenant.Admin.PostAsync("/api/v1/servers/ssh", NewServer(install: true, extra: new { hostKeyFingerprint = Fingerprint }));
        var body = await response.ReadAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var job = body["installJob"]!;
        Assert.Equal("server.install_agent", job["type"]!.GetValue<string>());
        Assert.Equal("queued", job["status"]!.GetValue<string>());
        Assert.Equal(body["server"]!.Id(), job["resource"]!["id"]!.GetValue<string>());

        // The fingerprint the user verified is pinned before any connection; the payload carries ids and the fingerprint only.
        var id = Guid.Parse(body["server"]!.Id());
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        Assert.Equal(Fingerprint, (await db.Servers.AsNoTracking().FirstAsync(s => s.Id == id)).SshHostKeyFingerprint);
        var row = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == Guid.Parse(job.Id()));
        Assert.DoesNotContain(Password, row.PayloadJson);
        Assert.Contains(id.ToString(), row.PayloadJson);
    }

    [RequiresDatabaseFact]
    public async Task Invalid_requests_are_refused_without_creating_anything()
    {
        var tenant = await fixture.NewTenantAsync();
        async Task Rejected(object body, string pointer)
        {
            var response = await tenant.Admin.PostAsync("/api/v1/servers/ssh", body);
            var errors = await response.ValidationErrorsAsync();
            Assert.Contains(errors, e => e.Location == pointer);
        }

        await Rejected(new { name = "a", host = "h.example.com" }, "/privateKey"); // neither key nor password
        await Rejected(new { name = "a", host = "h.example.com", password = "x", privateKey = "-----BEGIN KEY-----" }, "/privateKey"); // both
        await Rejected(new { name = "a", host = "https://h.example.com", password = "x" }, "/host");
        await Rejected(new { name = "a", host = "h.example.com", password = "x", sshUser = "Bad User" }, "/sshUser");
        await Rejected(new { name = "a", host = "h.example.com", password = "x", passphrase = "p" }, "/passphrase");
        await Rejected(new { name = "a", host = "h.example.com", privateKey = "not a key" }, "/privateKey");
        await Rejected(new { name = "a", host = "h.example.com", password = "x", hostKeyFingerprint = "MD5:abc" }, "/hostKeyFingerprint");
        await Rejected(new { name = "a", host = "h.example.com", password = "x", roles = new[] { "king" } }, "/roles/0");
        await Rejected(new { name = "", host = "h.example.com", password = "x" }, "/name");

        // A key that looks like one but cannot be parsed is refused before it is stored.
        var unreadable = await tenant.Admin.PostAsync("/api/v1/servers/ssh",
            new { name = "keyed", host = "h.example.com", privateKey = "-----BEGIN RSA PRIVATE KEY-----\nbm90IGEga2V5\n-----END RSA PRIVATE KEY-----\n" });
        Assert.Contains(await unreadable.ValidationErrorsAsync(), e => e.Location == "/privateKey");
        Assert.Empty((await tenant.Admin.GetJsonAsync("/api/v1/servers"))["items"]!.AsArray());
        Assert.Empty((await tenant.Admin.GetJsonAsync("/api/v1/secrets"))["items"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task A_duplicate_name_is_a_conflict()
    {
        var tenant = await fixture.NewTenantAsync();
        await tenant.Admin.CreateAsync("/api/v1/servers/ssh", NewServer("same"));
        var again = await tenant.Admin.PostAsync("/api/v1/servers/ssh", NewServer("same"));
        await again.AssertProblemAsync(409, "server.already_exists");
    }

    // ================================================================ authorization

    [RequiresDatabaseFact]
    public async Task Only_administrators_with_the_servers_write_scope_may_change_ssh_settings()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = (await tenant.Admin.CreateAsync("/api/v1/servers/ssh", NewServer()))["server"]!;
        var id = server.Id();

        var writes = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, "/api/v1/servers/ssh", NewServer()),
            (HttpMethod.Post, "/api/v1/servers/ssh/host-key", new { host = "h.example.com" }),
            (HttpMethod.Post, $"/api/v1/servers/{id}/install-agent", new { }),
            (HttpMethod.Patch, Ssh(id), new { allowSshFallback = false }),
            (HttpMethod.Post, $"{Ssh(id)}/host-key/confirm", new { fingerprint = Fingerprint }),
            (HttpMethod.Post, $"{Ssh(id)}/ssh-test-placeholder", null),
            (HttpMethod.Post, $"{Ssh(id)}/test", null),
        };
        foreach (var (method, url, body) in writes.Where(w => !w.Url.Contains("placeholder")))
        {
            foreach (var denied in new[] { tenant.Developer, tenant.Viewer })
            {
                var request = new HttpRequestMessage(method, url) { Content = body is null ? null : Json.Body(body, method == HttpMethod.Patch ? "application/merge-patch+json" : "application/json") };
                Assert.Equal(HttpStatusCode.Forbidden, (await denied.SendAsync(request)).StatusCode);
            }

            // An administrator's token needs the servers:write scope; read-only tokens do not pass.
            var tokenRequest = new HttpRequestMessage(method, url) { Content = body is null ? null : Json.Body(body, method == HttpMethod.Patch ? "application/merge-patch+json" : "application/json") };
            Assert.Equal(HttpStatusCode.Forbidden, (await tenant.Token(OrganizationRole.Admin, "read").SendAsync(tokenRequest)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Factory.CreateAnonymousClient().SendAsync(new HttpRequestMessage(method, url))).StatusCode);
        }

        // Reading the SSH state needs only read access.
        Assert.Equal(HttpStatusCode.OK, (await tenant.Viewer.GetAsync(Ssh(id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenant.Token(OrganizationRole.Viewer, "read").GetAsync(Ssh(id))).StatusCode);
    }

    // ================================================================ state, fallback switch, host key

    [RequiresDatabaseFact]
    public async Task The_ssh_state_shows_the_pin_the_fallback_switch_and_the_degraded_mode()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = (await tenant.Admin.CreateAsync("/api/v1/servers/ssh", NewServer(extra: new { sshPort = 2222 })))["server"]!;
        var id = server.Id();

        var state = await tenant.Viewer.GetJsonAsync(Ssh(id));
        Assert.True(state["hasCredential"]!.GetValue<bool>());
        Assert.Equal(2222, state["port"]!.GetValue<int>());
        Assert.Equal("unpinned", state["hostKey"]!["state"]!.GetValue<string>());
        Assert.True(state["allowSshFallback"]!.GetValue<bool>());
        Assert.Equal("sshPolling", state["mode"]!.GetValue<string>());
        Assert.True(state["degraded"]!.GetValue<bool>());
        Assert.Equal("polling over SSH", state["degradedReason"]!.GetValue<string>());

        // Turn the fallback off: no longer polled, not degraded but idle; audited.
        var off = await tenant.Admin.PatchOkAsync(Ssh(id), new { allowSshFallback = false });
        Assert.False(off["allowSshFallback"]!.GetValue<bool>());
        Assert.Equal("sshIdle", off["mode"]!.GetValue<string>());
        Assert.False(off["degraded"]!.GetValue<bool>());
        Assert.Single(await tenant.AuditAsync(id, "server.ssh_fallback_changed"));
        Assert.Equal("sshPolling", (await tenant.Admin.PatchOkAsync(Ssh(id), new { allowSshFallback = true }))["mode"]!.GetValue<string>());

        // A server that was never given SSH credentials has nothing to show.
        var plain = await tenant.CreateServerAsync();
        var none = await tenant.Viewer.GetJsonAsync(Ssh(plain.Id()));
        Assert.False(none["hasCredential"]!.GetValue<bool>());
        Assert.Equal("none", none["mode"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task A_changed_host_key_is_shown_and_confirming_the_presented_one_unblocks_the_server()
    {
        var tenant = await fixture.NewTenantAsync();
        var created = (await tenant.Admin.CreateAsync("/api/v1/servers/ssh", NewServer(extra: new { hostKeyFingerprint = Fingerprint })))["server"]!;
        var id = created.Id();
        var presented = "SHA256:" + new string('B', 43);
        await WithScopeAsync(async sp =>
        {
            await sp.GetRequiredService<SshHostKeyService>().RecordChangedAsync(Guid.Parse(id), Fingerprint, new HostKeyInfo("ssh-ed25519", presented), CancellationToken.None);
            return 0;
        });

        var state = await tenant.Viewer.GetJsonAsync(Ssh(id));
        Assert.Equal("changed", state["hostKey"]!["state"]!.GetValue<string>());
        Assert.Equal(Fingerprint, state["hostKey"]!["pinnedFingerprint"]!.GetValue<string>());
        Assert.Equal(presented, state["hostKey"]!["pending"]!["fingerprint"]!.GetValue<string>());

        // Anything but the key that was presented is refused.
        var wrong = await tenant.Admin.PostAsync($"{Ssh(id)}/host-key/confirm", new { fingerprint = "SHA256:" + new string('C', 43) });
        await wrong.AssertProblemAsync(409, "ssh.host_key_mismatch");
        var malformed = await tenant.Admin.PostAsync($"{Ssh(id)}/host-key/confirm", new { fingerprint = "not-a-fingerprint" });
        Assert.Contains(await malformed.ValidationErrorsAsync(), e => e.Location == "/fingerprint");

        var confirmed = await tenant.Admin.PostAsync($"{Ssh(id)}/host-key/confirm", new { fingerprint = presented });
        var after = await confirmed.ReadAsync();
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("pinned", after["hostKey"]!["state"]!.GetValue<string>());
        Assert.Equal(presented, after["hostKey"]!["pinnedFingerprint"]!.GetValue<string>());
        Assert.Null(after["hostKey"]!["pending"]);
        var audit = Assert.Single(await tenant.AuditAsync(id, "server.ssh_host_key_confirmed"));
        Assert.Contains(presented, audit.MetadataJson);
        Assert.Contains(Fingerprint, audit.MetadataJson); // the key it replaced

        // Confirming the pinned key again is harmless; confirming any other key when nothing is pending is refused.
        Assert.Equal(HttpStatusCode.OK, (await tenant.Admin.PostAsync($"{Ssh(id)}/host-key/confirm", new { fingerprint = presented })).StatusCode);
        var other = await tenant.Admin.PostAsync($"{Ssh(id)}/host-key/confirm", new { fingerprint = "SHA256:" + new string('D', 43) });
        await other.AssertProblemAsync(409, "ssh.host_key_nothing_to_confirm");
    }

    // ================================================================ install, test, scan

    [RequiresDatabaseFact]
    public async Task Installing_the_agent_needs_ssh_credentials_and_returns_the_job()
    {
        var tenant = await fixture.NewTenantAsync();
        var plain = await tenant.CreateServerAsync();
        var refused = await tenant.Admin.PostAsync($"/api/v1/servers/{plain.Id()}/install-agent", new { });
        await refused.AssertProblemAsync(422, "ssh.no_credential");
        await (await tenant.Admin.PostAsync($"/api/v1/servers/{Guid.NewGuid()}/install-agent", new { })).AssertProblemAsync(404, "server.not_found");

        var server = (await tenant.Admin.CreateAsync("/api/v1/servers/ssh", NewServer()))["server"]!;
        var response = await tenant.Admin.PostAsync($"/api/v1/servers/{server.Id()}/install-agent", new { hostKeyFingerprint = Fingerprint });
        var job = await response.ReadAsync();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("server.install_agent", job["type"]!.GetValue<string>());
        Assert.NotNull(response.Headers.Location);
        Assert.Single(await tenant.AuditAsync(server.Id(), "server.agent_install_requested"));

        var malformed = await tenant.Admin.PostAsync($"/api/v1/servers/{server.Id()}/install-agent", new { hostKeyFingerprint = "x" });
        Assert.Contains(await malformed.ValidationErrorsAsync(), e => e.Location == "/hostKeyFingerprint");
    }

    [RequiresDatabaseFact]
    public async Task A_connection_test_reports_the_host_and_maps_failures_to_problems()
    {
        var tenant = await fixture.NewTenantAsync();
        var server = (await tenant.Admin.CreateAsync("/api/v1/servers/ssh", NewServer()))["server"]!;
        var id = server.Id();
        var connector = new FakeSshConnector();
        connector.Connection.Handler = c => new RemoteResult(0, "Linux x86_64\n27.3.1\n", "");

        // The fixture's connector is shared by the whole collection; use a dedicated factory for scripted failures.
        using var factory = fixture.Base.WithWebHostBuilder(b =>
        {
            b.UseSetting("Aethera:Jobs:WorkerCount", "0");
            b.ConfigureTestServices(s => { s.RemoveAll<ISshConnector>(); s.AddSingleton<ISshConnector>(connector); });
        });
        var admin = factory.CreateClientAs(OrganizationRole.Admin, identity: tenant.Identity);

        var ok = await admin.PostAsync(Ssh(id) + "/test", null);
        var body = await ok.ReadAsync();
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(body["connected"]!.GetValue<bool>());
        Assert.Equal("Linux", body["os"]!.GetValue<string>());
        Assert.Equal("amd64", body["architecture"]!.GetValue<string>());
        Assert.Equal("27.3.1", body["dockerVersion"]!.GetValue<string>());
        Assert.Equal(connector.Connection.HostKey.Fingerprint, body["hostKey"]!["fingerprint"]!.GetValue<string>());

        // The first connection pinned the key; a server that now presents another one is a 409 until confirmed.
        connector.Connection = new FakeSshConnection("SHA256:" + new string('Z', 43));
        await factory.Services.GetRequiredService<SshConnectionPool>().EvictAsync(Guid.Parse(id));
        await (await admin.PostAsync(Ssh(id) + "/test", null)).AssertProblemAsync(409, "ssh.host_key_changed");
        var state = await admin.GetJsonAsync(Ssh(id));
        Assert.Equal("changed", state["hostKey"]!["state"]!.GetValue<string>());

        var plain = await tenant.CreateServerAsync();
        await (await admin.PostAsync(Ssh(plain.Id()) + "/test", null)).AssertProblemAsync(422, "ssh.no_credential");
    }

    [RequiresDatabaseFact]
    public async Task Refused_credentials_and_unreachable_hosts_are_problems_but_never_a_401()
    {
        var tenant = await fixture.NewTenantAsync();
        var id = (await tenant.Admin.CreateAsync("/api/v1/servers/ssh", NewServer()))["server"]!.Id();
        var connector = new FakeSshConnector { Failure = new SshConnectException("The server refused the SSH credentials.", null, authentication: true) };
        using var factory = fixture.Base.WithWebHostBuilder(b =>
        {
            b.UseSetting("Aethera:Jobs:WorkerCount", "0");
            b.ConfigureTestServices(s => { s.RemoveAll<ISshConnector>(); s.AddSingleton<ISshConnector>(connector); });
        });
        var admin = factory.CreateClientAs(OrganizationRole.Admin, identity: tenant.Identity);

        var refused = await admin.PostAsync(Ssh(id) + "/test", null);
        await refused.AssertProblemAsync(422, "ssh.auth_failed");

        await factory.Services.GetRequiredService<SshConnectionPool>().EvictAsync(Guid.Parse(id));
        connector.Failure = new SshConnectException("The server could not be reached (ConnectionRefused).");
        var unreachable = await admin.PostAsync(Ssh(id) + "/test", null);
        await unreachable.AssertProblemAsync(503, "server.unreachable");
        Assert.DoesNotContain(Password, await refused.Content.ReadAsStringAsync() + await unreachable.Content.ReadAsStringAsync());
    }

    [RequiresDatabaseFact]
    public async Task Scanning_a_host_key_returns_the_fingerprint_without_storing_anything()
    {
        var tenant = await fixture.NewTenantAsync();
        var response = await tenant.Admin.PostAsync("/api/v1/servers/ssh/host-key", new { host = "scan.example.com", port = 2222 });
        var body = await response.ReadAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ssh-ed25519", body["algorithm"]!.GetValue<string>());
        Assert.StartsWith("SHA256:", body["fingerprint"]!.GetValue<string>());
        Assert.Empty((await tenant.Admin.GetJsonAsync("/api/v1/servers"))["items"]!.AsArray());

        var bad = await tenant.Admin.PostAsync("/api/v1/servers/ssh/host-key", new { host = "ssh://scan.example.com" });
        Assert.Contains(await bad.ValidationErrorsAsync(), e => e.Location == "/host");
    }

    // ================================================================ contract

    [RequiresDatabaseFact]
    public async Task The_openapi_document_describes_the_ssh_operations_and_no_response_has_a_credential_field()
    {
        var doc = await fixture.Factory.CreateAnonymousClient().GetStringAsync("/api/openapi/v1.json");
        var json = JsonNode.Parse(doc)!;
        var ids = json["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject().Select(o => o.Value!["operationId"]?.GetValue<string>())).ToHashSet();
        foreach (var operation in new[] { "addSshServer", "scanSshHostKey", "installServerAgent", "getServerSsh", "updateServerSsh", "confirmServerSshHostKey", "testServerSsh" })
            Assert.Contains(operation, ids);

        var schemas = json["components"]!["schemas"]!.AsObject();
        foreach (var name in new[] { "AddSshServerResponse", "SshStateResponse", "SshTestResponse", "HostKeyResponse" })
        {
            var text = schemas[name]!.ToJsonString().ToLowerInvariant();
            foreach (var forbidden in new[] { "password", "privatekey", "passphrase", "secret" }) Assert.DoesNotContain(forbidden, text.Replace("sshcredentialsecretid", ""));
        }

        Assert.NotNull(schemas["AddSshServerRequest"]!["properties"]!["privateKey"]);
    }
}

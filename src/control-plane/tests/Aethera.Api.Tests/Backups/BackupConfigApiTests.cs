using System.Net;
using System.Text.Json.Nodes;
using Aethera.Api.Tests.Resources;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests.Backups;

/// <summary>Destinations and policies: validation, authorization, tenancy, secrets.</summary>
[Collection(BackupsCollection.Name)]
public sealed class BackupConfigApiTests(BackupsFixture fixture)
{
    private static object Destination(string? name = null, object? extra = null) => new
    {
        name = name ?? Tenant.Unique("dest"), endpoint = "https://acct123.r2.cloudflarestorage.com", region = "auto", bucket = "turn-one-backups",
        accessKeyId = "AKIA-TEST-KEY", secretAccessKey = BackupStack.Secret,
    };

    // ---- destinations -------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_destination_is_created_with_a_write_only_managed_secret_and_the_first_one_is_the_default()
    {
        var t = await fixture.NewTenantAsync();
        var response = await t.Admin.PostAsync("/api/v1/backup-destinations", Destination("R2"));
        var body = await response.ReadAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("s3", body["kind"]!.GetValue<string>());
        Assert.True(body["hasSecret"]!.GetValue<bool>());
        Assert.True(body["isDefault"]!.GetValue<bool>());
        Assert.Equal("https://acct123.r2.cloudflarestorage.com", body["endpoint"]!.GetValue<string>());
        Assert.DoesNotContain(BackupStack.Secret, body.ToJsonString());
        Assert.DoesNotContain(BackupStack.Secret, await t.Admin.GetStringAsync($"/api/v1/backup-destinations/{body.Id()}"));
        Assert.DoesNotContain(BackupStack.Secret, await t.Admin.GetStringAsync("/api/v1/backup-destinations"));

        // the access key is a managed secret of its own purpose, so /secrets lists it but cannot change or bind it
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var secret = await db.Secrets.AsNoTracking().SingleAsync(s => s.OrganizationId == t.Identity.OrganizationId && s.Purpose == SecretPurpose.BackupCredential);
        Assert.True(secret.IsManaged);
        var bind = await t.Admin.PatchAsync($"/api/v1/secrets/{secret.Id}", new { description = "x" });
        await bind.AssertProblemAsync(409, "secret.managed");

        var second = await t.Admin.CreateAsync("/api/v1/backup-destinations", Destination("R2 two"));
        Assert.False(second["isDefault"]!.GetValue<bool>());
        var made = await t.Admin.PatchOkAsync($"/api/v1/backup-destinations/{second.Id()}", new { isDefault = true });
        Assert.True(made["isDefault"]!.GetValue<bool>());
        Assert.False((await t.Admin.GetJsonAsync($"/api/v1/backup-destinations/{body.Id()}"))["isDefault"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task Only_administrators_with_the_write_and_secrets_scopes_manage_destinations()
    {
        var t = await fixture.NewTenantAsync();
        foreach (var client in new[] { t.Developer, t.Viewer })
            await (await client.PostAsync("/api/v1/backup-destinations", Destination())).AssertProblemAsync(403, "auth.forbidden");

        await (await t.Token(OrganizationRole.Admin, "write").PostAsync("/api/v1/backup-destinations", Destination())).AssertProblemAsync(403, "auth.insufficient_scope");
        await (await t.Token(OrganizationRole.Admin, "secrets:write").PostAsync("/api/v1/backup-destinations", Destination())).AssertProblemAsync(403, "auth.insufficient_scope");
        var created = await t.Token(OrganizationRole.Admin, "write", "secrets:write").PostAsync("/api/v1/backup-destinations", Destination());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // anyone who may read can pick one for a policy
        Assert.Equal(HttpStatusCode.OK, (await t.Viewer.GetAsync("/api/v1/backup-destinations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Developer.GetAsync("/api/v1/backup-destinations")).StatusCode);
        var id = (await created.ReadAsync()).Id();
        await (await t.Developer.PatchAsync($"/api/v1/backup-destinations/{id}", new { name = "x" })).AssertProblemAsync(403, "auth.forbidden");
        await (await t.Developer.PostAsync($"/api/v1/backup-destinations/{id}/test", new { })).AssertProblemAsync(403, "auth.forbidden");
        await (await t.Developer.DeleteAsync($"/api/v1/backup-destinations/{id}?confirm=x")).AssertProblemAsync(403, "auth.forbidden");
    }

    [RequiresDatabaseTheory]
    [InlineData("http://s3.example.com", "/endpoint")]
    [InlineData("ftp://x", "/endpoint")]
    [InlineData("https://acct.r2.cloudflarestorage.com/bucket", "/endpoint")]
    public async Task Endpoints_must_be_https_unless_private(string endpoint, string pointer)
    {
        var t = await fixture.NewTenantAsync();
        var response = await t.Admin.PostAsync("/api/v1/backup-destinations", new { name = "d", endpoint, bucket = "bucket-one", accessKeyId = "k", secretAccessKey = "s" });
        Assert.Contains((pointer, "pattern"), await response.ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task A_local_minio_is_allowed_over_http_and_required_fields_are_checked()
    {
        var t = await fixture.NewTenantAsync();
        var ok = await t.Admin.PostAsync("/api/v1/backup-destinations", new
        {
            name = "minio", endpoint = "http://127.0.0.1:9102", bucket = "backups", accessKeyId = "minioadmin", secretAccessKey = "minioadmin", forcePathStyle = true,
        });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.True((await ok.ReadAsync())["forcePathStyle"]!.GetValue<bool>());

        var missing = await t.Admin.PostAsync("/api/v1/backup-destinations", new { name = "d" });
        var errors = await missing.ValidationErrorsAsync();
        Assert.Contains(("/endpoint", "required"), errors);
        Assert.Contains(("/bucket", "required"), errors);
        Assert.Contains(("/accessKeyId", "required"), errors);
        Assert.Contains(("/secretAccessKey", "required"), errors);

        var prefix = await t.Admin.PostAsync("/api/v1/backup-destinations", new { name = "d", endpoint = "https://x.example.com", bucket = "bucket-one", accessKeyId = "k", secretAccessKey = "s", pathPrefix = "/lead" });
        Assert.Contains(("/pathPrefix", "pattern"), await prefix.ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task A_local_destination_needs_no_s3_settings_and_refuses_them_on_update()
    {
        var t = await fixture.NewTenantAsync();
        var local = await t.Admin.CreateAsync("/api/v1/backup-destinations", new { name = "local", kind = "local" });
        Assert.Equal("local", local["kind"]!.GetValue<string>());
        Assert.False(local["hasSecret"]!.GetValue<bool>());
        await (await t.Admin.PatchAsync($"/api/v1/backup-destinations/{local.Id()}", new { bucket = "nope-bucket" })).ValidationErrorsAsync();
        var test = await t.Admin.PostAsync($"/api/v1/backup-destinations/{local.Id()}/test", new { });
        Assert.True((await test.ReadAsync())["ok"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task Names_are_unique_per_organization_and_destinations_of_other_organizations_do_not_exist()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var first = await a.Admin.CreateAsync("/api/v1/backup-destinations", Destination("same-name"));
        await (await a.Admin.PostAsync("/api/v1/backup-destinations", Destination("same-name"))).AssertProblemAsync(409, "backup_destination.already_exists");
        Assert.Equal(HttpStatusCode.Created, (await b.Admin.PostAsync("/api/v1/backup-destinations", Destination("same-name"))).StatusCode);

        await (await b.Admin.GetAsync($"/api/v1/backup-destinations/{first.Id()}")).AssertProblemAsync(404, "backup_destination.not_found");
        await (await b.Admin.PatchAsync($"/api/v1/backup-destinations/{first.Id()}", new { name = "x" })).AssertProblemAsync(404, "backup_destination.not_found");
        await (await b.Admin.PostAsync($"/api/v1/backup-destinations/{first.Id()}/test", new { })).AssertProblemAsync(404, "backup_destination.not_found");
        await (await b.Admin.DeleteAsync($"/api/v1/backup-destinations/{first.Id()}?confirm=same-name")).AssertProblemAsync(404, "backup_destination.not_found");
        Assert.Single((await b.Admin.GetJsonAsync("/api/v1/backup-destinations"))["items"]!.AsArray());
    }

    [RequiresDatabaseFact]
    public async Task Updating_the_secret_adds_a_version_and_never_returns_it()
    {
        var t = await fixture.NewTenantAsync();
        var destination = await t.Admin.CreateAsync("/api/v1/backup-destinations", Destination());
        var updated = await t.Admin.PatchOkAsync($"/api/v1/backup-destinations/{destination.Id()}", new { secretAccessKey = "a-second-secret-value", bucket = "other-bucket" });
        Assert.DoesNotContain("a-second-secret-value", updated.ToJsonString());
        Assert.Equal("other-bucket", updated["bucket"]!.GetValue<string>());

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        var secret = await db.Secrets.AsNoTracking().SingleAsync(s => s.OrganizationId == t.Identity.OrganizationId && s.Purpose == SecretPurpose.BackupCredential);
        Assert.Equal(2, secret.CurrentVersion);
        var audit = await t.AuditAsync(destination.Id(), "backup_destination.updated");
        Assert.Single(audit);
        Assert.DoesNotContain("a-second-secret-value", audit[0].MetadataJson);
        Assert.DoesNotContain(BackupStack.Secret, audit[0].MetadataJson);
    }

    [RequiresDatabaseFact]
    public async Task Testing_a_destination_records_the_result_and_the_error_without_credentials()
    {
        var t = await fixture.NewTenantAsync();
        var destination = await t.Admin.CreateAsync("/api/v1/backup-destinations", Destination());
        fixture.Store.Reset();

        var ok = await (await t.Admin.PostAsync($"/api/v1/backup-destinations/{destination.Id()}/test", new { })).ReadAsync();
        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Null(ok["error"]?.GetValue<string>());
        var tested = Assert.Single(fixture.Store.Tested);
        Assert.Equal("turn-one-backups", tested.Bucket);
        Assert.Equal("auto", tested.Region);
        Assert.Equal(BackupStack.Secret, tested.SecretAccessKey.Value); // the real value reaches the store, never a response
        Assert.NotNull((await t.Admin.GetJsonAsync($"/api/v1/backup-destinations/{destination.Id()}"))["lastTestedAt"]);

        fixture.Store.TestResult = new Infrastructure.Backups.StoreTestResult(false, "Access denied: the key may not read, write and delete objects in this bucket.");
        var failed = await (await t.Admin.PostAsync($"/api/v1/backup-destinations/{destination.Id()}/test", new { })).ReadAsync();
        Assert.False(failed["ok"]!.GetValue<bool>());
        var stored = await t.Admin.GetJsonAsync($"/api/v1/backup-destinations/{destination.Id()}");
        Assert.Contains("Access denied", stored["lastTestError"]!.GetValue<string>());
        fixture.Store.Reset();

        // a change of the connection settings clears what was tested
        var changed = await t.Admin.PatchOkAsync($"/api/v1/backup-destinations/{destination.Id()}", new { bucket = "another-bucket" });
        Assert.Null(changed["lastTestedAt"]?.GetValue<string>());
        Assert.Null(changed["lastTestError"]?.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task A_destination_used_by_a_policy_cannot_be_deleted_and_deleting_needs_the_name()
    {
        var stack = await BackupStack.CreateAsync(fixture);
        var t = stack.Tenant;
        var policyId = await stack.CreateDatabasePolicyAsync();
        var name = (await t.Admin.GetJsonAsync($"/api/v1/backup-destinations/{stack.DestinationId}"))["name"]!.GetValue<string>();

        await (await t.Admin.DeleteAsync($"/api/v1/backup-destinations/{stack.DestinationId}")).AssertProblemAsync(428, "confirmation.required");
        await (await t.Admin.DeleteAsync($"/api/v1/backup-destinations/{stack.DestinationId}?confirm={name}")).AssertProblemAsync(409, "backup_destination.in_use");

        Assert.Equal(HttpStatusCode.NoContent, (await t.Developer.DeleteAsync($"/api/v1/backup-policies/{policyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync($"/api/v1/backup-destinations/{stack.DestinationId}?confirm={name}")).StatusCode);
        await (await t.Admin.GetAsync($"/api/v1/backup-destinations/{stack.DestinationId}")).AssertProblemAsync(404, "backup_destination.not_found");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        Assert.Empty(await db.Secrets.AsNoTracking().Where(s => s.OrganizationId == t.Identity.OrganizationId && s.Purpose == SecretPurpose.BackupCredential).ToListAsync());
    }

    // ---- policies -----------------------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task A_service_lists_what_can_be_backed_up_and_its_policies()
    {
        var stack = await BackupStack.CreateAsync(fixture);
        var empty = await stack.Tenant.Viewer.GetJsonAsync(stack.PoliciesUrl);
        Assert.Empty(empty["policies"]!.AsArray());
        var targets = empty["targets"]!.AsArray();
        var database = targets.Single(x => x!["kind"]!.GetValue<string>() == "database")!;
        Assert.True(database["supported"]!.GetValue<bool>());
        Assert.Equal("postgres", database["engine"]!.GetValue<string>());
        var volume = targets.Single(x => x!["kind"]!.GetValue<string>() == "volume")!;
        Assert.True(volume["supported"]!.GetValue<bool>());
        Assert.NotNull(volume["volumeId"]);
        Assert.Equal("/var/lib/postgresql/data", volume["mountPath"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task A_template_without_an_engine_offers_volume_backups_only()
    {
        var stack = await BackupStack.CreateAsync(fixture, "grafana");
        var targets = (await stack.Tenant.Viewer.GetJsonAsync(stack.PoliciesUrl))["targets"]!.AsArray();
        var database = targets.Single(x => x!["kind"]!.GetValue<string>() == "database")!;
        Assert.False(database["supported"]!.GetValue<bool>());
        Assert.Contains("grafana", database["reason"]!.GetValue<string>());

        var response = await stack.Tenant.Developer.PutAsync(stack.PoliciesUrl, Json.Body(new { kind = "database", destinationId = stack.DestinationId }));
        Assert.Contains(("/kind", "backup_policy.not_supported"), await response.ValidationErrorsAsync());

        var volumeId = targets.First(x => x!["kind"]!.GetValue<string>() == "volume")!["volumeId"]!.GetValue<string>();
        var volume = await stack.PutPolicyAsync(new { kind = "volume", volumeId, destinationId = stack.DestinationId });
        Assert.Equal("volume", volume["kind"]!.GetValue<string>());
        Assert.NotNull(volume["volumeName"]);
    }

    [RequiresDatabaseFact]
    public async Task An_application_has_volume_backups_only()
    {
        var t = await fixture.NewTenantAsync();
        var (serverId, environmentId, _, appId) = await t.CreateStackAsync();
        _ = serverId;
        _ = environmentId;
        var destination = await t.Admin.CreateAsync("/api/v1/backup-destinations", Destination());
        var response = await t.Developer.PutAsync($"/api/v1/applications/{appId}/backup-policies", Json.Body(new { kind = "database", destinationId = destination.Id() }));
        Assert.Contains(("/kind", "backup_policy.not_supported"), await response.ValidationErrorsAsync());
        var targets = (await t.Viewer.GetJsonAsync($"/api/v1/applications/{appId}/backup-policies"))["targets"]!.AsArray();
        Assert.False(targets.Single(x => x!["kind"]!.GetValue<string>() == "database")!["supported"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task A_policy_is_created_with_defaults_and_replaced_by_the_same_put()
    {
        var stack = await BackupStack.CreateAsync(fixture);
        var created = await stack.PutPolicyAsync();

        Assert.Equal("database", created["kind"]!.GetValue<string>());
        Assert.True(created["enabled"]!.GetValue<bool>());
        Assert.Equal("0 3 * * *", created["schedule"]!.GetValue<string>());
        Assert.Equal("UTC", created["timeZone"]!.GetValue<string>());
        Assert.Equal(7, created["keepDaily"]!.GetValue<int>());
        Assert.Equal(4, created["keepWeekly"]!.GetValue<int>());
        Assert.Equal(6, created["keepMonthly"]!.GetValue<int>());
        Assert.NotNull(created["nextRunAt"]);
        var nextRuns = created["nextRuns"]!.AsArray();
        Assert.Equal(3, nextRuns.Count);
        Assert.All(nextRuns, n => Assert.EndsWith("T03:00:00.000Z", n!.GetValue<string>()));
        Assert.Equal(DateTimeOffset.Parse(nextRuns[0]!.GetValue<string>()), DateTimeOffset.Parse(created["nextRunAt"]!.GetValue<string>()));

        var replaced = await stack.PutPolicyAsync(new { kind = "database", destinationId = stack.DestinationId, schedule = "0 */6 * * *", keepDaily = 3, keepWeekly = 0, keepMonthly = 1 });
        Assert.Equal(created.Id(), replaced.Id());
        Assert.Equal("0 */6 * * *", replaced["schedule"]!.GetValue<string>());
        Assert.Equal(3, replaced["keepDaily"]!.GetValue<int>());
        Assert.Single((await stack.Tenant.Viewer.GetJsonAsync(stack.PoliciesUrl))["policies"]!.AsArray());
        Assert.Equal(1, (await stack.Tenant.Viewer.GetJsonAsync("/api/v1/backup-policies"))["items"]!.AsArray().Count(p => p!["workloadId"]!.GetValue<string>() == stack.ServiceId));
        Assert.Equal(created.Id(), (await stack.Tenant.Viewer.GetJsonAsync($"/api/v1/backup-policies/{created.Id()}")).Id());

        var disabled = await stack.PutPolicyAsync(new { kind = "database", destinationId = stack.DestinationId, enabled = false });
        Assert.False(disabled["enabled"]!.GetValue<bool>());
        Assert.Null(disabled["nextRunAt"]?.GetValue<string>());

        Assert.NotEmpty(await stack.Tenant.AuditAsync(created.Id(), "backup_policy.created"));
        Assert.Equal(2, (await stack.Tenant.AuditAsync(created.Id(), "backup_policy.updated")).Count);
    }

    [RequiresDatabaseTheory]
    [InlineData("not a cron", "UTC", "/schedule", "invalid")]
    [InlineData("* * * * *", "UTC", "/schedule", "too_frequent")]
    [InlineData("*/5 * * * *", "UTC", "/schedule", "too_frequent")]
    [InlineData("0 3 * * *", "Mars/Olympus", "/timeZone", "invalid")]
    public async Task Schedules_must_be_valid_and_not_more_frequent_than_every_fifteen_minutes(string schedule, string timeZone, string pointer, string code)
    {
        var stack = await BackupStack.CreateAsync(fixture);
        var response = await stack.Tenant.Developer.PutAsync(stack.PoliciesUrl, Json.Body(new { kind = "database", destinationId = stack.DestinationId, schedule, timeZone }));
        Assert.Contains((pointer, code), await response.ValidationErrorsAsync());
    }

    [RequiresDatabaseTheory]
    [InlineData(0, 0, 0)]
    [InlineData(-1, 4, 6)]
    public async Task Retention_keeps_at_least_one_backup(int daily, int weekly, int monthly)
    {
        var stack = await BackupStack.CreateAsync(fixture);
        var response = await stack.Tenant.Developer.PutAsync(stack.PoliciesUrl, Json.Body(new { kind = "database", destinationId = stack.DestinationId, keepDaily = daily, keepWeekly = weekly, keepMonthly = monthly }));
        Assert.Contains(("/keepDaily", "range"), await response.ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task A_volume_policy_names_one_of_the_workloads_volumes_and_there_is_one_per_volume()
    {
        var stack = await BackupStack.CreateAsync(fixture);
        var targets = (await stack.Tenant.Viewer.GetJsonAsync(stack.PoliciesUrl))["targets"]!.AsArray();
        var volumeId = targets.Single(x => x!["kind"]!.GetValue<string>() == "volume")!["volumeId"]!.GetValue<string>();

        var missing = await stack.Tenant.Developer.PutAsync(stack.PoliciesUrl, Json.Body(new { kind = "volume", destinationId = stack.DestinationId }));
        Assert.Contains(("/volumeId", "required"), await missing.ValidationErrorsAsync());
        var foreign = await stack.Tenant.Developer.PutAsync(stack.PoliciesUrl, Json.Body(new { kind = "volume", volumeId = Guid.NewGuid(), destinationId = stack.DestinationId }));
        Assert.Contains(("/volumeId", "not_found"), await foreign.ValidationErrorsAsync());
        var withVolume = await stack.Tenant.Developer.PutAsync(stack.PoliciesUrl, Json.Body(new { kind = "database", volumeId, destinationId = stack.DestinationId }));
        Assert.Contains(("/volumeId", "invalid"), await withVolume.ValidationErrorsAsync());

        var first = await stack.PutPolicyAsync(new { kind = "volume", volumeId, destinationId = stack.DestinationId });
        var again = await stack.PutPolicyAsync(new { kind = "volume", volumeId, destinationId = stack.DestinationId, keepDaily = 2 });
        Assert.Equal(first.Id(), again.Id());
        Assert.Equal(volumeId, again["volumeId"]!.GetValue<string>());
    }

    [RequiresDatabaseFact]
    public async Task Bind_mounts_are_not_backed_up()
    {
        var stack = await BackupStack.CreateAsync(fixture, "grafana");
        var targets = (await stack.Tenant.Viewer.GetJsonAsync(stack.PoliciesUrl))["targets"]!.AsArray();
        var volumeId = targets.First(x => x!["kind"]!.GetValue<string>() == "volume")!["volumeId"]!.GetValue<string>();
        await stack.WithDbAsync(async db =>
        {
            var volume = await db.Volumes.FirstAsync(v => v.Id == Guid.Parse(volumeId));
            volume.HostPath = "/srv/grafana";
            return await db.SaveChangesAsync();
        });

        var response = await stack.Tenant.Developer.PutAsync(stack.PoliciesUrl, Json.Body(new { kind = "volume", volumeId, destinationId = stack.DestinationId }));
        Assert.Contains(("/volumeId", "backup_policy.not_supported"), await response.ValidationErrorsAsync());
        var volumeTarget = (await stack.Tenant.Viewer.GetJsonAsync(stack.PoliciesUrl))["targets"]!.AsArray().First(x => x!["kind"]!.GetValue<string>() == "volume")!;
        Assert.False(volumeTarget["supported"]!.GetValue<bool>());
    }

    [RequiresDatabaseFact]
    public async Task Policies_need_a_developer_and_the_write_scope_and_belong_to_one_organization()
    {
        var stack = await BackupStack.CreateAsync(fixture);
        var t = stack.Tenant;
        var body = new { kind = "database", destinationId = stack.DestinationId };

        await (await t.Viewer.PutAsync(stack.PoliciesUrl, Json.Body(body))).AssertProblemAsync(403, "auth.forbidden");
        await (await t.Token(OrganizationRole.Developer, "read").PutAsync(stack.PoliciesUrl, Json.Body(body))).AssertProblemAsync(403, "auth.insufficient_scope");
        Assert.Equal(HttpStatusCode.OK, (await t.Token(OrganizationRole.Developer, "write").PutAsync(stack.PoliciesUrl, Json.Body(body))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Token(OrganizationRole.Viewer, "read").GetAsync(stack.PoliciesUrl)).StatusCode);
        await (await t.Token(OrganizationRole.Viewer, "deploy").GetAsync(stack.PoliciesUrl)).AssertProblemAsync(403, "auth.insufficient_scope");

        var other = await fixture.NewTenantAsync();
        await (await other.Developer.GetAsync(stack.PoliciesUrl)).AssertProblemAsync(404, "service.not_found");
        await (await other.Developer.PutAsync(stack.PoliciesUrl, Json.Body(body))).AssertProblemAsync(404, "service.not_found");
        // a destination of another organization is as good as missing
        var foreignDestination = await other.Admin.CreateAsync("/api/v1/backup-destinations", Destination());
        var response = await t.Developer.PutAsync(stack.PoliciesUrl, Json.Body(new { kind = "database", destinationId = foreignDestination.Id() }));
        Assert.Contains(("/destinationId", "not_found"), await response.ValidationErrorsAsync());

        var policyId = (await t.Viewer.GetJsonAsync(stack.PoliciesUrl))["policies"]![0]!.Id();
        await (await other.Developer.GetAsync($"/api/v1/backup-policies/{policyId}")).AssertProblemAsync(404, "backup_policy.not_found");
        await (await other.Developer.DeleteAsync($"/api/v1/backup-policies/{policyId}")).AssertProblemAsync(404, "backup_policy.not_found");
        await (await other.Developer.PostAsync($"/api/v1/backup-policies/{policyId}/run", new { })).AssertProblemAsync(404, "backup_policy.not_found");
        Assert.Empty((await other.Viewer.GetJsonAsync("/api/v1/backup-policies"))["items"]!.AsArray());
        await (await t.Developer.GetAsync($"/api/v1/applications/{stack.ServiceId}/backup-policies")).AssertProblemAsync(404, "application.not_found");
    }

    // ---- Aethera's own database -------------------------------------------------------------------------------------------------

    [RequiresDatabaseFact]
    public async Task The_control_plane_policy_is_for_administrators_and_needs_an_s3_destination()
    {
        var stack = await BackupStack.CreateAsync(fixture);
        var t = stack.Tenant;
        var empty = await t.Admin.GetJsonAsync("/api/v1/system/backup-policy");
        Assert.Null(empty["policy"]?.GetValue<string>());
        Assert.Equal("0 3 * * *", empty["defaultSchedule"]!.GetValue<string>());

        await (await t.Developer.GetAsync("/api/v1/system/backup-policy")).AssertProblemAsync(403, "auth.forbidden");
        await (await t.Developer.PutAsync("/api/v1/system/backup-policy", Json.Body(new { destinationId = stack.DestinationId }))).AssertProblemAsync(403, "auth.forbidden");

        var local = await t.Admin.CreateAsync("/api/v1/backup-destinations", new { name = "local", kind = "local" });
        var rejected = await t.Admin.PutAsync("/api/v1/system/backup-policy", Json.Body(new { destinationId = local.Id() }));
        Assert.Contains(("/destinationId", "s3_required"), await rejected.ValidationErrorsAsync());

        var put = await t.Admin.PutAsync("/api/v1/system/backup-policy", Json.Body(new { destinationId = stack.DestinationId, schedule = "30 2 * * *" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var policy = (await put.ReadAsync())["policy"]!;
        Assert.Equal("controlPlane", policy["kind"]!.GetValue<string>());
        Assert.Null(policy["workloadId"]?.GetValue<string>());
        Assert.Equal("30 2 * * *", policy["schedule"]!.GetValue<string>());

        // it is one policy per organization, replaced by the next PUT, and not part of the workload policy list
        var again = await (await t.Admin.PutAsync("/api/v1/system/backup-policy", Json.Body(new { destinationId = stack.DestinationId, keepDaily = 14 }))).ReadAsync();
        Assert.Equal(policy.Id(), again["policy"]!.Id());
        Assert.Empty((await t.Viewer.GetJsonAsync("/api/v1/backup-policies"))["items"]!.AsArray());
        await (await t.Developer.GetAsync($"/api/v1/backup-policies/{policy.Id()}")).AssertProblemAsync(404, "backup_policy.not_found");
        await (await t.Developer.DeleteAsync($"/api/v1/backup-policies/{policy.Id()}")).AssertProblemAsync(404, "backup_policy.not_found");
        // the destination is in use now
        await (await t.Admin.DeleteAsync($"/api/v1/backup-destinations/{stack.DestinationId}?confirm=x")).AssertProblemAsync(428, "confirmation.required");
    }
}

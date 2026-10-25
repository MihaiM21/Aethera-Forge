using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Backups;

/// <summary>Backup destinations (ADR 0010): where encrypted backups are stored. The access key is a managed secret, write-only.</summary>
internal static class BackupDestinationEndpoints
{
    private static readonly SortDefinition<BackupDestination> Sorts = new SortDefinition<BackupDestination>("name")
        .Add("name", d => d.Name).Add("createdAt", d => d.CreatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/backup-destinations").WithTags("Backups");
        group.MapGet("/", List).WithName("listBackupDestinations").RequireRead();
        // The access key is a credential: besides the Administrator role a token needs write and secrets:write (write never covers a credential).
        group.MapPost("/", Create).WithName("createBackupDestination").Validate<CreateBackupDestinationRequest>().RequireAdmin().RequireScope(Scopes.SecretsWrite);
        group.MapGet("/{id:guid}", Get).WithName("getBackupDestination").RequireRead();
        group.MapPatch("/{id:guid}", Update).WithName("updateBackupDestination")
            .Accepts<UpdateBackupDestinationRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateBackupDestinationRequest>().RequireAdmin().RequireScope(Scopes.SecretsWrite);
        group.MapDelete("/{id:guid}", Delete).WithName("deleteBackupDestination").RequireAdmin().RequireScope(Scopes.SecretsWrite)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{id:guid}/test", Test).WithName("testBackupDestination").RequireAdmin()
            .WithSummary("Write, read and delete a small object to check the connection and the credentials");
    }

    private static async Task<Ok<Page<BackupDestinationResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort, CancellationToken ct)
    {
        http.RejectUnknownQuery();
        var query = db.BackupDestinations.Where(d => d.OrganizationId == actor.Org()).AsNoTracking();
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, "", ct);
        return TypedResults.Ok(new Page<BackupDestinationResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Created<BackupDestinationResponse>> Create(
        CreateBackupDestinationRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault, CancellationToken ct)
    {
        var org = actor.Org();
        var name = request.Name!.Trim();
        await EnsureNameFreeAsync(db, org, name, null, ct);
        var kind = request.Kind ?? BackupDestinationKind.S3;

        var destination = new BackupDestination { OrganizationId = org, Name = name, Kind = kind };
        if (kind == BackupDestinationKind.S3)
        {
            BackupDestinationRules.TryNormalizeEndpoint(request.Endpoint, out var endpoint, out _);
            destination.Endpoint = endpoint;
            destination.Region = Blank(request.Region);
            destination.Bucket = request.Bucket!.Trim();
            destination.PathPrefix = Blank(request.PathPrefix);
            destination.AccessKeyId = request.AccessKeyId!.Trim();
            destination.ForcePathStyle = request.ForcePathStyle ?? false;
            var secret = vault.Create(org, $"backup-destination/{destination.Id.ToString("N")[^12..]}", $"Access key of backup destination '{name}'.",
                request.SecretAccessKey!, purpose: SecretPurpose.BackupCredential);
            destination.SecretAccessKeySecretId = secret.Id;
            destination.SecretAccessKeySecret = secret;
        }

        db.BackupDestinations.Add(destination);
        var makeDefault = request.IsDefault ?? !await db.BackupDestinations.AnyAsync(d => d.OrganizationId == org, ct);
        if (makeDefault) await SetDefaultAsync(db, org, destination, ct);

        await audit.RecordAsync("backup_destination.created", "backup_destination", destination.Id,
            new { name, kind, endpoint = destination.Endpoint, bucket = destination.Bucket, isDefault = destination.IsDefault }, ct);
        http.SetETag(destination.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("backup-destinations", destination.Id), ToResponse(destination));
    }

    private static async Task<Ok<BackupDestinationResponse>> Get(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var destination = await FindAsync(db, actor.Org(), id, tracking: false, ct);
        http.SetETag(destination.RowVersion);
        return TypedResults.Ok(ToResponse(destination));
    }

    private static async Task<Ok<BackupDestinationResponse>> Update(
        Guid id, PatchRequest<UpdateBackupDestinationRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault,
        CancellationToken ct)
    {
        var org = actor.Org();
        var destination = await FindAsync(db, org, id, tracking: true, ct);
        http.CheckIfMatch(destination.RowVersion);
        var body = patch.Body;
        var changed = new List<string>();

        if (patch.Has("name") && body.Name is { } name && name.Trim() != destination.Name)
        {
            await EnsureNameFreeAsync(db, org, name.Trim(), id, ct);
            destination.Name = name.Trim();
            changed.Add("name");
        }

        var s3Fields = new[] { "endpoint", "region", "bucket", "pathPrefix", "accessKeyId", "secretAccessKey", "forcePathStyle" };
        if (destination.Kind == BackupDestinationKind.Local && s3Fields.Any(patch.Has))
            throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/" + s3Fields.First(patch.Has), "not_applicable", "A local destination has no S3 settings.")]));

        if (patch.Has("endpoint") && body.Endpoint is { } endpoint)
        {
            BackupDestinationRules.TryNormalizeEndpoint(endpoint, out var normalized, out _);
            destination.Endpoint = normalized;
            changed.Add("endpoint");
        }

        if (patch.Has("region")) { destination.Region = Blank(body.Region); changed.Add("region"); }
        if (patch.Has("bucket") && body.Bucket is { } bucket) { destination.Bucket = bucket.Trim(); changed.Add("bucket"); }
        if (patch.Has("pathPrefix")) { destination.PathPrefix = Blank(body.PathPrefix); changed.Add("pathPrefix"); }
        if (patch.Has("accessKeyId") && body.AccessKeyId is { } keyId) { destination.AccessKeyId = keyId.Trim(); changed.Add("accessKeyId"); }
        if (patch.Has("forcePathStyle") && body.ForcePathStyle is { } pathStyle) { destination.ForcePathStyle = pathStyle; changed.Add("forcePathStyle"); }
        if (patch.Has("secretAccessKey") && body.SecretAccessKey is { } secretValue)
        {
            if (destination.SecretAccessKeySecretId is { } secretId) vault.AddVersion(await db.Secrets.FirstAsync(s => s.Id == secretId, ct), secretValue);
            else
            {
                var secret = vault.Create(org, $"backup-destination/{destination.Id.ToString("N")[^12..]}", $"Access key of backup destination '{destination.Name}'.",
                    secretValue, purpose: SecretPurpose.BackupCredential);
                destination.SecretAccessKeySecretId = secret.Id;
                destination.SecretAccessKeySecret = secret;
            }

            changed.Add("secretAccessKey");
        }

        if (patch.Has("isDefault") && body.IsDefault is { } isDefault && isDefault != destination.IsDefault)
        {
            if (isDefault) await SetDefaultAsync(db, org, destination, ct);
            else destination.IsDefault = false;
            changed.Add("isDefault");
        }

        // What was tested no longer describes the destination once its connection settings changed.
        if (changed.Any(c => c is not ("name" or "isDefault"))) { destination.LastTestedAt = null; destination.LastTestError = null; }

        await audit.RecordAsync("backup_destination.updated", "backup_destination", id, new { name = destination.Name, changed }, ct);
        http.SetETag(destination.RowVersion);
        return TypedResults.Ok(ToResponse(destination));
    }

    private static async Task<NoContent> Delete(
        Guid id, string? confirm, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var destination = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(destination.RowVersion);
        Confirmation.Require(confirm, destination.Name);

        var users = await db.BackupPolicies.CountAsync(p => p.DestinationId == id, ct);
        if (users > 0)
            throw new ApiProblemException(ApiProblems.Conflict(BackupProblemCodes.DestinationInUse, $"{users} backup polic{(users == 1 ? "y uses" : "ies use")} this destination. Point them at another destination or remove them first."));

        var now = clock.UtcNow;
        destination.MarkDeleted(now);
        if (destination.SecretAccessKeySecretId is { } secretId) (await db.Secrets.FirstAsync(s => s.Id == secretId, ct)).MarkDeleted(now);
        await audit.RecordAsync("backup_destination.deleted", "backup_destination", id, new { name = destination.Name }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<BackupDestinationTestResponse>> Test(
        Guid id, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault, IBackupObjectStore store, IClock clock, CancellationToken ct)
    {
        var destination = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        StoreTestResult result;
        if (destination.Kind == BackupDestinationKind.Local)
            result = new StoreTestResult(true, null); // lives on the server of each workload: the first backup is the test
        else
        {
            var secret = await db.Secrets.FirstAsync(s => s.Id == destination.SecretAccessKeySecretId, ct);
            var (_, secretAccessKey) = await vault.RevealAsync(secret, null, ct);
            result = await store.TestAsync(BackupLocations.S3(destination, secretAccessKey), destination.PathPrefix, ct);
        }

        var now = clock.UtcNow;
        destination.LastTestedAt = now;
        destination.LastTestError = result.Ok ? null : Truncate(result.Error ?? "The test failed.");
        await audit.RecordAsync("backup_destination.tested", "backup_destination", id, new { name = destination.Name, ok = result.Ok }, ct);
        return TypedResults.Ok(new BackupDestinationTestResponse(result.Ok, destination.LastTestError, now));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    /// <summary>At most one default per organization.</summary>
    private static async Task SetDefaultAsync(AetheraDbContext db, Guid org, BackupDestination destination, CancellationToken ct)
    {
        foreach (var other in await db.BackupDestinations.Where(d => d.OrganizationId == org && d.IsDefault && d.Id != destination.Id).ToListAsync(ct))
            other.IsDefault = false;
        destination.IsDefault = true;
    }

    private static async Task<BackupDestination> FindAsync(AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.BackupDestinations.Where(d => d.OrganizationId == org);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("backup_destination", id));
    }

    private static async Task EnsureNameFreeAsync(AetheraDbContext db, Guid org, string name, Guid? exceptId, CancellationToken ct)
    {
        if (await db.BackupDestinations.AnyAsync(d => d.OrganizationId == org && d.Name == name && d.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("backup_destination", $"A backup destination named '{name}' already exists."));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string text) => text.Length > 900 ? text[..900] : text;

    internal static BackupDestinationResponse ToResponse(BackupDestination d) => new(
        d.Id, d.Name, d.Kind, d.Endpoint, d.Region, d.Bucket, d.PathPrefix, d.AccessKeyId, d.SecretAccessKeySecretId is not null, d.ForcePathStyle, d.IsDefault,
        d.LastTestedAt, d.LastTestError, d.CreatedAt, d.UpdatedAt);
}

using System.Text.RegularExpressions;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Domain;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Aethera.Api.Features.Resources.Workloads;

public sealed record CreateVolumeRequest
{
    /// <summary>The application or service the volume is mounted into (implied on <c>/applications/{id}/volumes</c>).</summary>
    public Guid? WorkloadId { get; init; }

    /// <summary>Docker volume name; derived from the mount path when absent.</summary>
    public string? Name { get; init; }

    /// <summary>Absolute path inside the container; not <c>/</c>.</summary>
    public string? MountPath { get; init; }

    /// <summary>Absolute host path for a bind mount; absent for a named Docker volume.</summary>
    public string? HostPath { get; init; }

    public bool? ReadOnly { get; init; }
    public bool? BackupEnabled { get; init; }
}

public sealed record UpdateVolumeRequest
{
    [NotClearable] public string? Name { get; init; }
    [NotClearable] public string? MountPath { get; init; }
    public string? HostPath { get; init; }
    [NotClearable] public bool? ReadOnly { get; init; }
    [NotClearable] public bool? BackupEnabled { get; init; }
}

public sealed record VolumeResponse(
    Guid Id, Guid WorkloadId, string Name, string MountPath, string? HostPath, bool ReadOnly, bool BackupEnabled,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public static partial class VolumeRules
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,254}$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>Absolute, not the root, no <c>..</c> segments, none of whitespace, <c>:</c>, <c>,</c>, quotes or control characters. Normalized (no <c>//</c>, no trailing slash).</summary>
    public static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = "";
        if (path is null) return false;
        var text = path.Trim();
        if (text.Length == 0 || text.Length > 500 || text[0] != '/') return false;
        if (text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ':' or ',' or '"' or '\'' or '\\')) return false;
        text = Regex.Replace(text, "/{2,}", "/").TrimEnd('/');
        if (text.Length == 0 || text.Split('/').Contains("..")) return false;
        normalized = text;
        return true;
    }

    public static string NameFromPath(string normalizedPath) => string.Join('-', normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static IRuleBuilderOptions<T, string?> MustBeAbsolutePath<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(p => TryNormalizePath(p, out _)).WithErrorCode("pattern")
            .WithMessage("Must be an absolute path other than '/', without '..', spaces, ':' or ','.");
}

public sealed class CreateVolumeValidator : AbstractValidator<CreateVolumeRequest>
{
    public CreateVolumeValidator()
    {
        RuleFor(x => x.MountPath).NotEmpty();
        RuleFor(x => x.MountPath).MustBeAbsolutePath().When(x => !string.IsNullOrEmpty(x.MountPath));
        RuleFor(x => x.HostPath).MustBeAbsolutePath().When(x => x.HostPath is not null);
        RuleFor(x => x.Name).Must(VolumeRules.IsValidName).WithErrorCode("pattern")
            .WithMessage("Must start with a letter or digit and contain only letters, digits, '_', '.' and '-' (at most 255 characters).")
            .When(x => x.Name is not null);
    }
}

public sealed class UpdateVolumeValidator : AbstractValidator<UpdateVolumeRequest>
{
    public UpdateVolumeValidator()
    {
        RuleFor(x => x.MountPath).MustBeAbsolutePath().When(x => x.MountPath is not null);
        RuleFor(x => x.HostPath).MustBeAbsolutePath().When(x => x.HostPath is not null);
        RuleFor(x => x.Name).Must(VolumeRules.IsValidName).WithErrorCode("pattern")
            .WithMessage("Must start with a letter or digit and contain only letters, digits, '_', '.' and '-' (at most 255 characters).")
            .When(x => x.Name is not null);
    }
}

internal static class VolumeEndpoints
{
    private static readonly SortDefinition<Volume> Sorts = new SortDefinition<Volume>("mountPath")
        .Add("mountPath", v => v.MountPath).Add("name", v => v.Name).Add("createdAt", v => v.CreatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/volumes").WithTags("Volumes");

        group.MapGet("/", List).WithName("listVolumes").RequireRead();
        group.MapPost("/", (CreateVolumeRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, CancellationToken ct) =>
                Create(request, request.WorkloadId, http, db, actor, audit, ct))
            .WithName("createVolume").Validate<CreateVolumeRequest>().RequireWrite();
        group.MapGet("/{id:guid}", Get).WithName("getVolume").RequireRead();
        group.MapPatch("/{id:guid}", Update).WithName("updateVolume")
            .Accepts<UpdateVolumeRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateVolumeRequest>().RequireWrite();
        group.MapDelete("/{id:guid}", Delete).WithName("deleteVolume").RequireWrite();

        foreach (var (collection, singular) in new[] { ("applications", "Application"), ("services", "Service") })
        {
            var nested = api.MapGroup($"/{collection}/{{workloadId:guid}}/volumes").WithTags("Volumes");
            var isApplication = singular == "Application";
            nested.MapGet("/", (Guid workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors,
                    [AsParameters] PageRequest page, string? sort, string? q, CancellationToken ct) =>
                    ListForWorkload(isApplication, workloadId, http, db, actor, cursors, page, sort, q, ct))
                .WithName($"list{singular}Volumes").RequireRead();
            nested.MapPost("/", async (Guid workloadId, CreateVolumeRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor,
                    IAuditLog audit, CancellationToken ct) =>
                {
                    await WorkloadSupport.RequireWorkloadRouteAsync(db, actor.Org(), isApplication, workloadId, ct);
                    if (request.WorkloadId is { } given && given != workloadId)
                        throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/workloadId", "mismatch",
                            "Does not match the application or service in the URL.")]));
                    return await Create(request, workloadId, http, db, actor, audit, ct);
                })
                .WithName($"create{singular}Volume").Validate<CreateVolumeRequest>().RequireWrite();
        }
    }

    private static async Task<Ok<Page<VolumeResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort,
        Guid? workloadId, string? q, CancellationToken ct)
    {
        http.RejectUnknownQuery("workloadId", "q");
        var query = db.VolumesOf(actor.Org()).AsNoTracking();
        if (workloadId is { } w) query = query.Where(v => v.WorkloadId == w);
        if (ResourceHttp.LikePattern(q) is { } like) query = query.Where(v => EF.Functions.ILike(v.Name, like, "\\") || EF.Functions.ILike(v.MountPath, like, "\\"));
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"workloadId={workloadId}&q={q}", ct);
        return TypedResults.Ok(new Page<VolumeResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Ok<Page<VolumeResponse>>> ListForWorkload(
        bool isApplication, Guid workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, PageRequest page,
        string? sort, string? q, CancellationToken ct)
    {
        http.RejectUnknownQuery("q");
        var org = actor.Org();
        await WorkloadSupport.RequireWorkloadRouteAsync(db, org, isApplication, workloadId, ct);

        var query = db.VolumesOf(org).AsNoTracking().Where(v => v.WorkloadId == workloadId);
        if (ResourceHttp.LikePattern(q) is { } like) query = query.Where(v => EF.Functions.ILike(v.Name, like, "\\") || EF.Functions.ILike(v.MountPath, like, "\\"));
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, $"workloadId={workloadId}&q={q}", ct);
        return TypedResults.Ok(new Page<VolumeResponse>(items.Select(ToResponse).ToList(), next));
    }

    private static async Task<Created<VolumeResponse>> Create(
        CreateVolumeRequest request, Guid? workloadId, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, CancellationToken ct)
    {
        var org = actor.Org();
        var workload = await db.RequireWorkloadAsync(org, workloadId, "/workloadId", ct);
        VolumeRules.TryNormalizePath(request.MountPath, out var mountPath);
        var name = request.Name ?? VolumeRules.NameFromPath(mountPath);
        if (!VolumeRules.IsValidName(name)) name = "volume-" + Guid.CreateVersion7().ToString("N")[^8..];
        await EnsureFreeAsync(db, workload.Id, name, mountPath, null, ct);

        var volume = new Volume
        {
            WorkloadId = workload.Id, Name = name, MountPath = mountPath,
            HostPath = request.HostPath is null ? null : Normalize(request.HostPath),
            ReadOnly = request.ReadOnly ?? false, BackupEnabled = request.BackupEnabled ?? false,
        };
        db.Volumes.Add(volume);
        await audit.RecordAsync("volume.created", "volume", volume.Id, new { workloadId = workload.Id, name, mountPath }, ct);
        http.SetETag(volume.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("volumes", volume.Id), ToResponse(volume));
    }

    private static async Task<Ok<VolumeResponse>> Get(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var volume = await FindAsync(db, actor.Org(), id, tracking: false, ct);
        http.SetETag(volume.RowVersion);
        return TypedResults.Ok(ToResponse(volume));
    }

    private static async Task<Ok<VolumeResponse>> Update(
        Guid id, PatchRequest<UpdateVolumeRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, CancellationToken ct)
    {
        var volume = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(volume.RowVersion);
        var body = patch.Body;

        var name = volume.Name;
        var mountPath = volume.MountPath;
        var changed = new List<string>();
        if (patch.Has("name") && body.Name is { } newName) { name = newName; changed.Add("name"); }
        if (patch.Has("mountPath") && body.MountPath is { } rawPath)
        {
            VolumeRules.TryNormalizePath(rawPath, out mountPath);
            changed.Add("mountPath");
        }

        if (name != volume.Name || mountPath != volume.MountPath)
        {
            await EnsureFreeAsync(db, volume.WorkloadId, name, mountPath, id, ct);
            volume.Name = name;
            volume.MountPath = mountPath;
        }

        if (patch.Has("hostPath")) { volume.HostPath = body.HostPath is null ? null : Normalize(body.HostPath); changed.Add("hostPath"); }
        if (patch.Has("readOnly") && body.ReadOnly is { } readOnly) { volume.ReadOnly = readOnly; changed.Add("readOnly"); }
        if (patch.Has("backupEnabled") && body.BackupEnabled is { } backup) { volume.BackupEnabled = backup; changed.Add("backupEnabled"); }

        await audit.RecordAsync("volume.updated", "volume", id, new { workloadId = volume.WorkloadId, name = volume.Name, changed }, ct);
        http.SetETag(volume.RowVersion);
        return TypedResults.Ok(ToResponse(volume));
    }

    private static async Task<NoContent> Delete(
        Guid id, string? confirm, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, CancellationToken ct)
    {
        var volume = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(volume.RowVersion);
        Confirmation.Require(confirm, volume.Name);
        db.Volumes.Remove(volume);
        await audit.RecordAsync("volume.deleted", "volume", id, new { workloadId = volume.WorkloadId, name = volume.Name, mountPath = volume.MountPath }, ct);
        return TypedResults.NoContent();
    }

    private static string Normalize(string path)
    {
        VolumeRules.TryNormalizePath(path, out var normalized);
        return normalized;
    }

    private static async Task<Volume> FindAsync(AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.VolumesOf(org);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(v => v.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("volume", id));
    }

    private static async Task EnsureFreeAsync(AetheraDbContext db, Guid workloadId, string name, string mountPath, Guid? exceptId, CancellationToken ct)
    {
        if (await db.Volumes.AnyAsync(v => v.WorkloadId == workloadId && v.MountPath == mountPath && v.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("volume", $"A volume is already mounted at '{mountPath}'."));
        if (await db.Volumes.AnyAsync(v => v.WorkloadId == workloadId && v.Name == name && v.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("volume", $"A volume named '{name}' already exists."));
    }

    internal static VolumeResponse ToResponse(Volume v) =>
        new(v.Id, v.WorkloadId, v.Name, v.MountPath, v.HostPath, v.ReadOnly, v.BackupEnabled, v.CreatedAt, v.UpdatedAt);
}

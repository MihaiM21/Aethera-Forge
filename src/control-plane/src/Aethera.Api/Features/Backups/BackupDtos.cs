using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Http.Errors;
using Aethera.Domain;
using Aethera.Infrastructure.Scheduling;
using FluentValidation;

namespace Aethera.Api.Features.Backups;

public static class BackupProblemCodes
{
    public const string DestinationInUse = "backup_destination.in_use";
    public const string RunNotRestorable = "backup_run.not_restorable";
    public const string PolicyNotSupported = "backup_policy.not_supported";
}

// ---- destinations ------------------------------------------------------------------------------------------------------------------

public sealed record CreateBackupDestinationRequest
{
    public string? Name { get; init; }

    /// <summary><c>s3</c> (default) or <c>local</c>.</summary>
    public BackupDestinationKind? Kind { get; init; }

    /// <summary><c>https://&lt;account id&gt;.r2.cloudflarestorage.com</c>; http only for loopback and private hosts (a local MinIO).</summary>
    public string? Endpoint { get; init; }

    public string? Region { get; init; }
    public string? Bucket { get; init; }
    public string? PathPrefix { get; init; }
    public string? AccessKeyId { get; init; }

    /// <summary>Write-only: stored as a managed secret, never returned.</summary>
    public string? SecretAccessKey { get; init; }

    public bool? ForcePathStyle { get; init; }
    public bool? IsDefault { get; init; }
}

public sealed record UpdateBackupDestinationRequest
{
    [NotClearable] public string? Name { get; init; }
    [NotClearable] public string? Endpoint { get; init; }
    public string? Region { get; init; }
    [NotClearable] public string? Bucket { get; init; }
    public string? PathPrefix { get; init; }
    [NotClearable] public string? AccessKeyId { get; init; }

    /// <summary>A new value adds a version to the stored secret.</summary>
    [NotClearable] public string? SecretAccessKey { get; init; }

    [NotClearable] public bool? ForcePathStyle { get; init; }
    [NotClearable] public bool? IsDefault { get; init; }
}

public sealed record BackupDestinationResponse(
    Guid Id, string Name, BackupDestinationKind Kind, string? Endpoint, string? Region, string? Bucket, string? PathPrefix, string? AccessKeyId,
    bool HasSecret, bool ForcePathStyle, bool IsDefault, DateTimeOffset? LastTestedAt, string? LastTestError, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record BackupDestinationTestResponse(bool Ok, string? Error, DateTimeOffset TestedAt);

public static partial class BackupDestinationRules
{
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{1,253}[A-Za-z0-9]\z")]
    private static partial Regex BucketPattern();

    public static bool IsValidBucket(string? bucket) => bucket is not null && BucketPattern().IsMatch(bucket);

    /// <summary>The prefix is a folder name inside the bucket: no leading or trailing slash, no <c>..</c>, no control characters.</summary>
    public static bool IsValidPrefix(string? prefix) =>
        prefix is null or "" || (!prefix.StartsWith('/') && !prefix.EndsWith('/') && !prefix.Contains("//") && !prefix.Split('/').Contains("..")
                                 && !prefix.Any(char.IsControl) && !prefix.Contains('\\'));

    /// <summary>
    /// https always; http only for a host that is not reachable from the internet: localhost, a private or loopback address, a single-label
    /// name (a container on the same Docker network) or <c>.local</c>/<c>.internal</c>/<c>.lan</c>. No credentials, no path.
    /// </summary>
    public static bool TryNormalizeEndpoint(string? endpoint, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            error = "Must be a URL such as https://<account id>.r2.cloudflarestorage.com.";
            return false;
        }

        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath.Trim('/').Length > 0)
        {
            error = "Must be an http(s) URL with only a host and an optional port: no credentials, path or query.";
            return false;
        }

        if (uri.Scheme == "http" && !IsPrivateHost(uri.Host))
        {
            error = "Use https. Plain http is only accepted for localhost, private addresses and local container names (a MinIO on the same network).";
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    public static bool IsPrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            }

            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        }

        return !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CreateBackupDestinationValidator : AbstractValidator<CreateBackupDestinationRequest>
{
    public CreateBackupDestinationValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Region).MaximumLength(100);
        RuleFor(x => x.PathPrefix).MaximumLength(500).Must(BackupDestinationRules.IsValidPrefix).WithErrorCode("pattern")
            .WithMessage("A folder name inside the bucket without leading or trailing slash, such as aethera or backups/prod.");
        When(x => (x.Kind ?? BackupDestinationKind.S3) == BackupDestinationKind.S3, () =>
        {
            RuleFor(x => x.Endpoint).NotEmpty().MaximumLength(500).Custom((endpoint, context) =>
            {
                if (!string.IsNullOrEmpty(endpoint) && !BackupDestinationRules.TryNormalizeEndpoint(endpoint, out _, out var error))
                    context.AddFailure(new FluentValidation.Results.ValidationFailure(context.PropertyPath, error) { ErrorCode = "pattern" });
            });
            RuleFor(x => x.Bucket).NotEmpty().Must(BackupDestinationRules.IsValidBucket).WithErrorCode("pattern")
                .WithMessage("A bucket name: 3-255 letters, digits, dots, hyphens and underscores.").When(x => !string.IsNullOrEmpty(x.Bucket));
            RuleFor(x => x.Bucket).NotEmpty();
            RuleFor(x => x.AccessKeyId).NotEmpty().MaximumLength(255);
            RuleFor(x => x.SecretAccessKey).NotEmpty().MaximumLength(SecretRules.MaxValueLength);
        });
    }
}

public sealed class UpdateBackupDestinationValidator : AbstractValidator<UpdateBackupDestinationRequest>
{
    public UpdateBackupDestinationValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        RuleFor(x => x.Region).MaximumLength(100);
        RuleFor(x => x.PathPrefix).MaximumLength(500).Must(BackupDestinationRules.IsValidPrefix).WithErrorCode("pattern")
            .WithMessage("A folder name inside the bucket without leading or trailing slash, such as aethera or backups/prod.");
        RuleFor(x => x.Endpoint).MaximumLength(500).Custom((endpoint, context) =>
        {
            if (endpoint is not null && !BackupDestinationRules.TryNormalizeEndpoint(endpoint, out _, out var error))
                context.AddFailure(new FluentValidation.Results.ValidationFailure(context.PropertyPath, error) { ErrorCode = "pattern" });
        });
        RuleFor(x => x.Bucket).Must(BackupDestinationRules.IsValidBucket).WithErrorCode("pattern").When(x => x.Bucket is not null);
        RuleFor(x => x.AccessKeyId).NotEmpty().MaximumLength(255).When(x => x.AccessKeyId is not null);
        RuleFor(x => x.SecretAccessKey).NotEmpty().MaximumLength(SecretRules.MaxValueLength).When(x => x.SecretAccessKey is not null);
    }
}

// ---- policies ----------------------------------------------------------------------------------------------------------------------

/// <summary>Creates or replaces the policy of a database or a volume (<c>kind</c> + <c>volumeId</c> identify it). Omitted settings get the defaults: daily 03:00 UTC, 7 daily, 4 weekly, 6 monthly.</summary>
public sealed record PutBackupPolicyRequest
{
    /// <summary><c>database</c> or <c>volume</c>.</summary>
    public BackupKind? Kind { get; init; }

    public Guid? VolumeId { get; init; }
    public Guid? DestinationId { get; init; }
    public bool? Enabled { get; init; }

    /// <summary>Five-field cron expression, evaluated in <c>timeZone</c>. At most every 15 minutes.</summary>
    public string? Schedule { get; init; }

    /// <summary>IANA time zone, default <c>UTC</c>.</summary>
    public string? TimeZone { get; init; }

    public int? KeepDaily { get; init; }
    public int? KeepWeekly { get; init; }
    public int? KeepMonthly { get; init; }
}

/// <summary>The policy of Aethera's own database (administrators).</summary>
public sealed record PutSystemBackupPolicyRequest
{
    public Guid? DestinationId { get; init; }
    public bool? Enabled { get; init; }
    public string? Schedule { get; init; }
    public string? TimeZone { get; init; }
    public int? KeepDaily { get; init; }
    public int? KeepWeekly { get; init; }
    public int? KeepMonthly { get; init; }
}

public sealed record BackupPolicyResponse(
    Guid Id, Guid? WorkloadId, BackupKind Kind, Guid? VolumeId, string? VolumeName, Guid DestinationId, string DestinationName, bool Enabled,
    string Schedule, string TimeZone, int KeepDaily, int KeepWeekly, int KeepMonthly, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt,
    IReadOnlyList<DateTimeOffset> NextRuns, BackupRunStatus? LastStatus, DateTimeOffset? LastSuccessAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>What a workload can be backed up as: its database (supported templates) and each named volume.</summary>
public sealed record BackupTargetResponse(BackupKind Kind, Guid? VolumeId, string Name, string? MountPath, bool Supported, string? Engine, string? Reason);

public sealed record WorkloadBackupsResponse(IReadOnlyList<BackupPolicyResponse> Policies, IReadOnlyList<BackupTargetResponse> Targets);

public sealed record SystemBackupPolicyResponse(BackupPolicyResponse? Policy, string DefaultSchedule);

public static class BackupPolicyRules
{
    public const string DefaultSchedule = "0 3 * * *";
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(15);
    public const int MaxKeep = 3650;

    public static void ValidateSchedule(string? schedule, string? timeZone, string pointerPrefix, List<FieldError> errors, DateTimeOffset now)
    {
        if (!CronSchedule.TryParse(schedule ?? DefaultSchedule, timeZone, out var parsed, out var error))
        {
            errors.Add(FieldError.AtPointer(error!.StartsWith("Unknown time zone", StringComparison.Ordinal) ? pointerPrefix + "/timeZone" : pointerPrefix + "/schedule", "invalid", error));
            return;
        }

        if (parsed.FiresMoreOftenThan(MinInterval, now))
            errors.Add(FieldError.AtPointer(pointerPrefix + "/schedule", "too_frequent", "Backups can run at most every 15 minutes."));
    }
}

public abstract class PolicyValidatorBase<T> : AbstractValidator<T> where T : class
{
    protected void Retention(Func<T, int?> daily, Func<T, int?> weekly, Func<T, int?> monthly)
    {
        RuleFor(x => daily(x)).InclusiveBetween(0, BackupPolicyRules.MaxKeep).When(x => daily(x) is not null).OverridePropertyName("keepDaily");
        RuleFor(x => weekly(x)).InclusiveBetween(0, BackupPolicyRules.MaxKeep).When(x => weekly(x) is not null).OverridePropertyName("keepWeekly");
        RuleFor(x => monthly(x)).InclusiveBetween(0, BackupPolicyRules.MaxKeep).When(x => monthly(x) is not null).OverridePropertyName("keepMonthly");
        RuleFor(x => x).Custom((x, context) =>
        {
            var total = (daily(x) ?? 7) + (weekly(x) ?? 4) + (monthly(x) ?? 6);
            if (total < 1) context.AddFailure(new FluentValidation.Results.ValidationFailure("keepDaily", "Keep at least one backup.") { ErrorCode = "range" });
        });
    }
}

public sealed class PutBackupPolicyValidator : PolicyValidatorBase<PutBackupPolicyRequest>
{
    public PutBackupPolicyValidator()
    {
        RuleFor(x => x.Kind).NotNull().Must(k => k is BackupKind.Database or BackupKind.Volume).WithErrorCode("invalid_enum").WithMessage("Must be database or volume.");
        RuleFor(x => x.DestinationId).NotNull();
        RuleFor(x => x.VolumeId).NotNull().When(x => x.Kind == BackupKind.Volume);
        RuleFor(x => x.VolumeId).Null().When(x => x.Kind == BackupKind.Database).WithMessage("A database policy has no volume.");
        Retention(x => x.KeepDaily, x => x.KeepWeekly, x => x.KeepMonthly);
    }
}

public sealed class PutSystemBackupPolicyValidator : PolicyValidatorBase<PutSystemBackupPolicyRequest>
{
    public PutSystemBackupPolicyValidator()
    {
        RuleFor(x => x.DestinationId).NotNull();
        Retention(x => x.KeepDaily, x => x.KeepWeekly, x => x.KeepMonthly);
    }
}

// ---- runs and restores -------------------------------------------------------------------------------------------------------------

public sealed record BackupRunResponse(
    Guid Id, Guid PolicyId, Guid? WorkloadId, Guid DestinationId, BackupKind Kind, BackupTrigger Trigger, BackupRunStatus Status, Guid? JobId, string? Engine,
    string? ObjectKey, long? RawSizeBytes, long? StoredSizeBytes, string? StoredSha256, string? KeyId, string? Error, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, long? DurationMs, DateTimeOffset? PrunedAt, bool Restorable);

public sealed record RestoreBackupRequest
{
    /// <summary><c>same</c>: into the service the backup came from (overwrites its data). <c>new</c>: into a new service made from the same template.</summary>
    public string? Target { get; init; }

    /// <summary>The exact name of the application or service the backup belongs to.</summary>
    public string? Confirm { get; init; }

    /// <summary>Name of the new service (target <c>new</c>).</summary>
    public string? NewServiceName { get; init; }
}

public sealed class RestoreBackupValidator : AbstractValidator<RestoreBackupRequest>
{
    public RestoreBackupValidator()
    {
        RuleFor(x => x.Target).NotEmpty().Must(t => t is "same" or "new").WithErrorCode("invalid_enum").WithMessage("Must be same or new.");
        RuleFor(x => x.Confirm).MaximumLength(200);
        RuleFor(x => x.NewServiceName).NotEmpty().MaximumLength(200).When(x => x.Target == "new");
    }
}

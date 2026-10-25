using System.Globalization;
using Aethera.Domain;
using Aethera.Domain.Transport;

namespace Aethera.Infrastructure.Backups;

/// <summary>Where objects of a backup destination live and how they are named (ADR 0010 "Objects").</summary>
public static class BackupLocations
{
    /// <summary>
    /// <c>&lt;prefix&gt;/&lt;org8&gt;/&lt;workload slug&gt;-&lt;workload8&gt;/&lt;kind&gt;/&lt;UTC yyyyMMdd'T'HHmmss'Z'&gt;-&lt;run8&gt;.&lt;ext&gt;.aeb</c>. The organization and run
    /// parts are the random tail of their UUIDv7 (the head is a timestamp), the workload part matches its container name. The control plane's
    /// own backup has no workload: <c>&lt;org8&gt;/aethera/controlplane/...</c>.
    /// </summary>
    public static string ObjectKey(
        string? prefix, Guid organizationId, string? workloadSlug, Guid? workloadId, BackupKind kind, DateTimeOffset at, Guid runId, string extension)
    {
        var owner = workloadId is { } id && workloadSlug is not null ? $"{workloadSlug}-{id.ToString("N")[..8]}" : "aethera";
        var kindName = kind switch { BackupKind.Database => "database", BackupKind.Volume => "volume", _ => "controlplane" };
        var stamp = at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var path = $"{Tail(organizationId)}/{owner}/{kindName}/{stamp}-{Tail(runId)}.{extension}.aeb";
        return string.IsNullOrEmpty(prefix) ? path : $"{prefix.Trim('/')}/{path}";
    }

    private static string Tail(Guid id) => id.ToString("N")[^8..];

    /// <summary>The location of <paramref name="objectKey"/> in <paramref name="destination"/>, ready for the agent.</summary>
    public static BackupLocationSpec Location(BackupDestination destination, string secretAccessKey, string objectKey) =>
        destination.Kind == BackupDestinationKind.Local
            ? new LocalBackupLocationSpec(objectKey)
            : new S3LocationSpec(destination.Endpoint!, destination.Region, destination.Bucket!, objectKey, destination.AccessKeyId!, new SecretValue(secretAccessKey), destination.ForcePathStyle);

    public static S3Target S3(BackupDestination destination, string secretAccessKey) =>
        new(destination.Endpoint!, destination.Region, destination.Bucket!, destination.AccessKeyId!, new SecretValue(secretAccessKey), destination.ForcePathStyle);
}

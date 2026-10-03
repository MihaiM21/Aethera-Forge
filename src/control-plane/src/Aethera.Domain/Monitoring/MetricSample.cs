namespace Aethera.Domain;

public enum MetricResolution
{
    Raw = 0,
    FiveMinutes = 1,
    OneHour = 2,
}

/// <summary>
/// Time-series sample for a server (ContainerId null) or one of its containers. Uses a bigint identity key
/// because of volume; not a <see cref="Entity"/>.
/// </summary>
public class MetricSample
{
    public long Id { get; set; }
    public Guid ServerId { get; set; }
    public Server Server { get; set; } = null!;
    public string? ContainerId { get; set; }
    public Guid? WorkloadId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public MetricResolution Resolution { get; set; } = MetricResolution.Raw;
    public double? CpuPercent { get; set; }
    public long? MemoryUsedBytes { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public long? DiskUsedBytes { get; set; }
    public long? DiskTotalBytes { get; set; }
    public long? NetRxBytes { get; set; }
    public long? NetTxBytes { get; set; }
    public double? Load1 { get; set; }
    public double? Load5 { get; set; }
    public double? Load15 { get; set; }
}

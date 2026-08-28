using System.Text.Json.Serialization;

namespace PerformanceMonitor.Models;

public sealed class PerformanceSnapshot
{
    [JsonRequired]
    public int Version { get; init; } = 1;
    public string ComputerName { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public CpuMetrics? Cpu { get; init; } = new();
    public GpuMetrics? Gpu { get; init; } = new();
    public MemoryMetrics? Memory { get; init; } = new();
    public DiskMetrics? Disk { get; init; } = new();
    public NetworkMetrics? Network { get; init; } = new();
    public FanMetrics? Fan { get; init; } = new();
}

public sealed class CpuMetrics
{
    public double? Usage { get; init; }
    public double? Temperature { get; init; }
    public double? Power { get; init; }
}

public sealed class GpuMetrics
{
    public string? Name { get; init; }
    public double? Usage { get; init; }
    public double? Temperature { get; init; }
    public double? VramUsedMb { get; init; }
    public double? VramTotalMb { get; init; }
}

public sealed class MemoryMetrics
{
    public double? UsedMb { get; init; }
    public double? TotalMb { get; init; }
}

public sealed class DiskMetrics
{
    public double? Active { get; init; }
    public double? ReadMb { get; init; }
    public double? WriteMb { get; init; }
}

public sealed class NetworkMetrics
{
    public double? DownloadMbps { get; init; }
    public double? UploadMbps { get; init; }
}

public sealed class FanMetrics
{
    public double? Rpm { get; init; }
}

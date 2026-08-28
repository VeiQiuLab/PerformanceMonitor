using PerformanceMonitor.Models;

namespace PerformanceMonitor.Services;

internal interface ILocalMetricsCollector : IDisposable
{
    PerformanceSnapshot Collect();
}
internal sealed class LocalMetricsCollector : ILocalMetricsCollector
{
    private readonly NativeSystemSampler _native = new();
    private readonly PdhDiskSampler _disk = new();
    private readonly HardwareSensorReader _hardware = new();
    private bool _disposed;

    public PerformanceSnapshot Collect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var hardware = TrySampleHardware();
        var nativeCpu = TrySampleCpu();
        var memory = TrySampleMemory();
        var disk = TrySampleDisk();
        var network = TrySampleNetwork();

        return new PerformanceSnapshot
        {
            ComputerName = Environment.MachineName,
            Timestamp = DateTimeOffset.UtcNow,
            Cpu = new CpuMetrics
            {
                Usage = hardware.CpuUsage ?? nativeCpu,
                Temperature = hardware.CpuTemperature,
                Power = hardware.CpuPower
            },
            Gpu = new GpuMetrics
            {
                Name = hardware.GpuName,
                Usage = hardware.GpuUsage,
                Temperature = hardware.GpuTemperature,
                VramUsedMb = hardware.VramUsedMb,
                VramTotalMb = hardware.VramTotalMb
            },
            Memory = new MemoryMetrics { UsedMb = memory.UsedMb, TotalMb = memory.TotalMb },
            Disk = new DiskMetrics { Active = disk.Active, ReadMb = disk.ReadMb, WriteMb = disk.WriteMb },
            Network = new NetworkMetrics
            {
                DownloadMbps = network.DownloadMbps,
                UploadMbps = network.UploadMbps
            },
            Fan = new FanMetrics { Rpm = hardware.FanRpm }
        };
    }

    private HardwareMetrics TrySampleHardware()
    {
        try
        {
            return _hardware.Sample();
        }
        catch (Exception)
        {
            return new HardwareMetrics();
        }
    }

    private double? TrySampleCpu()
    {
        try
        {
            return _native.SampleCpuUsage();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private (double? UsedMb, double? TotalMb) TrySampleMemory()
    {
        try
        {
            return _native.SampleMemory();
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private (double? Active, double? ReadMb, double? WriteMb) TrySampleDisk()
    {
        try
        {
            return _disk.Sample();
        }
        catch (Exception)
        {
            return (null, null, null);
        }
    }

    private (double? DownloadMbps, double? UploadMbps) TrySampleNetwork()
    {
        try
        {
            return _native.SampleNetwork();
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hardware.Dispose();
        _disk.Dispose();
    }
}

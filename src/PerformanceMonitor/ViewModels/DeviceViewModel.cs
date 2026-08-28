using System.Globalization;
using System.Windows;
using PerformanceMonitor.Models;

namespace PerformanceMonitor.ViewModels;

internal sealed class DeviceViewModel : BindableBase
{
    private PerformanceSnapshot? _snapshot;

    public DeviceViewModel(Guid id, string displayName, bool isLocal)
    {
        Id = id;
        DisplayName = displayName;
        IsLocal = isLocal;
        IsOnline = isLocal;
    }

    public Guid Id { get; }
    public string DisplayName { get; private set; }
    public bool IsLocal { get; }
    public bool IsOnline { get; private set; }
    public DateTimeOffset? LastSuccess { get; private set; }

    public string StatusText => IsOnline ? "Online" : "Offline";
    public string ComputerText => _snapshot?.ComputerName is { Length: > 0 } computerName
        ? IsLocal ? $"{computerName}  •  Local" : computerName
        : IsLocal ? "Local" : "Waiting for data";
    public string LastUpdatedText => _snapshot?.Timestamp is { } timestamp
        ? $"Updated {timestamp.ToLocalTime():HH:mm:ss}"
        : "Updated --";

    public double CpuUsageValue => PercentValue(_snapshot?.Cpu?.Usage);
    public string CpuUsageText => PercentText(_snapshot?.Cpu?.Usage);
    public string CpuTemperatureText => NumberText(_snapshot?.Cpu?.Temperature, "0 °C");
    public string CpuPowerText => NumberText(_snapshot?.Cpu?.Power, "0.0 W");

    public string GpuNameText => string.IsNullOrWhiteSpace(_snapshot?.Gpu?.Name) ? "GPU" : _snapshot!.Gpu!.Name!;
    public double GpuUsageValue => PercentValue(_snapshot?.Gpu?.Usage);
    public string GpuUsageText => PercentText(_snapshot?.Gpu?.Usage);
    public string GpuTemperatureText => NumberText(_snapshot?.Gpu?.Temperature, "0 °C");
    public string VramText => PairMbText(_snapshot?.Gpu?.VramUsedMb, _snapshot?.Gpu?.VramTotalMb);

    public double MemoryUsageValue => CalculatePercent(_snapshot?.Memory?.UsedMb, _snapshot?.Memory?.TotalMb);
    public string MemoryUsageText => MemoryUsageValue > 0 || _snapshot?.Memory?.UsedMb == 0
        ? $"{MemoryUsageValue:0}%"
        : "--";
    public string MemoryText => PairMbText(_snapshot?.Memory?.UsedMb, _snapshot?.Memory?.TotalMb);

    public string DiskActiveText => PercentText(_snapshot?.Disk?.Active);
    public string DiskReadText => NumberText(_snapshot?.Disk?.ReadMb, "0.0 MB/s");
    public string DiskWriteText => NumberText(_snapshot?.Disk?.WriteMb, "0.0 MB/s");
    public string DownloadText => NumberText(_snapshot?.Network?.DownloadMbps, "0.00 Mbps");
    public string UploadText => NumberText(_snapshot?.Network?.UploadMbps, "0.00 Mbps");
    public string FanText => NumberText(_snapshot?.Fan?.Rpm, "0 RPM");
    public Visibility FanVisibility => _snapshot?.Fan?.Rpm.HasValue == true ? Visibility.Visible : Visibility.Collapsed;

    public void Apply(DeviceStateUpdate update)
    {
        DisplayName = update.DisplayName;
        IsOnline = update.IsOnline;
        LastSuccess = update.LastSuccess;
        if (update.Snapshot is not null)
        {
            _snapshot = update.Snapshot;
        }

        RaiseAll();
    }

    public void Rename(string displayName)
    {
        DisplayName = displayName;
        RaiseAll();
    }

    private static double PercentValue(double? value) => value.HasValue && double.IsFinite(value.Value)
        ? Math.Clamp(value.Value, 0d, 100d)
        : 0d;

    private static string PercentText(double? value) => value.HasValue && double.IsFinite(value.Value)
        ? $"{value.Value:0}%"
        : "--";

    private static string NumberText(double? value, string format) => value.HasValue && double.IsFinite(value.Value)
        ? value.Value.ToString(format, CultureInfo.CurrentCulture)
        : "--";

    private static string PairMbText(double? usedMb, double? totalMb)
    {
        if (!usedMb.HasValue || !totalMb.HasValue || !double.IsFinite(usedMb.Value) || !double.IsFinite(totalMb.Value))
        {
            return "-- / --";
        }

        return $"{FormatCapacity(usedMb.Value)} / {FormatCapacity(totalMb.Value)}";
    }

    private static string FormatCapacity(double megabytes) => megabytes >= 1024d
        ? $"{megabytes / 1024d:0.0} GB"
        : $"{megabytes:0} MB";

    private static double CalculatePercent(double? used, double? total) =>
        used.HasValue && total is > 0 ? Math.Clamp(used.Value / total.Value * 100d, 0d, 100d) : 0d;
}

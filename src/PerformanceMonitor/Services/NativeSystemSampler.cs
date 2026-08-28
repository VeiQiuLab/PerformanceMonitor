using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PerformanceMonitor.Services;

internal sealed class NativeSystemSampler
{
    private static readonly string[] VirtualInterfaceMarkers =
    [
        "virtual", "vmware", "hyper-v", "vethernet", "loopback", "tunnel", "tap", "vpn", "bluetooth"
    ];

    private readonly Dictionary<string, (long Received, long Sent)> _networkCounters = new(StringComparer.Ordinal);
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private long _previousNetworkTimestamp;
    private bool _hasCpuSample;
    private bool _hasNetworkSample;

    public double? SampleCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        var currentIdle = idle.ToUInt64();
        var currentKernel = kernel.ToUInt64();
        var currentUser = user.ToUInt64();

        if (!_hasCpuSample)
        {
            _previousIdle = currentIdle;
            _previousKernel = currentKernel;
            _previousUser = currentUser;
            _hasCpuSample = true;
            return null;
        }

        var idleDelta = currentIdle - _previousIdle;
        var kernelDelta = currentKernel - _previousKernel;
        var userDelta = currentUser - _previousUser;
        _previousIdle = currentIdle;
        _previousKernel = currentKernel;
        _previousUser = currentUser;

        var total = kernelDelta + userDelta;
        if (total == 0 || idleDelta > total)
        {
            return null;
        }

        return Math.Clamp(100d * (total - idleDelta) / total, 0d, 100d);
    }

    public (double? UsedMb, double? TotalMb) SampleMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0)
        {
            return (null, null);
        }

        const double bytesPerMb = 1024d * 1024d;
        return ((status.TotalPhysical - status.AvailablePhysical) / bytesPerMb, status.TotalPhysical / bytesPerMb);
    }

    public (double? DownloadMbps, double? UploadMbps) SampleNetwork()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var active = new Dictionary<string, (long Received, long Sent)>(StringComparer.Ordinal);

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (!IsUseful(networkInterface))
                {
                    continue;
                }

                var statistics = networkInterface.GetIPStatistics();
                active[networkInterface.Id] = (statistics.BytesReceived, statistics.BytesSent);
            }
            catch (NetworkInformationException)
            {
                // Interfaces can disappear between enumeration and statistics access.
            }
        }

        if (!_hasNetworkSample)
        {
            ReplaceNetworkCounters(active);
            _previousNetworkTimestamp = now;
            _hasNetworkSample = true;
            return (null, null);
        }

        var elapsedSeconds = (now - _previousNetworkTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        long receivedDelta = 0;
        long sentDelta = 0;

        foreach (var (id, current) in active)
        {
            if (_networkCounters.TryGetValue(id, out var previous))
            {
                if (current.Received >= previous.Received)
                {
                    receivedDelta += current.Received - previous.Received;
                }

                if (current.Sent >= previous.Sent)
                {
                    sentDelta += current.Sent - previous.Sent;
                }
            }
        }

        ReplaceNetworkCounters(active);
        _previousNetworkTimestamp = now;
        if (elapsedSeconds <= 0)
        {
            return (null, null);
        }

        const double bitsPerMegabit = 1_000_000d;
        return (receivedDelta * 8d / elapsedSeconds / bitsPerMegabit, sentDelta * 8d / elapsedSeconds / bitsPerMegabit);
    }

    private void ReplaceNetworkCounters(Dictionary<string, (long Received, long Sent)> active)
    {
        _networkCounters.Clear();
        foreach (var pair in active)
        {
            _networkCounters[pair.Key] = pair.Value;
        }
    }

    private static bool IsUseful(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up ||
            networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        var identity = $"{networkInterface.Name} {networkInterface.Description}".ToLowerInvariant();
        if (VirtualInterfaceMarkers.Any(identity.Contains))
        {
            return false;
        }

        return networkInterface.Supports(NetworkInterfaceComponent.IPv4) ||
               networkInterface.Supports(NetworkInterfaceComponent.IPv6);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

using System.Runtime.InteropServices;

namespace PerformanceMonitor.Services;

internal sealed class PdhDiskSampler : IDisposable
{
    private const uint PdhFormatDouble = 0x00000200;
    private IntPtr _query;
    private IntPtr _activeCounter;
    private IntPtr _readCounter;
    private IntPtr _writeCounter;
    private bool _disposed;

    public PdhDiskSampler()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }

        _activeCounter = AddCounter(@"\PhysicalDisk(_Total)\% Disk Time");
        _readCounter = AddCounter(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _writeCounter = AddCounter(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _ = PdhCollectQueryData(_query);
    }

    public (double? Active, double? ReadMb, double? WriteMb) Sample()
    {
        if (_disposed || _query == IntPtr.Zero || PdhCollectQueryData(_query) != 0)
        {
            return (null, null, null);
        }

        var active = ReadCounter(_activeCounter);
        var readBytes = ReadCounter(_readCounter);
        var writeBytes = ReadCounter(_writeCounter);
        const double bytesPerMb = 1024d * 1024d;
        return (
            active.HasValue ? Math.Clamp(active.Value, 0d, 100d) : null,
            readBytes.HasValue ? Math.Max(0d, readBytes.Value / bytesPerMb) : null,
            writeBytes.HasValue ? Math.Max(0d, writeBytes.Value / bytesPerMb) : null);
    }

    private IntPtr AddCounter(string path) =>
        PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    private static double? ReadCounter(IntPtr counter)
    {
        if (counter == IntPtr.Zero ||
            PdhGetFormattedCounterValue(counter, PdhFormatDouble, out _, out var value) != 0 ||
            value.Status != 0 ||
            !double.IsFinite(value.Value))
        {
            return null;
        }

        return value.Value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_query != IntPtr.Zero)
        {
            _ = PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        out uint type,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        public uint Status;
        public double Value;
    }
}

using PerformanceMonitor.Models;

namespace PerformanceMonitor.Services;

internal sealed class SnapshotStore
{
    private PerformanceSnapshot? _snapshot;

    public PerformanceSnapshot? Current => Volatile.Read(ref _snapshot);

    public void Update(PerformanceSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
}

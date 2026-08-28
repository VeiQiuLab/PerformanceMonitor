using PerformanceMonitor.Models;

namespace PerformanceMonitor.Services;

internal sealed class LocalMonitorService : IAsyncDisposable
{
    private readonly Func<ILocalMetricsCollector> _collectorFactory;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;
    private bool _disposed;

    public LocalMonitorService(Func<ILocalMetricsCollector>? collectorFactory = null, TimeSpan? interval = null)
    {
        _collectorFactory = collectorFactory ?? (() => new LocalMetricsCollector());
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    public event EventHandler<PerformanceSnapshot>? SnapshotUpdated;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        ILocalMetricsCollector? collector = null;
        try
        {
            collector = _collectorFactory();
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    var snapshot = collector.Collect();
                    SnapshotUpdated?.Invoke(this, snapshot);
                }
                catch (Exception) when (!_cancellation.IsCancellationRequested)
                {
                    // The next tick retries after a device-specific or transient sampler failure.
                }

                await Task.Delay(_interval, _cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception) when (!_cancellation.IsCancellationRequested)
        {
            // Collector creation can fail on unsupported machines; the UI remains usable with null data.
        }
        finally
        {
            collector?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellation.Dispose();
    }
}

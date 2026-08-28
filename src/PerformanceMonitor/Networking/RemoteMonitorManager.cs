using System.Net.Http;
using PerformanceMonitor.Configuration;

namespace PerformanceMonitor.Networking;

internal sealed class RemoteMonitorManager : IAsyncDisposable
{
    private readonly HttpClient _client = RemoteDeviceMonitor.CreateHttpClient();
    private readonly List<RemoteDeviceMonitor> _monitors = [];
    private bool _disposed;

    public event EventHandler<RemoteDeviceState>? StateChanged;

    public async Task ApplyAsync(IEnumerable<RemoteDeviceSettings> devices)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopAllAsync().ConfigureAwait(false);
        foreach (var device in devices)
        {
            var monitor = new RemoteDeviceMonitor(device, _client);
            monitor.StateChanged += OnStateChanged;
            _monitors.Add(monitor);
            monitor.Start();
        }
    }

    private void OnStateChanged(object? sender, RemoteDeviceState state) => StateChanged?.Invoke(this, state);

    private async Task StopAllAsync()
    {
        var monitors = _monitors.ToArray();
        _monitors.Clear();
        foreach (var monitor in monitors)
        {
            monitor.StateChanged -= OnStateChanged;
        }

        await Task.WhenAll(monitors.Select(async monitor => await monitor.DisposeAsync().ConfigureAwait(false)))
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAllAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}

using System.Net.Sockets;
using PerformanceMonitor.Configuration;
using PerformanceMonitor.Models;
using PerformanceMonitor.Networking;
using PerformanceMonitor.Services;

namespace PerformanceMonitor;

internal sealed class AppController : IAsyncDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly SnapshotStore _snapshotStore = new();
    private readonly LocalMonitorService _localMonitor = new();
    private readonly RemoteMonitorManager _remoteMonitors = new();
    private readonly StartupTaskService _startupTask = new();
    private readonly bool _manageStartupTask;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private AppSettings _settings;
    private LanApiServer? _server;
    private bool _started;
    private bool _disposed;

    public AppController(SettingsStore settingsStore, AppSettings settings, bool manageStartupTask = true)
    {
        _settingsStore = settingsStore;
        _settings = settings.Clone();
        _manageStartupTask = manageStartupTask;
        _localMonitor.SnapshotUpdated += OnLocalSnapshot;
        _remoteMonitors.StateChanged += OnRemoteStateChanged;
    }

    public event EventHandler<DeviceStateUpdate>? DeviceStateChanged;
    public event EventHandler<string?>? ServiceMessageChanged;

    public AppSettings CurrentSettings => _settings.Clone();

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _localMonitor.Start();
        try
        {
            if (_manageStartupTask)
            {
                _startupTask.SetEnabled(_settings.StartWithWindows);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ServiceMessageChanged?.Invoke(this, "The Windows startup task could not be synchronized.");
        }

        try
        {
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ServiceMessageChanged?.Invoke(this, "Settings could not be saved; monitoring will continue for this session.");
        }

        await _remoteMonitors.ApplyAsync(_settings.RemoteDevices).ConfigureAwait(false);
        await RestartServerAsync().ConfigureAwait(false);
    }

    public async Task ApplySettingsAsync(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        settings.Normalize();
        await _configurationGate.WaitAsync().ConfigureAwait(false);
        var previousStartupSetting = _settings.StartWithWindows;
        try
        {
            if (_manageStartupTask)
            {
                _startupTask.SetEnabled(settings.StartWithWindows);
            }
            _settings = settings.Clone();
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
            await _remoteMonitors.ApplyAsync(_settings.RemoteDevices).ConfigureAwait(false);
            await RestartServerAsync().ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (_manageStartupTask)
                {
                    _startupTask.SetEnabled(previousStartupSetting);
                }
            }
            catch (Exception rollbackException) when (rollbackException is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The original settings failure is more useful than a best-effort rollback failure.
            }

            throw;
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task SaveWindowSettingsAsync(WindowSettings window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _configurationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _settings.Window = window.Clone();
            await _settingsStore.SaveAsync(_settings).ConfigureAwait(false);
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    private async Task RestartServerAsync()
    {
        var previous = Interlocked.Exchange(ref _server, null);
        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        if (!_settings.LanServer.Enabled)
        {
            ServiceMessageChanged?.Invoke(this, null);
            return;
        }

        LanApiServer? candidate = null;
        try
        {
            candidate = new LanApiServer(
                _settings.LanServer.Port,
                _settings.LanServer.AccessToken,
                () => _snapshotStore.Current);
            candidate.Start();
            _server = candidate;
            ServiceMessageChanged?.Invoke(this, $"LAN monitoring is listening on port {_settings.LanServer.Port}.");
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException)
        {
            if (candidate is not null)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }

            ServiceMessageChanged?.Invoke(this, $"LAN monitoring could not start on port {_settings.LanServer.Port}.");
        }
    }

    private void OnLocalSnapshot(object? sender, PerformanceSnapshot snapshot)
    {
        _snapshotStore.Update(snapshot);
        DeviceStateChanged?.Invoke(this, new DeviceStateUpdate(
            Guid.Empty,
            snapshot.ComputerName,
            true,
            true,
            snapshot.Timestamp,
            snapshot));
    }

    private void OnRemoteStateChanged(object? sender, RemoteDeviceState state) =>
        DeviceStateChanged?.Invoke(this, new DeviceStateUpdate(
            state.DeviceId,
            state.DisplayName,
            false,
            state.IsOnline,
            state.LastSuccess,
            state.Snapshot));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localMonitor.SnapshotUpdated -= OnLocalSnapshot;
        _remoteMonitors.StateChanged -= OnRemoteStateChanged;

        var server = Interlocked.Exchange(ref _server, null);
        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }

        await _remoteMonitors.DisposeAsync().ConfigureAwait(false);
        await _localMonitor.DisposeAsync().ConfigureAwait(false);
        _configurationGate.Dispose();
    }
}

internal sealed record DeviceStateUpdate(
    Guid DeviceId,
    string DisplayName,
    bool IsLocal,
    bool IsOnline,
    DateTimeOffset? LastSuccess,
    PerformanceSnapshot? Snapshot);

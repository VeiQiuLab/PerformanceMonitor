using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PerformanceMonitor.Configuration;
using PerformanceMonitor.Models;

namespace PerformanceMonitor.Networking;

internal sealed class RemoteDeviceMonitor : IAsyncDisposable
{
    private const int MaximumResponseBytes = 256 * 1024;
    private readonly RemoteDeviceSettings _settings;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _offlineAfter;
    private readonly TimeSpan _requestTimeout;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;
    private DateTimeOffset? _lastSuccess;
    private PerformanceSnapshot? _lastSnapshot;
    private bool _disposed;

    public RemoteDeviceMonitor(
        RemoteDeviceSettings settings,
        HttpClient? client = null,
        TimeSpan? pollInterval = null,
        TimeSpan? offlineAfter = null,
        TimeSpan? requestTimeout = null)
    {
        _settings = settings.Clone();
        _client = client ?? CreateHttpClient();
        _ownsClient = client is null;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _offlineAfter = offlineAfter ?? TimeSpan.FromSeconds(5);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(3);
    }

    public event EventHandler<RemoteDeviceState>? StateChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null)
        {
            return;
        }

        Publish(false);
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await PollOnceAsync(_cancellation.Token).ConfigureAwait(false);
                await Task.Delay(_pollInterval, _cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildEndpoint(_settings));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccessToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                Publish(IsStillOnline());
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var payload = await ReadLimitedAsync(stream, MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
            var snapshot = JsonSerializer.Deserialize<PerformanceSnapshot>(payload, JsonDefaults.Api);
            if (snapshot is null || snapshot.Version != 1 || string.IsNullOrWhiteSpace(snapshot.ComputerName) ||
                snapshot.Timestamp == default)
            {
                Publish(IsStillOnline());
                return;
            }

            _lastSnapshot = snapshot;
            _lastSuccess = DateTimeOffset.UtcNow;
            Publish(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException or UriFormatException)
        {
            Publish(IsStillOnline());
        }
    }

    private bool IsStillOnline() =>
        _lastSuccess.HasValue && DateTimeOffset.UtcNow - _lastSuccess.Value < _offlineAfter;

    private void Publish(bool isOnline) => StateChanged?.Invoke(this, new RemoteDeviceState(
        _settings.Id,
        _settings.DisplayName,
        isOnline,
        _lastSuccess,
        _lastSnapshot));

    private static Uri BuildEndpoint(RemoteDeviceSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Host.Contains('/') || settings.Host.Contains('\\'))
        {
            throw new UriFormatException("Remote host is invalid.");
        }

        return new UriBuilder(Uri.UriSchemeHttp, settings.Host.Trim().Trim('[', ']'), settings.Port, "/api/v1/status").Uri;
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return memory.ToArray();
            }

            if (memory.Length + read > maximumBytes)
            {
                throw new IOException("Remote response is too large.");
            }

            memory.Write(buffer, 0, read);
        }
    }

    internal static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 2,
            UseProxy = false
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}

internal sealed record RemoteDeviceState(
    Guid DeviceId,
    string DisplayName,
    bool IsOnline,
    DateTimeOffset? LastSuccess,
    PerformanceSnapshot? Snapshot);

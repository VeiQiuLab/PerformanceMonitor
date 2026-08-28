using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PerformanceMonitor.Models;

namespace PerformanceMonitor.Networking;

internal sealed class LanApiServer : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private readonly Func<PerformanceSnapshot?> _snapshotProvider;
    private readonly byte[] _expectedToken;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _clientSlots = new(8, 8);
    private readonly object _taskGate = new();
    private readonly HashSet<Task> _clientTasks = [];
    private Task? _acceptLoop;
    private bool _disposed;

    public LanApiServer(int port, string accessToken, Func<PerformanceSnapshot?> snapshotProvider, IPAddress? bindAddress = null)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _expectedToken = Encoding.UTF8.GetBytes(accessToken);
        _listener = new TcpListener(bindAddress ?? IPAddress.Any, port)
        {
            ExclusiveAddressUse = false
        };
    }

    public int Port => _listener.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_acceptLoop is not null)
        {
            return;
        }

        _listener.Start(32);
        _acceptLoop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cancellation.Token).ConfigureAwait(false);
                if (!_clientSlots.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                TrackClient(client);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_cancellation.IsCancellationRequested)
        {
        }
    }

    private void TrackClient(TcpClient client)
    {
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? task = null;
        task = RunClientAsync(client, startGate.Task, () => task!);

        lock (_taskGate)
        {
            _clientTasks.Add(task);
        }

        startGate.SetResult();
    }

    private async Task RunClientAsync(TcpClient client, Task startGate, Func<Task> currentTask)
    {
        await startGate.ConfigureAwait(false);
        try
        {
            await HandleClientAsync(client, _cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or
                                          ObjectDisposedException or JsonException or InvalidOperationException or
                                          ArgumentException or NotSupportedException)
        {
            // A malformed, disconnected, or timing-out LAN client is isolated to this request.
        }
        finally
        {
            client.Dispose();
            _clientSlots.Release();
            lock (_taskGate)
            {
                _clientTasks.Remove(currentTask());
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        client.NoDelay = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var stream = client.GetStream();
        var requestText = await ReadHeadersAsync(stream, timeout.Token).ConfigureAwait(false);
        if (requestText is null)
        {
            await WriteResponseAsync(stream, 400, "Bad Request", null, timeout.Token).ConfigureAwait(false);
            return;
        }

        var request = ParseRequest(requestText);
        if (!request.IsValid)
        {
            await WriteResponseAsync(stream, 400, "Bad Request", null, timeout.Token).ConfigureAwait(false);
            return;
        }

        if (!request.Method.Equals("GET", StringComparison.Ordinal))
        {
            await WriteResponseAsync(stream, 405, "Method Not Allowed", null, timeout.Token).ConfigureAwait(false);
            return;
        }

        if (!request.Path.Equals("/api/v1/status", StringComparison.Ordinal))
        {
            await WriteResponseAsync(stream, 404, "Not Found", null, timeout.Token).ConfigureAwait(false);
            return;
        }

        if (!IsAuthorized(request.Authorization))
        {
            await WriteResponseAsync(stream, 401, "Unauthorized", null, timeout.Token).ConfigureAwait(false);
            return;
        }

        var snapshot = _snapshotProvider();
        if (snapshot is null)
        {
            await WriteResponseAsync(stream, 503, "Service Unavailable", null, timeout.Token).ConfigureAwait(false);
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonDefaults.Api);
        await WriteResponseAsync(stream, 200, "OK", payload, timeout.Token).ConfigureAwait(false);
    }

    private bool IsAuthorized(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(authorization[prefix.Length..].Trim());
        return presented.Length == _expectedToken.Length && CryptographicOperations.FixedTimeEquals(presented, _expectedToken);
    }

    private static async Task<string?> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumHeaderBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            count += read;
            if (count >= 4 && buffer[count - 4] == '\r' && buffer[count - 3] == '\n' &&
                buffer[count - 2] == '\r' && buffer[count - 1] == '\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, count);
            }
        }

        return null;
    }

    private static ParsedRequest ParseRequest(string text)
    {
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return ParsedRequest.Invalid;
        }

        string? authorization = null;
        var authorizationCount = 0;
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
            {
                break;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                return ParsedRequest.Invalid;
            }

            if (line[..separator].Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                authorization = line[(separator + 1)..].Trim();
                authorizationCount++;
            }
        }

        if (authorizationCount > 1)
        {
            return ParsedRequest.Invalid;
        }

        return new ParsedRequest(true, requestLine[0], requestLine[1], authorization);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        byte[]? payload,
        CancellationToken cancellationToken)
    {
        payload ??= [];
        var contentType = payload.Length > 0 ? "application/json; charset=utf-8" : "text/plain; charset=utf-8";
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
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
        _listener.Stop();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] clients;
        lock (_taskGate)
        {
            clients = _clientTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _clientSlots.Dispose();
        _cancellation.Dispose();
    }

    private sealed record ParsedRequest(bool IsValid, string Method, string Path, string? Authorization)
    {
        public static readonly ParsedRequest Invalid = new(false, string.Empty, string.Empty, null);
    }
}

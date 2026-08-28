using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PerformanceMonitor.Configuration;
using PerformanceMonitor.Models;
using PerformanceMonitor.Networking;
using PerformanceMonitor.Services;

namespace PerformanceMonitor.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Test)> Tests =
    [
        ("corrupt configuration falls back and atomic save reloads", TestConfigurationAsync),
        ("CPU temperature selection is prioritized and range checked", TestCpuTemperatureSelectionAsync),
        ("CPU power selection accepts only real aggregate power sensors", TestCpuPowerSelectionAsync),
        ("local CPU, RAM and network sampling returns usable data", TestLocalSamplingAsync),
        ("LAN API authorizes valid token and rejects invalid token", TestLanAuthorizationAsync),
        ("remote loopback becomes offline and recovers automatically", TestOfflineAndRecoveryAsync),
        ("unreachable IP and wrong port remain isolated", TestUnreachableEndpointsAsync),
        ("wrong token leaves remote device offline", TestWrongRemoteTokenAsync),
        ("malformed JSON is rejected without crashing", TestMalformedJsonAsync),
        ("missing GPU and Fan values are accepted as null", TestMissingOptionalSensorsAsync)
    ];

    private static Task TestCpuTemperatureSelectionAsync()
    {
        var package = HardwareSensorReader.SelectCpuTemperature(
        [
            ("Core (Tctl/Tdie)", 62d),
            ("CPU Package", 68d),
            ("CPU Core #1", 60d)
        ]);
        Assert(package == 68d, "CPU Package did not win the documented priority order");

        var amd = HardwareSensorReader.SelectCpuTemperature(
        [
            ("Core (Tctl/Tdie)", 57.5d),
            ("CCD1 (Tdie)", 55d)
        ]);
        Assert(amd == 57.5d, "AMD Core (Tctl/Tdie) was not selected");

        var average = HardwareSensorReader.SelectCpuTemperature(
        [
            ("CPU Core #1", 50d),
            ("CPU Core #2", 60d)
        ]);
        Assert(average == 55d, "individual core temperatures were not averaged");

        var invalid = HardwareSensorReader.SelectCpuTemperature(
        [
            ("CPU Package", double.NaN),
            ("Core (Tctl/Tdie)", 0d),
            ("Package", 250d)
        ]);
        Assert(invalid is null, "invalid CPU temperature was accepted");
        return Task.CompletedTask;
    }

    private static Task TestCpuPowerSelectionAsync()
    {
        var package = HardwareSensorReader.SelectCpuPower(
        [
            ("Core #1 (SMU)", 8d),
            ("CPU PPT", 72d),
            ("Package Power", 65d)
        ]);
        Assert(package == 65d, "Package Power did not win the documented priority order");

        var ppt = HardwareSensorReader.SelectCpuPower(
        [
            ("Package", 0d),
            ("CPU PPT (SMU)", 70d)
        ]);
        Assert(ppt == 70d, "AMD CPU PPT sensor was not accepted");

        var invalid = HardwareSensorReader.SelectCpuPower(
        [
            ("CPU TDP", 45d),
            ("PL1 Limit", 65d),
            ("Core #1 (SMU)", 7d),
            ("Package", double.PositiveInfinity)
        ]);
        Assert(invalid is null, "a limit, TDP, core-only, or invalid power value was accepted");
        return Task.CompletedTask;
    }

    public static async Task<int> Main()
    {
        var failures = new List<string>();
        foreach (var (name, test) in Tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                failures.Add($"{name}: {exception.Message}");
                Console.WriteLine($"FAIL  {name}\n      {exception}");
            }
        }

        Console.WriteLine($"\n{Tests.Count - failures.Count}/{Tests.Count} tests passed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task TestConfigurationAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PerformanceMonitor-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(path, "{ definitely not json");
            var store = new SettingsStore(path);
            var defaults = store.Load();
            Assert(defaults.LanServer.Port == 52100, "corrupt config did not use the default port");
            Assert(!defaults.LanServer.Enabled, "LAN server must default to disabled");
            Assert(defaults.LanServer.AccessToken.Length >= 32, "default token was not random and strong");

            defaults.LanServer.Enabled = true;
            defaults.StartWithWindows = true;
            defaults.RemoteDevices.Add(new RemoteDeviceSettings
            {
                DisplayName = "Laptop",
                Host = "127.0.0.1",
                Port = 52100,
                AccessToken = "test-token"
            });
            await store.SaveAsync(defaults);
            var reloaded = store.Load();
            Assert(reloaded.StartWithWindows, "saved startup setting did not reload");
            Assert(reloaded.LanServer.Enabled, "saved LAN setting did not reload");
            Assert(reloaded.RemoteDevices.Count == 1, "saved remote device did not reload");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task TestLocalSamplingAsync()
    {
        using var collector = new LocalMetricsCollector();
        _ = collector.Collect();
        await Task.Delay(1100);
        var snapshot = collector.Collect();
        Assert(snapshot.Cpu?.Usage.HasValue == true, "CPU usage was unavailable");
        Assert(snapshot.Memory?.TotalMb is > 0, "total RAM was unavailable");
        Assert(snapshot.Memory?.UsedMb is > 0, "used RAM was unavailable");
        Assert(snapshot.Network?.DownloadMbps.HasValue == true, "network download rate was unavailable");
        Assert(snapshot.Network?.UploadMbps.HasValue == true, "network upload rate was unavailable");
    }

    private static async Task TestLanAuthorizationAsync()
    {
        const string token = "correct-test-token";
        await using var server = StartServer(token, IPAddress.Loopback);
        using var client = CreateClient();
        var endpoint = new Uri($"http://127.0.0.1:{server.Port}/api/v1/status");

        using var validRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
        validRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var valid = await client.SendAsync(validRequest);
        Assert(valid.StatusCode == HttpStatusCode.OK, $"valid token returned {(int)valid.StatusCode}");
        var json = await valid.Content.ReadAsStringAsync();
        Assert(json.Contains("\"computerName\"", StringComparison.Ordinal), "API did not use the V1 JSON contract");
        Assert(!json.Contains(token, StringComparison.Ordinal), "API leaked the access token");

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
        invalidRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        using var invalid = await client.SendAsync(invalidRequest);
        Assert(invalid.StatusCode == HttpStatusCode.Unauthorized, "wrong token was not rejected");

        using var missing = await client.GetAsync(endpoint);
        Assert(missing.StatusCode == HttpStatusCode.Unauthorized, "missing token was not rejected");
    }

    private static async Task TestOfflineAndRecoveryAsync()
    {
        const string token = "recovery-token";
        var server = StartServer(token, IPAddress.Loopback);
        var port = server.Port;
        var box = new StateBox();
        await using var monitor = new RemoteDeviceMonitor(
            RemoteSettings(port, token),
            pollInterval: TimeSpan.FromMilliseconds(80),
            offlineAfter: TimeSpan.FromMilliseconds(350),
            requestTimeout: TimeSpan.FromMilliseconds(250));
        monitor.StateChanged += (_, state) => box.Value = state;
        monitor.Start();

        await WaitUntilAsync(() => box.Value?.IsOnline == true, TimeSpan.FromSeconds(3), "remote never became online");
        await server.DisposeAsync();
        await WaitUntilAsync(() => box.Value?.IsOnline == false, TimeSpan.FromSeconds(3), "remote never became offline");

        server = StartServer(token, IPAddress.Loopback, port);
        try
        {
            await WaitUntilAsync(() => box.Value?.IsOnline == true, TimeSpan.FromSeconds(3), "remote did not recover");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static async Task TestUnreachableEndpointsAsync()
    {
        foreach (var settings in new[]
                 {
                     new RemoteDeviceSettings { DisplayName = "Unreachable", Host = "192.0.2.1", Port = 52100, AccessToken = "x" },
                     new RemoteDeviceSettings { DisplayName = "Wrong port", Host = "127.0.0.1", Port = FindUnusedPort(), AccessToken = "x" }
                 })
        {
            var box = new StateBox();
            await using var monitor = new RemoteDeviceMonitor(settings, requestTimeout: TimeSpan.FromMilliseconds(250));
            monitor.StateChanged += (_, state) => box.Value = state;
            await monitor.PollOnceAsync();
            Assert(box.Value?.IsOnline == false, $"{settings.DisplayName} unexpectedly became online");
        }
    }

    private static async Task TestWrongRemoteTokenAsync()
    {
        await using var server = StartServer("server-token", IPAddress.Loopback);
        var box = new StateBox();
        await using var monitor = new RemoteDeviceMonitor(RemoteSettings(server.Port, "wrong-token"));
        monitor.StateChanged += (_, state) => box.Value = state;
        await monitor.PollOnceAsync();
        Assert(box.Value?.IsOnline == false, "remote accepted the wrong token");
    }

    private static async Task TestMalformedJsonAsync()
    {
        using var client = new HttpClient(new StubHandler("{ malformed", HttpStatusCode.OK));
        var box = new StateBox();
        await using var monitor = new RemoteDeviceMonitor(RemoteSettings(52100, "x"), client);
        monitor.StateChanged += (_, state) => box.Value = state;
        await monitor.PollOnceAsync();
        Assert(box.Value?.IsOnline == false, "malformed JSON was treated as valid");
    }

    private static async Task TestMissingOptionalSensorsAsync()
    {
        var snapshot = CreateSnapshot().WithOptionalSensorsMissing();
        var json = JsonSerializer.Serialize(snapshot, JsonDefaults.Api);
        using var client = new HttpClient(new StubHandler(json, HttpStatusCode.OK));
        var box = new StateBox();
        await using var monitor = new RemoteDeviceMonitor(RemoteSettings(52100, "x"), client);
        monitor.StateChanged += (_, state) => box.Value = state;
        await monitor.PollOnceAsync();
        Assert(box.Value?.IsOnline == true, "missing optional sensors invalidated the whole snapshot");
        Assert(box.Value?.Snapshot?.Gpu?.Usage is null, "missing GPU usage was not preserved as null");
        Assert(box.Value?.Snapshot?.Fan?.Rpm is null, "missing Fan RPM was not preserved as null");
    }

    private static PerformanceSnapshot WithOptionalSensorsMissing(this PerformanceSnapshot snapshot) => new()
    {
        ComputerName = snapshot.ComputerName,
        Timestamp = snapshot.Timestamp,
        Cpu = snapshot.Cpu,
        Memory = snapshot.Memory,
        Disk = snapshot.Disk,
        Network = snapshot.Network,
        Gpu = new GpuMetrics(),
        Fan = new FanMetrics()
    };

    private static LanApiServer StartServer(string token, IPAddress address, int port = 0)
    {
        var server = new LanApiServer(port, token, CreateSnapshot, address);
        server.Start();
        return server;
    }

    private static PerformanceSnapshot CreateSnapshot() => new()
    {
        ComputerName = "Test-PC",
        Timestamp = DateTimeOffset.UtcNow,
        Cpu = new CpuMetrics { Usage = 12.5, Temperature = null, Power = null },
        Gpu = new GpuMetrics { Name = "Test GPU", Usage = 20, Temperature = 45, VramUsedMb = 512, VramTotalMb = 4096 },
        Memory = new MemoryMetrics { UsedMb = 4096, TotalMb = 16384 },
        Disk = new DiskMetrics { Active = 1, ReadMb = 0, WriteMb = 0 },
        Network = new NetworkMetrics { DownloadMbps = 0, UploadMbps = 0 },
        Fan = new FanMetrics { Rpm = null }
    };

    private static RemoteDeviceSettings RemoteSettings(int port, string token) => new()
    {
        DisplayName = "Loopback",
        Host = "127.0.0.1",
        Port = port,
        AccessToken = token
    };

    private static HttpClient CreateClient() => new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static int FindUnusedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var stop = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < stop)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(30);
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StateBox
    {
        private RemoteDeviceState? _value;
        public RemoteDeviceState? Value
        {
            get => Volatile.Read(ref _value);
            set => Volatile.Write(ref _value, value);
        }
    }

    private sealed class StubHandler(string content, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }
}

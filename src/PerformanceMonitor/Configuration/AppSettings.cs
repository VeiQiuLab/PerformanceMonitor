using System.Security.Cryptography;

namespace PerformanceMonitor.Configuration;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public WindowSettings Window { get; set; } = new();
    public LanServerSettings LanServer { get; set; } = new();
    public List<RemoteDeviceSettings> RemoteDevices { get; set; } = [];

    public AppSettings Clone() => new()
    {
        StartWithWindows = StartWithWindows,
        Window = Window.Clone(),
        LanServer = LanServer.Clone(),
        RemoteDevices = RemoteDevices.Select(device => device.Clone()).ToList()
    };

    public void Normalize()
    {
        Window ??= new WindowSettings();
        LanServer ??= new LanServerSettings();
        RemoteDevices ??= [];

        Window.Width = double.IsFinite(Window.Width) ? Math.Clamp(Window.Width, 720, 3840) : 1180;
        Window.Height = double.IsFinite(Window.Height) ? Math.Clamp(Window.Height, 520, 2160) : 760;
        Window.Left = double.IsFinite(Window.Left) ? Window.Left : -1;
        Window.Top = double.IsFinite(Window.Top) ? Window.Top : -1;
        LanServer.Port = IsValidPort(LanServer.Port) ? LanServer.Port : 52100;
        if (string.IsNullOrWhiteSpace(LanServer.AccessToken))
        {
            LanServer.AccessToken = AccessTokenGenerator.Create();
        }

        var seenIds = new HashSet<Guid>();
        RemoteDevices = RemoteDevices
            .Where(device => device is not null)
            .Select(device =>
            {
                device.Id = device.Id == Guid.Empty || !seenIds.Add(device.Id) ? Guid.NewGuid() : device.Id;
                seenIds.Add(device.Id);
                device.DisplayName = string.IsNullOrWhiteSpace(device.DisplayName) ? "Remote PC" : device.DisplayName.Trim();
                device.Host = device.Host?.Trim() ?? string.Empty;
                device.Port = IsValidPort(device.Port) ? device.Port : 52100;
                device.AccessToken = device.AccessToken?.Trim() ?? string.Empty;
                return device;
            })
            .Where(device => !string.IsNullOrWhiteSpace(device.Host))
            .ToList();
    }

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;
}

public sealed class WindowSettings
{
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public double Width { get; set; } = 1180;
    public double Height { get; set; } = 760;
    public bool IsMaximized { get; set; }

    public WindowSettings Clone() => (WindowSettings)MemberwiseClone();
}

public sealed class LanServerSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 52100;
    public string AccessToken { get; set; } = AccessTokenGenerator.Create();

    public LanServerSettings Clone() => (LanServerSettings)MemberwiseClone();
}

public sealed class RemoteDeviceSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = "Remote PC";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 52100;
    public string AccessToken { get; set; } = string.Empty;

    public RemoteDeviceSettings Clone() => (RemoteDeviceSettings)MemberwiseClone();
}

internal static class AccessTokenGenerator
{
    public static string Create()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

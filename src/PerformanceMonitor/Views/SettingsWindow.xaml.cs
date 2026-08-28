using System.Collections.ObjectModel;
using System.Windows;
using PerformanceMonitor.Configuration;

namespace PerformanceMonitor.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    internal SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings.Clone();
        RemoteDevices = new ObservableCollection<RemoteDeviceSettings>(_settings.RemoteDevices);
        DataContext = this;
    }

    public LanServerSettings LanServer => _settings.LanServer;
    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set => _settings.StartWithWindows = value;
    }
    public ObservableCollection<RemoteDeviceSettings> RemoteDevices { get; }
    internal AppSettings Result => _settings;

    private void OnRegenerateToken(object sender, RoutedEventArgs e)
    {
        LanServer.AccessToken = AccessTokenGenerator.Create();
        DataContext = null;
        DataContext = this;
    }

    private void OnAddRemote(object sender, RoutedEventArgs e)
    {
        var remote = new RemoteDeviceSettings
        {
            DisplayName = "Remote PC",
            Host = "192.168.1.2",
            Port = 52100
        };
        RemoteDevices.Add(remote);
        RemoteGrid.SelectedItem = remote;
        RemoteGrid.ScrollIntoView(remote);
    }

    private void OnRemoveRemote(object sender, RoutedEventArgs e)
    {
        if (RemoteGrid.SelectedItem is RemoteDeviceSettings selected)
        {
            RemoteDevices.Remove(selected);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        RemoteGrid.CommitEdit();
        RemoteGrid.CommitEdit();
        _settings.RemoteDevices = RemoteDevices.ToList();
        var error = ValidateSettings(_settings);
        if (error is not null)
        {
            ValidationMessage.Text = error;
            return;
        }

        _settings.Normalize();
        DialogResult = true;
    }

    private static string? ValidateSettings(AppSettings settings)
    {
        if (!AppSettings.IsValidPort(settings.LanServer.Port))
        {
            return "LAN listen port must be between 1 and 65535.";
        }

        if (string.IsNullOrWhiteSpace(settings.LanServer.AccessToken))
        {
            return "LAN access token cannot be empty.";
        }

        foreach (var device in settings.RemoteDevices)
        {
            if (string.IsNullOrWhiteSpace(device.DisplayName) || string.IsNullOrWhiteSpace(device.Host))
            {
                return "Each remote device needs a display name and host/IP.";
            }

            if (device.Host.Contains("://", StringComparison.Ordinal) || device.Host.Contains('/') || device.Host.Contains('\\'))
            {
                return $"Host/IP for {device.DisplayName} must not contain a URL scheme or path.";
            }

            if (!AppSettings.IsValidPort(device.Port) || string.IsNullOrWhiteSpace(device.AccessToken))
            {
                return $"Check the port and access token for {device.DisplayName}.";
            }
        }

        return null;
    }
}

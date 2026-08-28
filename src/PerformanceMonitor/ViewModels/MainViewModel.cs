using System.Collections.ObjectModel;
using PerformanceMonitor.Configuration;

namespace PerformanceMonitor.ViewModels;

internal sealed class MainViewModel : BindableBase
{
    private string? _serviceMessage;

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public string? ServiceMessage
    {
        get => _serviceMessage;
        set => Set(ref _serviceMessage, value);
    }

    public void Reconcile(AppSettings settings)
    {
        var wanted = new HashSet<Guid>(settings.RemoteDevices.Select(device => device.Id)) { Guid.Empty };
        for (var index = Devices.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(Devices[index].Id))
            {
                Devices.RemoveAt(index);
            }
        }

        var local = Devices.FirstOrDefault(device => device.Id == Guid.Empty);
        if (local is null)
        {
            Devices.Insert(0, new DeviceViewModel(Guid.Empty, Environment.MachineName, true));
        }

        foreach (var remote in settings.RemoteDevices)
        {
            var existing = Devices.FirstOrDefault(device => device.Id == remote.Id);
            if (existing is null)
            {
                Devices.Add(new DeviceViewModel(remote.Id, remote.DisplayName, false));
            }
            else
            {
                existing.Rename(remote.DisplayName);
            }
        }
    }

    public void Apply(DeviceStateUpdate update)
    {
        var device = Devices.FirstOrDefault(candidate => candidate.Id == update.DeviceId);
        if (device is null)
        {
            device = new DeviceViewModel(update.DeviceId, update.DisplayName, update.IsLocal);
            Devices.Add(device);
        }

        device.Apply(update);
    }
}

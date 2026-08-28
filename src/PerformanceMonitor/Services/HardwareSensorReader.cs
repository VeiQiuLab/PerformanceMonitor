using LibreHardwareMonitor.Hardware;

namespace PerformanceMonitor.Services;

internal sealed class HardwareSensorReader : IDisposable
{
    private Computer? _computer;

    public HardwareSensorReader()
    {
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true
            };
            computer.Open();
            _computer = computer;
        }
        catch (Exception)
        {
            _computer?.Close();
            _computer = null;
        }
    }

    public HardwareMetrics Sample()
    {
        if (_computer is null)
        {
            return new HardwareMetrics();
        }

        var roots = _computer.Hardware.ToArray();
        foreach (var root in roots)
        {
            UpdateRecursively(root);
        }

        var hardware = Flatten(roots).ToArray();
        var cpus = roots.Where(item => item.HardwareType == HardwareType.Cpu).ToArray();
        var gpu = hardware
            .Where(item => item.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .OrderByDescending(item => item.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
            .ThenByDescending(item => ReadMemoryMb(item, "GPU Memory Total", "D3D Dedicated Memory Total") ?? 0d)
            .FirstOrDefault();

        var fan = hardware
            .SelectMany(item => item.Sensors)
            .Where(sensor => sensor.SensorType == SensorType.Fan)
            .Select(SafeValue)
            .Where(value => value is > 0)
            .OrderByDescending(value => value)
            .FirstOrDefault();

        return new HardwareMetrics
        {
            CpuUsage = ReadCpuUsage(cpus),
            CpuTemperature = SelectCpuTemperature(ReadCpuSensors(cpus, SensorType.Temperature)),
            CpuPower = SelectCpuPower(ReadCpuSensors(cpus, SensorType.Power)),
            GpuName = gpu?.Name,
            GpuUsage = ReadValue(gpu, SensorType.Load, "GPU Core", "GPU Total"),
            GpuTemperature = PositiveOrNull(ReadValue(gpu, SensorType.Temperature, "GPU Core", "GPU Temperature")),
            VramUsedMb = ReadMemoryMb(gpu, "GPU Memory Used", "D3D Dedicated Memory Used"),
            VramTotalMb = ReadMemoryMb(gpu, "GPU Memory Total", "D3D Dedicated Memory Total"),
            FanRpm = fan
        };
    }

    private static void UpdateRecursively(IHardware hardware)
    {
        try
        {
            hardware.Update();
        }
        catch (Exception)
        {
            // One unsupported device must not prevent its siblings from updating.
        }

        foreach (var child in hardware.SubHardware)
        {
            UpdateRecursively(child);
        }
    }

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> roots)
    {
        foreach (var hardware in roots)
        {
            yield return hardware;
            foreach (var child in Flatten(hardware.SubHardware))
            {
                yield return child;
            }
        }
    }

    private static double? ReadCpuUsage(IEnumerable<IHardware> cpus)
    {
        var sensors = ReadCpuSensors(cpus, SensorType.Load).ToArray();
        var total = sensors.FirstOrDefault(sensor => NameEquals(sensor.Name, "CPU Total"));
        if (IsFinite(total.Value))
        {
            return total.Value;
        }

        // A per-core load is not a valid fallback for total CPU usage. Returning null
        // lets LocalMetricsCollector use its native whole-system CPU sampler instead.
        return null;
    }

    private static IEnumerable<(string Name, double? Value)> ReadCpuSensors(
        IEnumerable<IHardware> cpuRoots,
        SensorType type) => cpuRoots
        .SelectMany(root => Flatten(new[] { root }))
        .SelectMany(hardware => hardware.Sensors)
        .Where(sensor => sensor.SensorType == type)
        .Select(sensor => (sensor.Name, SafeValue(sensor)));

    internal static double? SelectCpuTemperature(IEnumerable<(string Name, double? Value)> readings)
    {
        var candidates = readings.Where(reading => IsValidCpuTemperature(reading.Value)).ToArray();
        var preferred = FindFirstNamed(candidates,
            "CPU Package",
            "CPU (Tctl/Tdie)",
            "Core (Tctl/Tdie)",
            "Core Average",
            "CPU Core Average",
            "Package",
            "Core Max",
            "CCDs Average (Tdie)",
            "CCDs Max (Tdie)");
        if (preferred.HasValue)
        {
            return preferred;
        }

        var aggregate = candidates.FirstOrDefault(reading => IsAggregateCpuTemperatureName(reading.Name));
        if (aggregate.Value.HasValue)
        {
            return aggregate.Value;
        }

        var coreValues = candidates
            .Where(reading => IsIndividualCoreTemperatureName(reading.Name))
            .Select(reading => reading.Value!.Value)
            .ToArray();
        return coreValues.Length > 0 ? coreValues.Average() : null;
    }

    internal static double? SelectCpuPower(IEnumerable<(string Name, double? Value)> readings)
    {
        var candidates = readings.Where(reading => IsValidCpuPower(reading.Value)).ToArray();
        var preferred = FindFirstNamed(candidates,
            "CPU Package",
            "Package Power",
            "CPU Package Power",
            "CPU PPT",
            "PPT",
            "Package",
            "Total Power");
        if (preferred.HasValue)
        {
            return preferred;
        }

        var aggregate = candidates.FirstOrDefault(reading => IsAggregateCpuPowerName(reading.Name));
        return aggregate.Value;
    }

    private static double? FindFirstNamed(
        IReadOnlyCollection<(string Name, double? Value)> candidates,
        params string[] names)
    {
        foreach (var name in names)
        {
            var match = candidates.FirstOrDefault(candidate => NameEquals(candidate.Name, name));
            if (match.Value.HasValue)
            {
                return match.Value;
            }
        }

        return null;
    }

    private static bool IsAggregateCpuTemperatureName(string name)
    {
        if (ContainsAny(name, "Distance", "Limit", "TjMax"))
        {
            return false;
        }

        return ContainsAny(name, "Package", "Tctl/Tdie", "Core Average", "Core Max", "CCDs Average", "CCDs Max");
    }

    private static bool IsIndividualCoreTemperatureName(string name) =>
        name.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
        name.Contains('#') &&
        !ContainsAny(name, "Distance", "Limit", "TjMax", "Average", "Max");

    private static bool IsAggregateCpuPowerName(string name)
    {
        if (ContainsAny(name, "TDP", "PL1", "PL2", "Limit", "Maximum", "Max"))
        {
            return false;
        }

        return name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("PPT", StringComparison.OrdinalIgnoreCase) ||
               NameEquals(name, "Total Power");
    }

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool NameEquals(string value, string expected) =>
        value.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidCpuTemperature(double? value) =>
        value is > 0d and <= 150d && double.IsFinite(value.Value);

    private static bool IsValidCpuPower(double? value) =>
        value is > 0d and <= 2000d && double.IsFinite(value.Value);

    private static bool IsFinite(double? value) => value.HasValue && double.IsFinite(value.Value);

    private static double? ReadValue(IHardware? hardware, SensorType type, params string[] preferredNames)
    {
        if (hardware is null)
        {
            return null;
        }

        var sensors = hardware.Sensors.Where(sensor => sensor.SensorType == type).ToArray();
        foreach (var name in preferredNames)
        {
            var exact = sensors.FirstOrDefault(sensor => sensor.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var value = SafeValue(exact);
            if (value.HasValue)
            {
                return value;
            }
        }

        return SafeValue(sensors.FirstOrDefault());
    }

    private static double? ReadMemoryMb(IHardware? hardware, params string[] preferredNames)
    {
        if (hardware is null)
        {
            return null;
        }

        var sensors = hardware.Sensors
            .Where(sensor => sensor.SensorType is SensorType.SmallData or SensorType.Data)
            .ToArray();
        foreach (var name in preferredNames)
        {
            var sensor = sensors.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var value = SafeValue(sensor);
            if (value.HasValue)
            {
                return sensor!.SensorType == SensorType.Data ? value.Value * 1024d : value.Value;
            }
        }

        return null;
    }

    private static double? SafeValue(ISensor? sensor)
    {
        var value = sensor?.Value;
        return value.HasValue && float.IsFinite(value.Value) ? value.Value : null;
    }

    private static double? PositiveOrNull(double? value) => value is > 0 ? value : null;

    public void Dispose()
    {
        var computer = Interlocked.Exchange(ref _computer, null);
        if (computer is null)
        {
            return;
        }

        try
        {
            computer.Close();
        }
        catch (Exception)
        {
            // There is no remaining managed resource to recover after close.
        }
    }
}

internal sealed class HardwareMetrics
{
    public double? CpuUsage { get; init; }
    public double? CpuTemperature { get; init; }
    public double? CpuPower { get; init; }
    public string? GpuName { get; init; }
    public double? GpuUsage { get; init; }
    public double? GpuTemperature { get; init; }
    public double? VramUsedMb { get; init; }
    public double? VramTotalMb { get; init; }
    public double? FanRpm { get; init; }
}

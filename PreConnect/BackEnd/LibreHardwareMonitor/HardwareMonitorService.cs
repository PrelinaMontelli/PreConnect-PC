using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace PreConnect.BackEnd.LibreHardwareMonitor;

public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer;
    private readonly object _syncRoot = new();
    private bool _disposed;

    public HardwareMonitorService(
        bool enableCpu = true,
        bool enableGpu = true,
        bool enableMemory = true,
        bool enableMotherboard = true,
        bool enableStorage = true,
        bool enableController = false,
        bool enableNetwork = false)
    {
        _computer = new Computer
        {
            IsCpuEnabled = enableCpu,
            IsGpuEnabled = enableGpu,
            IsMemoryEnabled = enableMemory,
            IsMotherboardEnabled = enableMotherboard,
            IsStorageEnabled = enableStorage,
            IsControllerEnabled = enableController,
            IsNetworkEnabled = enableNetwork
        };

        _computer.Open();
    }

    /// <summary>
    /// ���ذ���ȫ��Ӳ���ڵ㼰�䴫�������������ա����ᱣ����ʷ���ݣ����ú�����ص�ǰ�ɼ������ݡ�
    /// </summary>
    public HardwareMonitorSnapshot GetSnapshot()
    {
        EnsureNotDisposed();

        lock (_syncRoot)
        {
            var components = new List<HardwareComponentSnapshot>();
            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                components.Add(BuildComponentSnapshot(hardware, hardware.Name));
            }

            return new HardwareMonitorSnapshot(components);
        }
    }

    /// <summary>
    /// ƽ�����д���������
    /// </summary>
    public IReadOnlyList<SensorReading> GetAllSensorReadings()
    {
        return GetSnapshot()
            .Components
            .SelectMany(FlattenSensors)
            .Select(s => new SensorReading(s.HardwarePath, s.SensorName, s.SensorType, s.Value))
            .ToList();
    }

    private HardwareComponentSnapshot BuildComponentSnapshot(IHardware hardware, string path)
    {
        var sensors = new List<SensorSnapshot>();
        foreach (var sensor in hardware.Sensors)
        {
            sensors.Add(new SensorSnapshot(
                SensorId: sensor.Identifier.ToString(),
                SensorName: sensor.Name,
                SensorType: sensor.SensorType,
                Value: NormalizeSensorValue(sensor.Value),
                Min: NormalizeSensorValue(sensor.Min),
                Max: NormalizeSensorValue(sensor.Max),
                HardwarePath: path,
                Index: sensor.Index));
        }

        var children = new List<HardwareComponentSnapshot>();
        foreach (var subHardware in hardware.SubHardware)
        {
            subHardware.Update();
            children.Add(BuildComponentSnapshot(subHardware, $"{path}/{subHardware.Name}"));
        }

        var properties = CollectHardwareProperties(hardware, sensors);

        return new HardwareComponentSnapshot(
            HardwareId: hardware.Identifier.ToString(),
            HardwareName: hardware.Name,
            HardwareType: hardware.HardwareType,
            Manufacturer: TryDetectVendor(hardware.Name) ?? TryDetectVendor(hardware.Identifier.ToString()) ?? string.Empty,
            Sensors: sensors,
            Children: children,
            Properties: properties);
    }

    private static IReadOnlyDictionary<string, string> CollectHardwareProperties(IHardware hardware, IReadOnlyList<SensorSnapshot> sensors)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = hardware.Identifier.ToString()
        };

        AddIfNotNull(properties, "Name", hardware.Name);

        switch (hardware.HardwareType)
        {
            case HardwareType.Cpu:
                AddCpuProperties(properties, sensors);
                break;
            case HardwareType.GpuAmd:
            case HardwareType.GpuNvidia:
            case HardwareType.GpuIntel:
                AddGpuProperties(properties, sensors);
                break;
            case HardwareType.Memory:
                AddMemoryProperties(properties, sensors);
                break;
            case HardwareType.Storage:
                AddStorageProperties(properties, sensors);
                break;
            case HardwareType.Motherboard:
                AddMotherboardProperties(properties, sensors);
                break;
        }

        return properties;
    }

    private static void AddCpuProperties(IDictionary<string, string> properties, IReadOnlyList<SensorSnapshot> sensors)
    {
        var coreNames = sensors
            .Where(s => s.SensorType == SensorType.Clock && s.SensorName.Contains("Core", StringComparison.OrdinalIgnoreCase))
            .Select(ParseCoreIndex)
            .Where(i => i.HasValue)
            .Select(i => i!.Value)
            .Distinct()
            .ToList();

        if (coreNames.Count > 0)
        {
            properties["CoreCount"] = coreNames.Count.ToString(CultureInfo.InvariantCulture);
        }

        var maxClock = sensors
            .Where(s => s.SensorType == SensorType.Clock)
            .Select(s => s.Value)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .DefaultIfEmpty()
            .Max();
        if (maxClock > 0)
        {
            properties["CurrentClockMHz"] = maxClock.ToString("F0", CultureInfo.InvariantCulture);
        }

        var packageTemp = sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.SensorName.Contains("Package", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .DefaultIfEmpty()
            .Max();
        if (packageTemp > 0)
        {
            properties["PackageTempC"] = packageTemp.ToString("F1", CultureInfo.InvariantCulture);
        }

        var coreTemps = sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.SensorName.Contains("Core", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();
        if (coreTemps.Count > 0)
        {
            properties["AvgCoreTempC"] = coreTemps.Average().ToString("F1", CultureInfo.InvariantCulture);
            properties["MaxCoreTempC"] = coreTemps.Max().ToString("F1", CultureInfo.InvariantCulture);
        }
    }

    private static void AddGpuProperties(IDictionary<string, string> properties, IReadOnlyList<SensorSnapshot> sensors)
    {
        var coreClock = sensors
            .Where(s => s.SensorType == SensorType.Clock && s.SensorName.Contains("Core", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .DefaultIfEmpty()
            .Max();
        if (coreClock > 0)
        {
            properties["CoreClockMHz"] = coreClock.ToString("F0", CultureInfo.InvariantCulture);
        }

        var memClock = sensors
            .Where(s => s.SensorType == SensorType.Clock && s.SensorName.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .DefaultIfEmpty()
            .Max();
        if (memClock > 0)
        {
            properties["MemoryClockMHz"] = memClock.ToString("F0", CultureInfo.InvariantCulture);
        }

        var gpuTemp = sensors
            .Where(s => s.SensorType == SensorType.Temperature)
            .Select(s => s.Value)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .DefaultIfEmpty()
            .Max();
        if (gpuTemp > 0)
        {
            properties["MaxTempC"] = gpuTemp.ToString("F1", CultureInfo.InvariantCulture);
        }

        var memUsed = FindFirstDataSensor(sensors, "Memory Used") ?? FindFirstDataSensor(sensors, "GPU Memory Used");
        var memTotal = FindFirstDataSensor(sensors, "Memory Total") ?? FindFirstDataSensor(sensors, "GPU Memory Total");
        if (memUsed is not null)
        {
            properties["MemoryUsedBytes"] = memUsed.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (memTotal is not null)
        {
            properties["MemoryTotalBytes"] = memTotal.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void AddMemoryProperties(IDictionary<string, string> properties, IReadOnlyList<SensorSnapshot> sensors)
    {
        var used = FindFirstDataSensor(sensors, "Memory Used") ?? FindFirstDataSensor(sensors, "Used Memory");
        var available = FindFirstDataSensor(sensors, "Available Memory") ?? FindFirstDataSensor(sensors, "Memory Available");
        var total = FindFirstDataSensor(sensors, "Memory Total");

        if (used is not null)
        {
            properties["UsedBytes"] = used.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (available is not null)
        {
            properties["AvailableBytes"] = available.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (total is not null)
        {
            properties["TotalBytes"] = total.Value.ToString(CultureInfo.InvariantCulture);
        }
        else if (used is not null && available is not null)
        {
            properties["TotalBytes"] = (used.Value + available.Value).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void AddStorageProperties(IDictionary<string, string> properties, IReadOnlyList<SensorSnapshot> sensors)
    {
        var temp = sensors
            .Where(s => s.SensorType == SensorType.Temperature)
            .Select(s => s.Value)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .DefaultIfEmpty()
            .Max();
        if (temp > 0)
        {
            properties["MaxTempC"] = temp.ToString("F1", CultureInfo.InvariantCulture);
        }

        var life = FindFirstDataSensor(sensors, "Remaining Life") ?? FindFirstDataSensor(sensors, "Life");
        if (life is not null)
        {
            properties["LifeRemaining"] = life.Value.ToString(CultureInfo.InvariantCulture);
        }

        var usedSpace = FindFirstDataSensor(sensors, "Used Space");
        var totalSpace = FindFirstDataSensor(sensors, "Total Space");
        if (usedSpace is not null)
        {
            properties["UsedBytes"] = usedSpace.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (totalSpace is not null)
        {
            properties["TotalBytes"] = totalSpace.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void AddMotherboardProperties(IDictionary<string, string> properties, IReadOnlyList<SensorSnapshot> sensors)
    {
        var temps = sensors
            .Where(s => s.SensorType == SensorType.Temperature)
            .Select(s => s.Value)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();
        if (temps.Count > 0)
        {
            properties["MaxTempC"] = temps.Max().ToString("F1", CultureInfo.InvariantCulture);
        }
    }

    private static int? ParseCoreIndex(SensorSnapshot sensor)
    {
        // �������ƣ�"Core #1", "CPU Core #2"
        var name = sensor.SensorName;
        var parts = name.Split('#');
        if (parts.Length < 2)
        {
            return null;
        }

        var numberPart = parts[^1];
        if (int.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return index;
        }

        return null;
    }

    private static float? FindFirstDataSensor(IEnumerable<SensorSnapshot> sensors, string contains)
    {
        return sensors
            .Where(s => (s.SensorType == SensorType.Data || s.SensorType == SensorType.SmallData) && s.SensorName.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value)
            .FirstOrDefault(v => v.HasValue);
    }

    private static float? NormalizeSensorValue(float? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return float.IsFinite(value.Value) ? value.Value : null;
    }

    private static IEnumerable<SensorSnapshot> FlattenSensors(HardwareComponentSnapshot component)
    {
        foreach (var sensor in component.Sensors)
        {
            yield return sensor;
        }

        foreach (var child in component.Children)
        {
            foreach (var sensor in FlattenSensors(child))
            {
                yield return sensor;
            }
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HardwareMonitorService));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _computer.Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static void AddIfNotNull(IDictionary<string, string> dict, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            dict[key] = value;
        }
    }

    private static string? TryDetectVendor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains("intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (text.Contains("amd", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (text.Contains("nvidia", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (text.Contains("ati", StringComparison.OrdinalIgnoreCase)) return "ATI";
        return null;
    }
}

public sealed record HardwareMonitorSnapshot(
    IReadOnlyList<HardwareComponentSnapshot> Components);

public sealed record HardwareComponentSnapshot(
    string HardwareId,
    string HardwareName,
    HardwareType HardwareType,
    string Manufacturer,
    IReadOnlyList<SensorSnapshot> Sensors,
    IReadOnlyList<HardwareComponentSnapshot> Children,
    IReadOnlyDictionary<string, string> Properties);

public sealed record SensorSnapshot(
    string SensorId,
    string SensorName,
    SensorType SensorType,
    float? Value,
    float? Min,
    float? Max,
    string HardwarePath,
    int Index);

public sealed record SensorReading(string Hardware, string SensorName, SensorType SensorType, float? Value);

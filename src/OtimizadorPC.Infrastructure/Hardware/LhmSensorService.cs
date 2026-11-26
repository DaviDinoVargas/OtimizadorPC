using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using LibreHardwareMonitor.Hardware;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Hardware;

namespace OtimizadorPC.Infrastructure.Hardware;

/// <summary>
/// Sensores via LibreHardwareMonitor. Carga de CPU/RAM/GPU funciona sem driver; temperaturas
/// e potência do processador exigem administrador e o driver PawnIO instalado.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LhmSensorService : ISensorService
{
    private readonly object _lock = new();
    private Computer? _computer;
    private bool _opened;

    public bool HasDriverAccess { get; } = WmiHardwareInfoService.IsAdministrator() && IsPawnIoInstalled();

    public static bool IsPawnIoInstalled()
    {
        try
        {
            return ServiceController.GetDevices().Any(s => s.ServiceName.Equals("PawnIO", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void EnsureOpen()
    {
        if (_opened) return;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
        };
        try { _computer.Open(); }
        catch (Exception) { /* sem driver: continua com o que estiver disponível */ }
        _opened = true;
    }

    public LiveMetrics Sample()
    {
        lock (_lock)
        {
            EnsureOpen();
            var readings = new List<SensorReading>();
            double cpuLoad = 0;
            double? cpuTemp = null, cpuClock = null, cpuPower = null;
            double? gpuLoad = null, gpuTemp = null, gpuPower = null, gpuCore = null, gpuMem = null;

            foreach (var hw in _computer!.Hardware)
            {
                Update(hw);
                foreach (var sensor in EnumerateSensors(hw))
                {
                    var kind = Map(sensor.SensorType);
                    if (kind == SensorKind.Outro) continue;
                    readings.Add(new SensorReading(hw.Name, sensor.Name, kind, sensor.Value, sensor.Min, sensor.Max));

                    if (sensor.Value is not { } value || float.IsNaN(value)) continue;
                    switch (hw.HardwareType)
                    {
                        case HardwareType.Cpu:
                            if (sensor.SensorType == SensorType.Load && sensor.Name == "CPU Total") cpuLoad = value;
                            else if (sensor.SensorType == SensorType.Temperature && (sensor.Name.Contains("Package") || sensor.Name.Contains("Tctl") || sensor.Name.Contains("Core (Tctl/Tdie)") || sensor.Name.Contains("Average")))
                                cpuTemp ??= value;
                            else if (sensor.SensorType == SensorType.Clock && sensor.Name.StartsWith("Core #"))
                                cpuClock = Math.Max(cpuClock ?? 0, value);
                            else if (sensor.SensorType == SensorType.Power && sensor.Name.Contains("Package"))
                                cpuPower = value;
                            break;

                        case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                            if (sensor.SensorType == SensorType.Load && sensor.Name is "GPU Core" or "D3D 3D") gpuLoad = Math.Max(gpuLoad ?? 0, value);
                            else if (sensor.SensorType == SensorType.Temperature && sensor.Name is "GPU Core" or "GPU Hot Spot") gpuTemp ??= value;
                            else if (sensor.SensorType == SensorType.Power && (sensor.Name.Contains("Package") || sensor.Name.Contains("GPU Power") || sensor.Name.Contains("Total"))) gpuPower ??= value;
                            else if (sensor.SensorType == SensorType.Clock && sensor.Name == "GPU Core") gpuCore = value;
                            else if (sensor.SensorType == SensorType.Clock && sensor.Name == "GPU Memory") gpuMem = value;
                            break;
                    }
                }
            }

            var (ramTotal, ramAvail) = ReadMemoryStatus();
            var ramUsed = ramTotal - ramAvail;

            return new LiveMetrics(
                DateTime.Now,
                cpuLoad,
                cpuTemp,
                cpuClock,
                cpuPower,
                ramTotal == 0 ? 0 : ramUsed * 100.0 / ramTotal,
                ramUsed,
                ramTotal,
                gpuLoad,
                gpuTemp,
                gpuPower,
                gpuCore,
                gpuMem,
                readings);
        }
    }

    private static void Update(IHardware hw)
    {
        try
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
        }
        catch (Exception) { /* hardware removido ou sem acesso */ }
    }

    private static IEnumerable<ISensor> EnumerateSensors(IHardware hw) =>
        hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors));

    private static SensorKind Map(SensorType t) => t switch
    {
        SensorType.Load => SensorKind.Carga,
        SensorType.Temperature => SensorKind.Temperatura,
        SensorType.Clock => SensorKind.Clock,
        SensorType.Power => SensorKind.Potencia,
        SensorType.Fan => SensorKind.Ventoinha,
        SensorType.Voltage => SensorKind.Tensao,
        SensorType.Data => SensorKind.Dados,
        _ => SensorKind.Outro
    };

    // RAM direto da API do Windows: sempre disponível, mesmo sem administrador.
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public static (long Total, long Available) ReadMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhys, (long)status.AvailPhys) : (0, 0);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _computer?.Close(); } catch (Exception) { }
            _computer = null;
            _opened = false;
        }
    }
}

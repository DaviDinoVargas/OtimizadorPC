using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Hardware;

namespace OtimizadorPC.Infrastructure.Hardware;

/// <summary>Coleta informações do hardware via WMI/CIM, registro e APIs do .NET.</summary>
[SupportedOSPlatform("windows")]
public sealed class WmiHardwareInfoService : IHardwareInfoService
{
    public Task<SystemSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var disks = ReadPhysicalDisks();
            var diskOfLetter = ReadPartitionMap();
            var systemLetter = Path.GetPathRoot(Environment.SystemDirectory)?[..1] ?? "C";

            return new SystemSnapshot(
                ReadOs(),
                ReadCpu(),
                ReadBoard(),
                ReadMemory(),
                ReadGpus(),
                disks,
                ReadVolumes(disks, diskOfLetter, systemLetter),
                ReadPageFiles(),
                ReadPowerPlan(),
                IsAdministrator(),
                DateTime.Now);
        }, cancellationToken);

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // ---------- Sistema ----------

    private static OsInfo ReadOs()
    {
        var o = QueryFirst("SELECT Caption, Version, BuildNumber, OSArchitecture, CSName, InstallDate, LastBootUpTime FROM Win32_OperatingSystem");
        var boot = ToDate(o?["LastBootUpTime"]);
        var displayVersion = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;
        var ubr = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null);
        var build = Str(o?["BuildNumber"]) + (ubr is int u ? "." + u : string.Empty);

        return new OsInfo(
            Str(o?["Caption"]).Replace("Microsoft ", string.Empty),
            displayVersion ?? Str(o?["Version"]),
            build,
            Str(o?["OSArchitecture"]),
            Environment.MachineName,
            ToDate(o?["InstallDate"]),
            boot is null ? TimeSpan.FromMilliseconds(Environment.TickCount64) : DateTime.Now - boot.Value);
    }

    private static CpuInfo ReadCpu()
    {
        var c = QueryFirst("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor");
        var baseMhz = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", null) is int mhz ? mhz : 0;
        return new CpuInfo(
            Str(c?["Name"]).Trim(),
            Str(c?["Manufacturer"]),
            Int(c?["NumberOfCores"]),
            Int(c?["NumberOfLogicalProcessors"]),
            baseMhz,
            Int(c?["MaxClockSpeed"]),
            Int(c?["L2CacheSize"]),
            Int(c?["L3CacheSize"]),
            Str(c?["SocketDesignation"]));
    }

    private static BoardInfo ReadBoard()
    {
        var b = QueryFirst("SELECT Manufacturer, Product FROM Win32_BaseBoard");
        var bios = QueryFirst("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
        return new BoardInfo(
            Str(b?["Manufacturer"]),
            Str(b?["Product"]),
            Str(bios?["Manufacturer"]),
            Str(bios?["SMBIOSBIOSVersion"]),
            ToDate(bios?["ReleaseDate"]));
    }

    // ---------- Memória ----------

    private static MemoryInfo ReadMemory()
    {
        var modules = Query("SELECT DeviceLocator, Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory")
            .Select(m => new MemoryModule(
                Str(m["DeviceLocator"]),
                Str(m["Manufacturer"]),
                Str(m["PartNumber"]).Trim(),
                Long(m["Capacity"]),
                Int(m["Speed"]),
                Int(m["ConfiguredClockSpeed"])))
            .ToList();

        var slots = Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => Int(a["MemoryDevices"]));
        var os = QueryFirst("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
        var total = Long(os?["TotalVisibleMemorySize"]) * 1024;
        var free = Long(os?["FreePhysicalMemory"]) * 1024;

        return new MemoryInfo(total, free, Math.Max(slots, modules.Count), modules);
    }

    // ---------- Vídeo ----------

    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var vram = ReadVramFromRegistry();
        return Query("SELECT Name, AdapterCompatibility, DriverVersion, DriverDate, AdapterRAM, PNPDeviceID FROM Win32_VideoController")
            .Where(g => !Str(g["PNPDeviceID"]).StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) // ignora adaptadores virtuais
            .Select(g =>
            {
                var name = Str(g["Name"]);
                var vendorText = Str(g["AdapterCompatibility"]) + " " + name;
                var vendor = vendorText.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia
                    : vendorText.Contains("AMD", StringComparison.OrdinalIgnoreCase) || vendorText.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || vendorText.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Amd
                    : vendorText.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel
                    : GpuVendor.Desconhecido;
                var mem = vram.TryGetValue(name, out var v) ? v : Long(g["AdapterRAM"]);
                return new GpuInfo(name, vendor, Str(g["DriverVersion"]), ToDate(g["DriverDate"]), mem);
            })
            .ToList();
    }

    /// <summary>AdapterRAM do WMI é limitado a 4 GB; o registro do driver tem o valor real.</summary>
    private static Dictionary<string, long> ReadVramFromRegistry()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return result;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string desc) continue;
                var size = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    _ => 0L
                };
                if (size > 0) result[desc] = size;
            }
        }
        catch (Exception) { /* sem acesso: usa AdapterRAM */ }
        return result;
    }

    // ---------- Discos ----------

    private static IReadOnlyList<PhysicalDiskInfo> ReadPhysicalDisks()
    {
        try
        {
            return Query("SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage")
                .Select(d =>
                {
                    var bus = Int(d["BusType"]);
                    var media = Int(d["MediaType"]) switch
                    {
                        3 => DiskMediaKind.HDD,
                        4 => bus == 17 ? DiskMediaKind.NVMe : DiskMediaKind.SSD,
                        _ => bus == 17 ? DiskMediaKind.NVMe : DiskMediaKind.Desconhecido
                    };
                    return new PhysicalDiskInfo(
                        int.TryParse(Str(d["DeviceId"]), out var n) ? n : -1,
                        Str(d["FriendlyName"]),
                        media,
                        BusName(bus),
                        Long(d["Size"]),
                        Int(d["HealthStatus"]) switch { 0 => "Saudável", 1 => "Atenção", 2 => "Com defeito", _ => "Desconhecido" });
                })
                .OrderBy(d => d.Number)
                .ToList();
        }
        catch (ManagementException)
        {
            return [];
        }
    }

    private static string BusName(int bus) => bus switch
    {
        1 => "SCSI", 3 => "ATA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 10 => "SAS",
        11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "Arquivo virtual", 17 => "NVMe", _ => "Outro"
    };

    private static Dictionary<string, int> ReadPartitionMap()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Query("SELECT DriveLetter, DiskNumber FROM MSFT_Partition", @"root\Microsoft\Windows\Storage"))
            {
                var letter = p["DriveLetter"] is char c && c != '\0' ? c.ToString()
                    : p["DriveLetter"] is ushort u && u != 0 ? ((char)u).ToString() : null;
                if (letter is not null) map[letter] = Int(p["DiskNumber"]);
            }
        }
        catch (ManagementException) { }
        return map;
    }

    private static IReadOnlyList<VolumeInfo> ReadVolumes(IReadOnlyList<PhysicalDiskInfo> disks, Dictionary<string, int> diskOfLetter, string systemLetter)
    {
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            try
            {
                if (!d.IsReady) continue;
                var letter = d.Name[..1].ToUpperInvariant();
                int? diskNumber = diskOfLetter.TryGetValue(letter, out var n) ? n : null;
                var media = diskNumber is null ? DiskMediaKind.Desconhecido
                    : disks.FirstOrDefault(x => x.Number == diskNumber)?.Media ?? DiskMediaKind.Desconhecido;
                list.Add(new VolumeInfo(letter, d.VolumeLabel, d.DriveFormat, d.TotalSize, d.TotalFreeSpace, diskNumber, media,
                    letter.Equals(systemLetter, StringComparison.OrdinalIgnoreCase)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list.OrderBy(v => v.Letter).ToList();
    }

    // ---------- Paginação e energia ----------

    private static IReadOnlyList<PageFileInfo> ReadPageFiles() =>
        Query("SELECT Name, AllocatedBaseSize, CurrentUsage, TempPageFile FROM Win32_PageFileUsage")
            .Select(p => new PageFileInfo(Str(p["Name"]), Int(p["AllocatedBaseSize"]), Int(p["CurrentUsage"]), p["TempPageFile"] is true))
            .ToList();

    private static string ReadPowerPlan() => Power.PowerPlans.GetActiveName();

    // ---------- Utilitários WMI ----------

    private static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (ManagementException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Classe WMI ausente nesta versão do Windows
            return [];
        }
    }

    private static ManagementBaseObject? QueryFirst(string wql, string scope = @"root\cimv2") => Query(wql, scope).FirstOrDefault();

    private static string Str(object? o) => o?.ToString()?.Trim() ?? string.Empty;

    private static int Int(object? o) => o is null ? 0 : int.TryParse(o.ToString(), out var v) ? v : 0;

    private static long Long(object? o) => o is null ? 0 : long.TryParse(o.ToString(), out var v) ? v : 0;

    private static DateTime? ToDate(object? o)
    {
        if (o is not string s || s.Length < 8) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch (Exception)
        {
            return DateTime.TryParseExact(s[..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        }
    }
}

namespace OtimizadorPC.Core.Hardware;

public sealed record OsInfo(
    string Name,
    string Version,
    string Build,
    string Architecture,
    string ComputerName,
    DateTime? InstallDate,
    TimeSpan Uptime);

public sealed record CpuInfo(
    string Name,
    string Vendor,
    int Cores,
    int Threads,
    int BaseClockMhz,
    int MaxClockMhz,
    int L2CacheKb,
    int L3CacheKb,
    string Socket)
{
    public bool IsIntel => Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);
    public bool IsAmd => Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);
}

public sealed record BoardInfo(
    string Manufacturer,
    string Product,
    string BiosVendor,
    string BiosVersion,
    DateTime? BiosDate);

public sealed record MemoryModule(
    string Slot,
    string Manufacturer,
    string PartNumber,
    long CapacityBytes,
    int RatedMhz,
    int ConfiguredMhz);

public sealed record MemoryInfo(
    long TotalBytes,
    long AvailableBytes,
    int SlotCount,
    IReadOnlyList<MemoryModule> Modules)
{
    public long UsedBytes => TotalBytes - AvailableBytes;
    public double UsedPercent => TotalBytes == 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
}

public enum GpuVendor { Desconhecido, Nvidia, Amd, Intel }

public sealed record GpuInfo(
    string Name,
    GpuVendor Vendor,
    string DriverVersion,
    DateTime? DriverDate,
    long VramBytes);

public enum DiskMediaKind { Desconhecido, HDD, SSD, NVMe }

public sealed record PhysicalDiskInfo(
    int Number,
    string Model,
    DiskMediaKind Media,
    string Bus,
    long SizeBytes,
    string Health)
{
    public bool IsHealthy => Health.Equals("Saudável", StringComparison.OrdinalIgnoreCase);
}

public sealed record VolumeInfo(
    string Letter,
    string Label,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    int? DiskNumber,
    DiskMediaKind Media,
    bool IsSystem)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public double FreePercent => TotalBytes == 0 ? 0 : FreeBytes * 100.0 / TotalBytes;
    public double UsedFraction => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;
}

public sealed record PageFileInfo(
    string Path,
    int AllocatedMb,
    int CurrentUsageMb,
    bool IsTemporary)
{
    public string DriveLetter => Path.Length >= 1 ? Path[..1].ToUpperInvariant() : string.Empty;
}

/// <summary>Foto completa do hardware e do sistema em um momento.</summary>
public sealed record SystemSnapshot(
    OsInfo Os,
    CpuInfo Cpu,
    BoardInfo Board,
    MemoryInfo Memory,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<PhysicalDiskInfo> Disks,
    IReadOnlyList<VolumeInfo> Volumes,
    IReadOnlyList<PageFileInfo> PageFiles,
    string PowerPlan,
    bool IsAdministrator,
    DateTime TakenAt);

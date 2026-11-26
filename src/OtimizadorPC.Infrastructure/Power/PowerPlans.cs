using System.Runtime.InteropServices;
using System.Text;

namespace OtimizadorPC.Infrastructure.Power;

public sealed record PowerPlan(Guid Id, string Name, bool IsActive);

/// <summary>Planos de energia do Windows via powrprof.dll (sem depender de WMI).</summary>
public static class PowerPlans
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public static Guid? GetActiveId()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    public static string GetActiveName()
    {
        var id = GetActiveId();
        return id is null ? "Desconhecido" : GetName(id.Value);
    }

    public static string GetName(Guid id)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size == 0) return id.ToString();
        var buffer = new byte[size];
        return PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
            ? Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : id.ToString();
    }

    public static IReadOnlyList<PowerPlan> List()
    {
        var active = GetActiveId();
        var list = new List<PowerPlan>();
        for (uint i = 0; ; i++)
        {
            var buffer = new byte[16];
            uint size = 16;
            if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /* ACCESS_SCHEME */, i, buffer, ref size) != 0) break;
            var id = new Guid(buffer);
            list.Add(new PowerPlan(id, GetName(id), id == active));
        }
        return list;
    }

    /// <summary>Ativa um plano. Retorna false se o plano não existir.</summary>
    public static bool SetActive(Guid id) => PowerSetActiveScheme(IntPtr.Zero, ref id) == 0;

    /// <summary>Cria uma cópia de um plano oculto (ex.: Desempenho Máximo) e devolve o novo id.</summary>
    public static Guid? Duplicate(Guid source)
    {
        if (PowerDuplicateScheme(IntPtr.Zero, ref source, out var ptr) != 0) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr rootPowerKey, ref Guid sourceSchemeGuid, out IntPtr destinationSchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
        uint accessFlags, uint index, byte[] buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

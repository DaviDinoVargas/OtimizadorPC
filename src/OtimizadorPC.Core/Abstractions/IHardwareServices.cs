using OtimizadorPC.Core.Hardware;

namespace OtimizadorPC.Core.Abstractions;

public interface IHardwareInfoService
{
    /// <summary>Coleta processador, placa, memória, vídeo, discos e sistema.</summary>
    Task<SystemSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface ISensorService : IDisposable
{
    /// <summary>Indica se o driver de sensores está disponível (temperaturas, potência).</summary>
    bool HasDriverAccess { get; }

    /// <summary>Lê todos os sensores. Seguro para chamar de qualquer thread.</summary>
    LiveMetrics Sample();
}

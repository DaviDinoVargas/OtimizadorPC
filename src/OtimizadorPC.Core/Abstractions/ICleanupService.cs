using OtimizadorPC.Core.Cleanup;

namespace OtimizadorPC.Core.Abstractions;

public interface ICleanupService
{
    IReadOnlyList<CleanupCategory> Categories { get; }

    /// <summary>Calcula quanto a categoria liberaria, sem apagar nada.</summary>
    Task<CleanupScan> ScanAsync(string categoryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Limpa a categoria. Em categorias com itens individuais, só os caminhos informados em
    /// <paramref name="selectedEntries"/> são removidos. Arquivos em uso são ignorados.
    /// </summary>
    Task<CleanupResult> CleanAsync(string categoryId, IReadOnlyCollection<string>? selectedEntries,
        IProgress<double>? progress, CancellationToken cancellationToken = default);
}

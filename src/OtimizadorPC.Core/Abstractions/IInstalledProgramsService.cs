using OtimizadorPC.Core.Programs;

namespace OtimizadorPC.Core.Abstractions;

/// <summary>Lista, mede e desinstala programas, e procura o que eles deixaram para trás.</summary>
public interface IInstalledProgramsService
{
    /// <summary>Programas (registro HKLM 64/32 bits e HKCU) e apps da Microsoft Store do usuário.</summary>
    Task<IReadOnlyList<InstalledProgram>> ListAsync(CancellationToken ct = default);

    /// <summary>Tamanho real da pasta de instalação, para quem não informa o tamanho.</summary>
    Task<long> MeasureAsync(InstalledProgram program, CancellationToken ct = default);

    /// <summary>Executa o desinstalador e espera terminar. <paramref name="quiet"/> usa o modo silencioso quando existir.</summary>
    Task<UninstallOutcome> UninstallAsync(InstalledProgram program, bool quiet, CancellationToken ct = default);

    /// <summary>A entrada do programa ainda existe no registro / na lista de pacotes?</summary>
    bool IsStillInstalled(InstalledProgram program);

    Task<IReadOnlyList<Leftover>> FindLeftoversAsync(InstalledProgram program, IReadOnlyList<InstalledProgram> stillInstalled, CancellationToken ct = default);

    /// <summary>Pastas vão para a Lixeira; chaves do registro são exportadas (.reg) antes de apagar.</summary>
    Task RemoveLeftoversAsync(IEnumerable<Leftover> leftovers, CancellationToken ct = default);

    /// <summary>Remove só a entrada da lista (para programas cujo desinstalador não existe mais).</summary>
    Task RemoveEntryAsync(InstalledProgram program, CancellationToken ct = default);
}

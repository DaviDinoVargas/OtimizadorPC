using OtimizadorPC.Core.DiskAnalysis;
using OtimizadorPC.Core.Settings;

namespace OtimizadorPC.Core.Abstractions;

public interface IDiskScanner
{
    /// <summary>Indica se o modo MFT pode ser usado nesta pasta (NTFS + administrador).</summary>
    bool CanUseMft(string path);

    Task<ScanResult> ScanAsync(string path, DiskScanMode mode, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}

public interface IFileOperations
{
    /// <summary>Envia arquivo ou pasta para a Lixeira (pode ser desfeito).</summary>
    void SendToRecycleBin(string path);

    /// <summary>
    /// Move uma pasta para outro local. Com <paramref name="leaveJunction"/>, deixa uma junção no
    /// caminho antigo apontando para o novo, para programas continuarem achando os arquivos.
    /// </summary>
    Task MoveFolderAsync(string source, string destination, bool leaveJunction, IProgress<string>? progress, CancellationToken ct);
}

public interface IDuplicateScanner
{
    /// <summary>Arquivos idênticos (a partir de <paramref name="minSize"/> bytes) dentro de <paramref name="root"/>.</summary>
    Task<IReadOnlyList<DuplicateGroup>> FindAsync(string root, long minSize, IProgress<DuplicateProgress>? progress, CancellationToken ct);
}

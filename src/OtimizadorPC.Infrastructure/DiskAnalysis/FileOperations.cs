using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Infrastructure.Processes;

namespace OtimizadorPC.Infrastructure.DiskAnalysis;

[SupportedOSPlatform("windows")]
public sealed class FileOperations : IFileOperations
{
    public void SendToRecycleBin(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Item não encontrado.", path);
        EnsureNotProtected(path);

        var op = new ShFileOpStruct
        {
            wFunc = 3, // FO_DELETE
            pFrom = path + "\0\0",
            fFlags = 0x40 | 0x10 | 0x4 | 0x400, // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
        };
        var result = SHFileOperation(ref op);
        if (result != 0 || op.fAnyOperationsAborted)
            throw new IOException($"O Windows não conseguiu mover para a Lixeira (código {result}). Algum arquivo pode estar em uso.");
    }

    public async Task MoveFolderAsync(string source, string destination, bool leaveJunction, IProgress<string>? progress, CancellationToken ct)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        EnsureNotProtected(source);

        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            destination.Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("O destino não pode ficar dentro da própria pasta.");

        // Se o destino já existe, a pasta vai para dentro dele com o mesmo nome
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            destination = Path.Combine(destination, Path.GetFileName(source));
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException($"Já existe uma pasta com conteúdo em {destination}.");

        var target = new DriveInfo(Path.GetPathRoot(destination)!);
        var needed = Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } });
        if (target.AvailableFreeSpace < needed + 512L * 1024 * 1024)
            throw new IOException($"Espaço insuficiente em {target.Name}: são necessários {needed / 1024 / 1024:N0} MB.");

        progress?.Report("Copiando e removendo do local original…");
        var r = await ProcessRunner.RunAsync("robocopy.exe",
            $"\"{source}\" \"{destination}\" /E /MOVE /COPY:DAT /DCOPY:T /R:1 /W:1 /XJ /NFL /NDL /NP /NJH", ct: ct);
        if (r.ExitCode >= 8)
            throw new IOException($"A cópia falhou (robocopy {r.ExitCode}). Nada foi removido da origem que não tenha sido copiado.");

        if (Directory.Exists(source))
        {
            var remaining = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count();
            if (remaining > 0)
                throw new IOException($"{remaining} arquivo(s) estavam em uso e ficaram na pasta original. Feche os programas e tente de novo.");
            Directory.Delete(source, recursive: true);
        }

        if (leaveJunction)
        {
            progress?.Report("Criando junção no local original…");
            var j = await ProcessRunner.RunAsync("cmd.exe", $"/c mklink /J \"{source}\" \"{destination}\"", ct: ct);
            if (j.ExitCode != 0)
                throw new IOException($"A pasta foi movida para {destination}, mas a junção não pôde ser criada: {j.Output.Trim()}");
        }
        progress?.Report("Concluído");
    }

    /// <summary>Recusa pastas do sistema, cujo movimento ou exclusão quebra o Windows.</summary>
    private static void EnsureNotProtected(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string[] blocked =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData"),
            Path.Combine(Path.GetPathRoot(full) ?? "C:\\", "Users"),
        ];
        if (Path.GetPathRoot(full) is { } root && Path.TrimEndingDirectorySeparator(root).Equals(full, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Não é possível mover ou apagar a raiz de um disco.");
        if (blocked.Any(b => !string.IsNullOrEmpty(b) && full.Equals(Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"\"{full}\" é uma pasta do sistema e não pode ser movida ou apagada por aqui.");
        if (full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Arquivos dentro da pasta do Windows não podem ser movidos ou apagados por aqui.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct op);
}

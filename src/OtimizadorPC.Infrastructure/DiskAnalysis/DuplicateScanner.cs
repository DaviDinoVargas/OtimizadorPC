using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.DiskAnalysis;

namespace OtimizadorPC.Infrastructure.DiskAnalysis;

/// <summary>Busca arquivos duplicados numa pasta, ignorando pastas do sistema e links.</summary>
public sealed class DuplicateScanner : IDuplicateScanner
{
    /// <summary>Pastas onde "duplicados" são do próprio Windows/programas e não devem ser mexidos.</summary>
    private static readonly string[] SkippedFolderNames =
    [
        "$recycle.bin", "system volume information", "windowsapps", "winsxs", "$windows.~bt", "$windows.~ws",
        "$sysreset", "recovery", "programdata\\package cache", "node_modules", ".git", "__pycache__",
    ];

    public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(string root, long minSize, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new DuplicateProgress("Listando arquivos", 0, 0, root));
        var files = await Task.Run(() => Enumerate(root, minSize, progress, ct), ct).ConfigureAwait(false);
        return await DuplicateFinder.FindAsync(files, new FileHasher(), progress, ct, parallelism: 4).ConfigureAwait(false);
    }

    private static List<DuplicateFile> Enumerate(string root, long minSize, IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline,
            ReturnSpecialDirectories = false,
        };
        var list = new List<DuplicateFile>();
        var e = new FileSystemEnumerable<DuplicateFile>(root,
            (ref FileSystemEntry entry) => new DuplicateFile(entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.LocalDateTime), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && entry.Length >= minSize,
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsSkipped(entry.ToFullPath()),
        };
        try
        {
            foreach (var f in e)
            {
                ct.ThrowIfCancellationRequested();
                list.Add(f);
                if (list.Count % 500 == 0) progress?.Report(new DuplicateProgress("Listando arquivos", list.Count, 0, f.Path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    private static bool IsSkipped(string dir)
    {
        var lower = dir.ToLowerInvariant();
        var name = Path.GetFileName(lower);
        foreach (var s in SkippedFolderNames)
        {
            if (s.Contains('\\') ? lower.EndsWith("\\" + s) : name == s) return true;
        }
        // Só a pasta Windows da raiz do disco (não qualquer pasta chamada "windows")
        return name == "windows" && Path.GetDirectoryName(lower) is { } parent && Path.GetPathRoot(lower) == parent;
    }

    /// <summary>Resumos SHA-256: rápido (64 KB do início + 64 KB do fim + tamanho) e completo.</summary>
    private sealed class FileHasher : IFileHasher
    {
        private const int Chunk = 64 * 1024;

        public long PartialCoversWholeFileUpTo => 2 * Chunk;

        public async Task<string?> PartialAsync(string path, long size, CancellationToken ct)
        {
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
                var buffer = new byte[(int)Math.Min(size, 2 * Chunk) + 8];
                BitConverter.TryWriteBytes(buffer.AsSpan(0, 8), size);
                if (size <= 2 * Chunk)
                {
                    await fs.ReadExactlyAsync(buffer.AsMemory(8, (int)size), ct).ConfigureAwait(false);
                }
                else
                {
                    await fs.ReadExactlyAsync(buffer.AsMemory(8, Chunk), ct).ConfigureAwait(false);
                    fs.Seek(-Chunk, SeekOrigin.End);
                    await fs.ReadExactlyAsync(buffer.AsMemory(8 + Chunk, Chunk), ct).ConfigureAwait(false);
                }
                return Convert.ToHexString(SHA256.HashData(buffer));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        public async Task<string?> FullAsync(string path, CancellationToken ct)
        {
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        public string? Identity(string path)
        {
            try
            {
                using SafeFileHandle h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!GetFileInformationByHandle(h, out var info)) return null;
                // Só importa quando há mais de um link para o mesmo arquivo
                return info.NumberOfLinks > 1 ? $"{info.VolumeSerialNumber:X}:{info.FileIndexHigh:X}{info.FileIndexLow:X8}" : null;
            }
            catch (Exception) { return null; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
    }
}

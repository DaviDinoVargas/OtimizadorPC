using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OtimizadorPC.Infrastructure.Overclock;

internal static class MemoryBenchmark
{
    /// <summary>Velocidade de cópia de memória em GB/s (usa todos os núcleos).</summary>
    public static double Bandwidth(IProgress<double>? progress, CancellationToken ct)
    {
        const int size = 256 * 1024 * 1024;
        var src = GC.AllocateUninitializedArray<byte>(size);
        var dst = GC.AllocateUninitializedArray<byte>(size);
        Random.Shared.NextBytes(src.AsSpan(0, 1024 * 1024));
        var threads = Environment.ProcessorCount;
        var chunk = size / threads;

        void CopyAll() => Parallel.For(0, threads, t => src.AsSpan(t * chunk, chunk).CopyTo(dst.AsSpan(t * chunk, chunk)));

        CopyAll(); // aquece e "toca" as páginas
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            ct.ThrowIfCancellationRequested();
            CopyAll();
            bytes += size;
            progress?.Report(sw.Elapsed.TotalSeconds / 2);
        }
        return Math.Round(bytes / sw.Elapsed.TotalSeconds / 1e9, 1);
    }

    /// <summary>Latência média de acesso aleatório à RAM em nanossegundos (perseguição de ponteiros).</summary>
    public static double Latency(IProgress<double>? progress, CancellationToken ct)
    {
        const int count = 32 * 1024 * 1024; // 128 MB: bem maior que qualquer cache
        var next = new int[count];
        for (var i = 0; i < count; i++) next[i] = i;
        // Algoritmo de Sattolo: um único ciclo cobrindo todo o vetor, em ordem aleatória
        var rnd = new Random(42);
        for (var i = count - 1; i > 0; i--)
        {
            var j = rnd.Next(i);
            (next[i], next[j]) = (next[j], next[i]);
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.5);

        const int steps = 20_000_000;
        var p = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < steps; i++) p = next[p];
        sw.Stop();
        GC.KeepAlive(p);
        progress?.Report(1);
        return Math.Round(sw.Elapsed.TotalMilliseconds * 1_000_000 / steps, 1);
    }
}

internal static unsafe class DiskBenchmark
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const int Block = 4 * 1024 * 1024;

    public sealed record DiskResult(double WriteMBs, double ReadMBs, double RandomIops);

    /// <summary>Grava, lê e faz leituras aleatórias de 4 KB em um arquivo temporário sem cache do Windows.</summary>
    public static DiskResult Run(string driveRoot, IProgress<double>? progress, CancellationToken ct)
    {
        var drive = new DriveInfo(Path.GetPathRoot(driveRoot)!);
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        var folder = string.Equals(drive.Name, systemDrive, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetTempPath(), "OtimizadorPC-teste")
            : Path.Combine(drive.Name, "OtimizadorPC-teste");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "disco.tmp");
        long size = drive.AvailableFreeSpace > 4L * 1024 * 1024 * 1024 ? 1024L * 1024 * 1024 : 256L * 1024 * 1024;
        if (drive.AvailableFreeSpace < size + 512L * 1024 * 1024)
            throw new IOException($"Espaço insuficiente em {drive.Name} para o teste de disco.");

        var buffer = (byte*)NativeMemory.AlignedAlloc(Block, 4096);
        try
        {
            new Span<byte>(buffer, Block).Fill(0x5A);
            double write, read, iops;

            // Gravação sequencial
            using (var h = File.OpenHandle(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough | NoBuffering, size))
            {
                var sw = Stopwatch.StartNew();
                for (long off = 0; off < size; off += Block)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, new ReadOnlySpan<byte>(buffer, Block), off);
                    progress?.Report(off / (double)size / 3);
                }
                write = size / sw.Elapsed.TotalSeconds / 1e6;
            }

            // Leitura sequencial
            using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering))
            {
                var sw = Stopwatch.StartNew();
                for (long off = 0; off < size; off += Block)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Read(h, new Span<byte>(buffer, Block), off);
                    progress?.Report(1.0 / 3 + off / (double)size / 3);
                }
                read = size / sw.Elapsed.TotalSeconds / 1e6;

                // Leitura aleatória de 4 KB por 3 segundos
                var rnd = new Random(7);
                var pages = size / 4096;
                long ops = 0;
                sw.Restart();
                while (sw.Elapsed < TimeSpan.FromSeconds(3))
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Read(h, new Span<byte>(buffer, 4096), rnd.NextInt64(pages) * 4096);
                    ops++;
                    if ((ops & 255) == 0) progress?.Report(2.0 / 3 + sw.Elapsed.TotalSeconds / 9);
                }
                iops = ops / sw.Elapsed.TotalSeconds;
            }
            return new DiskResult(Math.Round(write), Math.Round(read), Math.Round(iops));
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
            try { File.Delete(file); Directory.Delete(folder); } catch (IOException) { }
        }
    }
}

using System.Diagnostics;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Overclock;

namespace OtimizadorPC.Infrastructure.Overclock;

/// <summary>
/// Carga de CPU determinística: mistura ponto flutuante (fractal de Mandelbrot) com inteiros
/// (hash). O mesmo "seed" sempre dá o mesmo resultado — se mudar, o processador errou.
/// </summary>
internal static class CpuKernel
{
    public static ulong Run(int seed)
    {
        double acc = 0;
        var h = (ulong)(seed + 1) * 0x9E3779B97F4A7C15UL;
        var shift = (seed % 13) / 26.0;
        for (var py = 0; py < 24; py++)
        {
            for (var px = 0; px < 24; px++)
            {
                double x0 = px / 12.0 - 1.6 + shift, y0 = py / 12.0 - 1.0;
                double x = 0, y = 0;
                var it = 0;
                while (x * x + y * y < 4 && it < 96)
                {
                    var xt = x * x - y * y + x0;
                    y = 2 * x * y + y0;
                    x = xt;
                    it++;
                }
                h ^= (ulong)it + 0x632BE59BD9B4E019UL;
                h *= 0xBF58476D1CE4E5B9UL;
                h ^= h >> 31;
                acc += x * 0.5 + y;
            }
        }
        return h ^ BitConverter.DoubleToUInt64Bits(acc);
    }
}

internal static class CpuBenchmark
{
    /// <summary>Execuções do kernel por segundo (divididas por 10 para virar "pontos").</summary>
    public static double Measure(int threads, TimeSpan duration, IProgress<double>? progress, CancellationToken ct)
    {
        CpuKernel.Run(0); // aquece o JIT
        long total = 0;
        var sw = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, threads).Select(t => Task.Factory.StartNew(() =>
        {
            long local = 0;
            var seed = t * 1000;
            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                CpuKernel.Run(seed++ & 0xFFFF);
                local++;
            }
            Interlocked.Add(ref total, local);
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        while (!Task.WaitAll(workers, 200)) progress?.Report(Math.Min(1, sw.Elapsed / duration));
        ct.ThrowIfCancellationRequested();
        return Math.Round(total / sw.Elapsed.TotalSeconds / 10, 0);
    }
}

/// <summary>Teste de estresse de CPU com verificação de cada resultado.</summary>
public sealed class CpuStress : ICpuStress
{
    private const int Seeds = 512;

    public Task<StressReport> RunAsync(TimeSpan duration, Func<double?> temperature, double temperatureLimit,
        IProgress<StressStatus>? progress, CancellationToken ct) => Task.Run(() =>
    {
        // Resultados de referência calculados antes de esquentar
        var reference = new ulong[Seeds];
        for (var i = 0; i < Seeds; i++) reference[i] = CpuKernel.Run(i);

        long iterations = 0, errors = 0;
        double? maxTemp = null;
        var aborted = false;
        string? abortReason = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(t => Task.Factory.StartNew(() =>
        {
            var seed = t * 37;
            while (!stop.IsCancellationRequested && sw.Elapsed < duration)
            {
                var s = seed++ % Seeds;
                if (CpuKernel.Run(s) != reference[s]) Interlocked.Increment(ref errors);
                Interlocked.Increment(ref iterations);
            }
        }, stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        while (!Task.WaitAll(workers, 1000))
        {
            var t = temperature();
            if (t is { } v) maxTemp = Math.Max(maxTemp ?? 0, v);
            if (t > temperatureLimit)
            {
                aborted = true;
                abortReason = $"Interrompido: temperatura chegou a {t:0} °C (limite {temperatureLimit:0} °C).";
                stop.Cancel();
            }
            progress?.Report(new StressStatus(sw.Elapsed, Interlocked.Read(ref iterations), Interlocked.Read(ref errors), t, abortReason));
        }

        if (ct.IsCancellationRequested) { aborted = true; abortReason ??= "Cancelado pelo usuário."; }
        var verdict = abortReason
                      ?? (errors == 0 ? "Estável: nenhum erro de cálculo." : $"INSTÁVEL: {errors} erros de cálculo. Reduza o overclock ou melhore a refrigeração.");
        return new StressReport(sw.Elapsed, iterations, errors, maxTemp, aborted, verdict);
    }, CancellationToken.None);
}

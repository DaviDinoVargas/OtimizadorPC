using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Overclock;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace OtimizadorPC.Infrastructure.Overclock;

/// <summary>
/// Carga de GPU por Direct3D 11 (compute shader). Funciona em placas NVIDIA, AMD e Intel.
/// Cada lote parte dos mesmos dados, então o resultado tem que ser sempre idêntico: qualquer
/// diferença indica erro de cálculo (overclock instável).
/// </summary>
public sealed unsafe class GpuCompute : IGpuStress, IDisposable
{
    private const int Elements = 1 << 20;
    private const int Iterations = 512;
    private const int FlopsPerIteration = 4;
    private const int Batch = 16;

    private const string Shader = """
        RWStructuredBuffer<float> data : register(u0);
        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            float x = data[id.x];
            float y = frac(x * 0.5f + 0.25f);
            [loop] for (uint i = 0; i < 512; i++)
            {
                x = frac(mad(x, 1.6180339f, y));
                y = frac(mad(y, 1.4142135f, x));
            }
            data[id.x] = x + y;
        }
        """;

    private readonly Func<double?> _temperature;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _shader;
    private ID3D11Buffer? _pristine, _work, _staging;
    private ID3D11UnorderedAccessView? _uav;
    private float[]? _golden;

    public GpuCompute(Func<double?> temperature) => _temperature = temperature;

    public static bool IsAvailable()
    {
        try
        {
            using var g = new GpuCompute(() => null);
            g.Initialize();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Initialize()
    {
        if (_device is not null) return;

        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.None,
            [FeatureLevel.Level_11_0], out _device, out _context).CheckError();

        var bytecode = Compiler.Compile(Shader, "main", "gpu.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        _shader = _device!.CreateComputeShader(bytecode.Span, null);

        var initial = new float[Elements];
        for (var i = 0; i < Elements; i++) initial[i] = i * 0.0000917f % 1f;

        _pristine = _device.CreateBuffer(initial, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured, 0, 4);
        _work = _device.CreateBuffer((uint)(Elements * 4), BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured, 4);
        _staging = _device.CreateBuffer((uint)(Elements * 4), BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read,
            ResourceOptionFlags.BufferStructured, 4);
        _uav = _device.CreateUnorderedAccessView(_work, null);
    }

    /// <summary>Roda um lote, devolve o tempo e o resultado do último cálculo.</summary>
    private (double Seconds, float[] Output) RunBatch()
    {
        var ctx = _context!;
        ctx.CSSetShader(_shader!);
        ctx.CSSetUnorderedAccessView(0, _uav!, 0);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < Batch; i++)
        {
            ctx.CopyResource(_work!, _pristine!);
            ctx.Dispatch(Elements / 256, 1, 1);
        }
        ctx.CopyResource(_staging!, _work!);
        var mapped = ctx.Map(_staging!, MapMode.Read, MapFlags.None);
        var output = new float[Elements];
        Marshal.Copy(mapped.DataPointer, output, 0, Elements);
        ctx.Unmap(_staging!, 0);
        return (sw.Elapsed.TotalSeconds, output);
    }

    private static double Gflops(double seconds) => (double)Elements * Iterations * FlopsPerIteration * Batch / seconds / 1e9;

    /// <summary>GFLOPS aproximados (média de alguns lotes depois do aquecimento).</summary>
    public double Benchmark(IProgress<double>? progress, CancellationToken ct)
    {
        Initialize();
        RunBatch();
        var results = new List<double>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            ct.ThrowIfCancellationRequested();
            results.Add(Gflops(RunBatch().Seconds));
            progress?.Report(sw.Elapsed.TotalSeconds / 5);
        }
        return Math.Round(results.OrderBy(r => r).Skip(results.Count / 4).Average(), 0);
    }

    /// <summary>Define o resultado de referência (deve ser chamado com a placa nos clocks originais).</summary>
    public void CaptureGolden()
    {
        Initialize();
        _golden = RunBatch().Output;
    }

    public Task<GpuStressOutcome> RunAsync(TimeSpan duration, CancellationToken ct) => Task.Run(() =>
    {
        Initialize();
        _golden ??= RunBatch().Output;
        long errors = 0;
        double? maxTemp = null;
        var speeds = new List<double>();
        var sw = Stopwatch.StartNew();
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var (seconds, output) = RunBatch();
                speeds.Add(Gflops(seconds));
                for (var i = 0; i < Elements; i++)
                    if (BitConverter.SingleToInt32Bits(output[i]) != BitConverter.SingleToInt32Bits(_golden[i])) errors++;
                if (_temperature() is { } t) maxTemp = Math.Max(maxTemp ?? 0, t);
            }
            while (sw.Elapsed < duration);
            return new GpuStressOutcome(errors, false, maxTemp, speeds.Count == 0 ? 0 : speeds.Average());
        }
        catch (SharpGenException)
        {
            // Driver reiniciou (TDR): sinal claro de instabilidade. Recria o dispositivo na próxima vez.
            ResetDevice();
            return new GpuStressOutcome(errors, true, maxTemp, speeds.Count == 0 ? 0 : speeds.Average());
        }
    }, ct);

    private void ResetDevice()
    {
        var golden = _golden;
        Dispose();
        _golden = golden;
    }

    public void Dispose()
    {
        _uav?.Dispose(); _staging?.Dispose(); _work?.Dispose(); _pristine?.Dispose();
        _shader?.Dispose(); _context?.Dispose(); _device?.Dispose();
        _uav = null; _staging = null; _work = null; _pristine = null; _shader = null; _context = null; _device = null;
    }
}

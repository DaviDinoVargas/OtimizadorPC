using System.Diagnostics;
using System.Runtime.Versioning;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.DiskAnalysis;
using OtimizadorPC.Core.Settings;
using OtimizadorPC.Infrastructure.Hardware;
using Serilog;

namespace OtimizadorPC.Infrastructure.DiskAnalysis;

/// <summary>Escolhe o motor de análise (rápido ou MFT) e informa o progresso.</summary>
[SupportedOSPlatform("windows")]
public sealed class DiskScanner : IDiskScanner
{
    public bool CanUseMft(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            return drive.DriveType == DriveType.Fixed && drive.DriveFormat == "NTFS" && WmiHardwareInfoService.IsAdministrator();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<ScanResult> ScanAsync(string path, DiskScanMode mode, IProgress<ScanProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);

            var agg = new ScanAggregator();
            var sw = Stopwatch.StartNew();
            string? note = null;
            using var ticker = new Timer(_ => progress?.Report(new ScanProgress(agg.Files, agg.Folders, agg.Bytes,
                MftScanner.CurrentPhase ?? FastScanner.CurrentPath)), null, 0, 150);

            MftScanner.CurrentPhase = null;
            var wantsMft = mode == DiskScanMode.Mft || (mode == DiskScanMode.Automatico && CanUseMft(full));
            DirNode? root = null;
            var method = ScanMethod.Rapido;

            if (wantsMft)
            {
                if (!CanUseMft(full))
                {
                    note = "O modo MFT precisa de um disco NTFS e do app aberto como administrador. Foi usado o modo rápido.";
                }
                else
                {
                    try
                    {
                        root = MftScanner.Scan(full, agg, cancellationToken);
                        method = ScanMethod.Mft;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Leitura da MFT falhou; usando o modo rápido");
                        note = "Não foi possível ler a MFT deste disco. Foi usado o modo rápido.";
                        agg = new ScanAggregator();
                    }
                }
                MftScanner.CurrentPhase = null;
            }

            root ??= FastScanner.Scan(full, agg, cancellationToken);
            root.ComputeTotals();
            sw.Stop();
            progress?.Report(new ScanProgress(agg.Files, agg.Folders, agg.Bytes, null));

            Log.Information("Dinosize {Caminho}: {Arquivos} arquivos, {Bytes} bytes em {Tempo} ({Metodo})",
                full, agg.Files, agg.Bytes, sw.Elapsed, method);
            return new ScanResult(root, agg.LargestFiles(), agg.Extensions(), method, sw.Elapsed, agg.Inaccessible, note);
        }, cancellationToken);
}

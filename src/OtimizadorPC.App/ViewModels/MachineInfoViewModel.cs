using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Formatting;
using OtimizadorPC.Core.Hardware;
using Serilog;
using Wpf.Ui.Controls;

namespace OtimizadorPC.App.ViewModels;

public sealed record InfoRow(string Label, string Value);

public sealed record InfoSection(string Title, SymbolRegular Icon, IReadOnlyList<InfoRow> Rows);

public sealed partial class SensorRow : ObservableObject
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required SensorKind Kind { get; init; }
    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private string _min = "—";
    [ObservableProperty] private string _max = "—";
}

public sealed class SensorGroup
{
    public required string Hardware { get; init; }
    public ObservableCollection<SensorRow> Rows { get; } = [];
}

/// <summary>Página de informações detalhadas do hardware e sensores ao vivo.</summary>
public sealed partial class MachineInfoViewModel : PageViewModel
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly SnapshotProvider _snapshots;
    private readonly SensorMonitor _monitor;
    private readonly Dictionary<string, SensorRow> _sensorIndex = [];
    private DateTime _lastSensorRefresh = DateTime.MinValue;

    public MachineInfoViewModel(SnapshotProvider snapshots, SensorMonitor monitor)
    {
        _snapshots = snapshots;
        _monitor = monitor;
    }

    public ObservableCollection<InfoSection> Sections { get; } = [];
    public ObservableCollection<SensorGroup> SensorGroups { get; } = [];

    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _sensorDriverMissing;
    [ObservableProperty] private string _report = string.Empty;

    public override async Task OnNavigatedToAsync()
    {
        SensorDriverMissing = !_monitor.HasDriverAccess;
        _monitor.Subscribe(OnMetrics);
        await LoadAsync(false);
    }

    public override void OnNavigatedFrom() => _monitor.Unsubscribe(OnMetrics);

    [RelayCommand]
    private Task Refresh() => LoadAsync(true);

    private async Task LoadAsync(bool force)
    {
        try
        {
            IsBusy = !HasData;
            var s = await _snapshots.GetAsync(force);
            Build(s);
            HasData = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao coletar informações da máquina");
            StatusMessage = "Não foi possível ler as informações do sistema.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Build(SystemSnapshot s)
    {
        Sections.Clear();

        Sections.Add(new("Sistema", SymbolRegular.Window24,
        [
            new("Windows", s.Os.Name),
            new("Versão", $"{s.Os.Version} (build {s.Os.Build})"),
            new("Arquitetura", s.Os.Architecture),
            new("Nome do computador", s.Os.ComputerName),
            new("Instalado em", s.Os.InstallDate?.ToString("dd/MM/yyyy", PtBr) ?? "—"),
            new("Ligado há", $"{(int)s.Os.Uptime.TotalDays} d {s.Os.Uptime.Hours} h {s.Os.Uptime.Minutes} min"),
            new("Plano de energia", s.PowerPlan),
            new("Executando como", s.IsAdministrator ? "Administrador" : "Usuário comum"),
        ]));

        Sections.Add(new("Processador", SymbolRegular.DeveloperBoard24,
        [
            new("Modelo", s.Cpu.Name),
            new("Fabricante", s.Cpu.Vendor),
            new("Núcleos / threads", $"{s.Cpu.Cores} / {s.Cpu.Threads}"),
            new("Clock base", s.Cpu.BaseClockMhz > 0 ? $"{s.Cpu.BaseClockMhz / 1000.0:0.00} GHz" : "—"),
            new("Cache L2 / L3", $"{s.Cpu.L2CacheKb / 1024.0:0.#} MB / {s.Cpu.L3CacheKb / 1024.0:0.#} MB"),
            new("Soquete", s.Cpu.Socket),
        ]));

        Sections.Add(new("Placa-mãe e BIOS", SymbolRegular.Board24,
        [
            new("Fabricante", s.Board.Manufacturer),
            new("Modelo", s.Board.Product),
            new("BIOS", $"{s.Board.BiosVendor} {s.Board.BiosVersion}"),
            new("Data da BIOS", s.Board.BiosDate?.ToString("dd/MM/yyyy", PtBr) ?? "—"),
        ]));

        var memRows = new List<InfoRow>
        {
            new("Total", ByteSize.Format(s.Memory.TotalBytes)),
            new("Em uso", $"{ByteSize.Format(s.Memory.UsedBytes)} ({s.Memory.UsedPercent:0}%)"),
            new("Slots ocupados", $"{s.Memory.Modules.Count} de {s.Memory.SlotCount}"),
        };
        memRows.AddRange(s.Memory.Modules.Select(m => new InfoRow(
            m.Slot,
            $"{ByteSize.Format(m.CapacityBytes)} · {m.Manufacturer} {m.PartNumber} · rodando a {m.ConfiguredMhz} MHz (nominal {m.RatedMhz} MHz)")));
        Sections.Add(new("Memória", SymbolRegular.Ram20, memRows));

        Sections.Add(new("Placa de vídeo", SymbolRegular.Games24, s.Gpus.Count == 0
            ? [new("Placa", "Não identificada")]
            : s.Gpus.SelectMany(g => new[]
            {
                new InfoRow("Modelo", g.Name),
                new InfoRow("Memória de vídeo", g.VramBytes > 0 ? ByteSize.Format(g.VramBytes) : "—"),
                new InfoRow("Driver", $"{g.DriverVersion}" + (g.DriverDate is { } d ? $" de {d:dd/MM/yyyy}" : string.Empty)),
            }).ToList()));

        var storage = s.Disks.Select(d => new InfoRow($"Disco {d.Number}",
            $"{d.Model} · {d.Media} · {d.Bus} · {ByteSize.Format(d.SizeBytes)} · {d.Health}")).ToList();
        storage.AddRange(s.Volumes.Select(v => new InfoRow($"{v.Letter}: {v.Label}",
            $"{v.FileSystem} · {ByteSize.Format(v.FreeBytes)} livres de {ByteSize.Format(v.TotalBytes)} ({v.FreePercent:0}%)" + (v.IsSystem ? " · sistema" : string.Empty))));
        storage.AddRange(s.PageFiles.Select(p => new InfoRow("Paginação",
            $"{p.Path} · {p.AllocatedMb} MB" + (p.IsTemporary ? " · TEMPORÁRIA" : string.Empty))));
        Sections.Add(new("Armazenamento", SymbolRegular.Storage24, storage));

        Report = BuildReport();
    }

    private string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Relatório do OtimizadorPC — " + DateTime.Now.ToString("dd/MM/yyyy HH:mm", PtBr));
        foreach (var section in Sections)
        {
            sb.AppendLine().AppendLine("== " + section.Title);
            foreach (var row in section.Rows) sb.AppendLine($"{row.Label}: {row.Value}");
        }
        return sb.ToString();
    }

    private void OnMetrics(LiveMetrics m)
    {
        // Atualiza a tabela de sensores a cada 2 s para não poluir a tela
        if (DateTime.Now - _lastSensorRefresh < TimeSpan.FromSeconds(2)) return;
        _lastSensorRefresh = DateTime.Now;

        foreach (var r in m.Sensors.Where(r => r.Kind is not SensorKind.Dados))
        {
            var key = r.Hardware + "|" + r.Kind + "|" + r.Name;
            if (!_sensorIndex.TryGetValue(key, out var row))
            {
                var group = SensorGroups.FirstOrDefault(g => g.Hardware == r.Hardware);
                if (group is null)
                {
                    group = new SensorGroup { Hardware = r.Hardware };
                    SensorGroups.Add(group);
                }
                row = new SensorRow { Key = key, Name = $"{Label(r.Kind)} · {r.Name}", Kind = r.Kind };
                group.Rows.Add(row);
                _sensorIndex[key] = row;
            }
            row.Value = Format(r.Value, r);
            row.Min = Format(r.Min, r);
            row.Max = Format(r.Max, r);
        }
    }

    private static string Label(SensorKind k) => k switch
    {
        SensorKind.Carga => "Uso",
        SensorKind.Temperatura => "Temperatura",
        SensorKind.Clock => "Clock",
        SensorKind.Potencia => "Consumo",
        SensorKind.Ventoinha => "Ventoinha",
        SensorKind.Tensao => "Tensão",
        _ => k.ToString()
    };

    private static string Format(float? v, SensorReading r) =>
        v is null || float.IsNaN(v.Value) ? "—"
        : r.Kind == SensorKind.Tensao ? $"{v:0.000} {r.Unit}"
        : $"{v:0.#} {r.Unit}";

    [RelayCommand]
    private void CopyReport()
    {
        try
        {
            Clipboard.SetText(Report);
            StatusMessage = "Relatório copiado para a área de transferência.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao copiar relatório");
            StatusMessage = "Não foi possível copiar. Tente novamente.";
        }
    }

    [RelayCommand]
    private void ExportReport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salvar relatório",
            FileName = $"OtimizadorPC-relatorio-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Texto (*.txt)|*.txt",
        };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, Report, Encoding.UTF8);
        StatusMessage = $"Relatório salvo em {dialog.FileName}";
    }
}

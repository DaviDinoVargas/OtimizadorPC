using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Network;
using Serilog;

namespace OtimizadorPC.App.ViewModels;

/// <summary>Cartão de um destino no teste de latência.</summary>
public sealed partial class PingRow(PingTarget target) : ObservableObject
{
    private readonly List<double?> _raw = [];

    public PingTarget Target { get; } = target;
    public ObservableCollection<double> History { get; } = [];

    [ObservableProperty] private PingStats? _stats;
    [ObservableProperty] private ConnectionQuality? _quality;
    [ObservableProperty] private string _method = string.Empty;
    [ObservableProperty] private double _last;

    public string AvgText => Stats is { Received: > 0 } s ? $"{s.Avg:0}" : Stats is null ? "—" : "×";
    public string Detail => Stats is null ? Target.Host
        : $"jitter {Stats.Jitter:0.#} ms · perda {Stats.LossPercent:0.#}% · {Stats.Min:0}–{Stats.Max:0} ms";
    public string QualityText => Quality switch
    {
        ConnectionQuality.Excelente => "Excelente",
        ConnectionQuality.Boa => "Boa",
        ConnectionQuality.Regular => "Regular",
        ConnectionQuality.Ruim => "Ruim",
        ConnectionQuality.SemResposta => "Sem resposta",
        _ => "Aguardando",
    };

    public void Reset()
    {
        _raw.Clear();
        History.Clear();
        Stats = null;
        Quality = null;
        Method = string.Empty;
        Notify();
    }

    public void Add(PingSample s)
    {
        _raw.Add(s.Ms);
        if (s.Ms is { } ms)
        {
            Last = ms;
            History.Add(ms);
            if (History.Count > 30) History.RemoveAt(0);
        }
        Method = s.Method == PingMethod.Icmp ? "ping" : "conexão TCP";
        Stats = NetworkStats.Compute(_raw);
        Quality = Stats.Quality;
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(AvgText));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(QualityText));
    }
}

/// <summary>Linha de um provedor no teste de DNS.</summary>
public sealed partial class DnsRow(DnsProvider provider) : ObservableObject
{
    public DnsProvider Provider { get; } = provider;

    [ObservableProperty] private double? _avgMs;
    [ObservableProperty] private int _failures;
    [ObservableProperty] private bool _isBest;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _tested;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private string? _testedServer;

    public string Servers => Provider.IsAutomatic ? (TestedServer is null ? "definido pelo roteador" : $"atual: {TestedServer}") : $"{Provider.Primary} · {Provider.Secondary}";
    public string AvgText => !Tested ? "—" : AvgMs is { } ms ? $"{ms:0} ms" : "sem resposta";

    partial void OnAvgMsChanged(double? value) => OnPropertyChanged(nameof(AvgText));
    partial void OnTestedChanged(bool value) => OnPropertyChanged(nameof(AvgText));
    partial void OnTestedServerChanged(string? value) => OnPropertyChanged(nameof(Servers));
}

/// <summary>Rede e DNS: teste de latência para jogos, teste/troca de DNS e reparos da conexão.</summary>
public sealed partial class NetworkViewModel : PageViewModel
{
    private const int Rounds = 20;

    private readonly INetworkService _net;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public ObservableCollection<AdapterInfo> Adapters { get; } = [];
    public ObservableCollection<PingRow> Pings { get; } = new(NetworkCatalog.PingTargets.Select(t => new PingRow(t)));
    public ObservableCollection<DnsRow> DnsRows { get; } = new(new[] { NetworkCatalog.Automatic }.Concat(NetworkCatalog.DnsProviders).Select(p => new DnsRow(p)));

    [ObservableProperty] private AdapterInfo? _selectedAdapter;
    [ObservableProperty] private bool _isPinging;
    [ObservableProperty] private bool _isBenchmarking;
    [ObservableProperty] private bool _isRepairing;
    [ObservableProperty] private int _round;
    [ObservableProperty] private bool _hasMultipleAdapters;
    [ObservableProperty] private ConnectionQuality? _overallQuality;
    [ObservableProperty] private string _overallText = "Faça o teste para ver como está a sua conexão.";
    [ObservableProperty] private string? _dnsMessage;
    [ObservableProperty] private string? _repairMessage;

    public NetworkViewModel(INetworkService net, IDialogService dialogs)
    {
        _net = net;
        _dialogs = dialogs;
    }

    public string AdapterKindText => SelectedAdapter?.Kind switch
    {
        AdapterKind.Cabo => "Cabo de rede",
        AdapterKind.WiFi => "Wi-Fi",
        null => "Sem conexão",
        _ => "Outro",
    };

    public string AdapterSpeedText => SelectedAdapter is { SpeedMbps: > 0 } a ? (a.SpeedMbps >= 1000 ? $"{a.SpeedMbps / 1000.0:0.#} Gbps" : $"{a.SpeedMbps} Mbps") : "—";
    public string AdapterDnsText => SelectedAdapter is { } a
        ? (a.DnsServers.Count == 0 ? "—" : string.Join(" · ", a.DnsServers)) + (a.DnsIsAutomatic ? "  (automático)" : "  (fixo)")
        : "—";

    partial void OnSelectedAdapterChanged(AdapterInfo? value)
    {
        OnPropertyChanged(nameof(AdapterKindText));
        OnPropertyChanged(nameof(AdapterSpeedText));
        OnPropertyChanged(nameof(AdapterDnsText));
        MarkCurrentDns();
    }

    public override Task OnNavigatedToAsync()
    {
        LoadAdapters();
        return Task.CompletedTask;
    }

    private void LoadAdapters()
    {
        var keep = SelectedAdapter?.Id;
        Adapters.Clear();
        foreach (var a in _net.GetAdapters()) Adapters.Add(a);
        HasMultipleAdapters = Adapters.Count > 1;
        SelectedAdapter = Adapters.FirstOrDefault(a => a.Id == keep) ?? Adapters.FirstOrDefault();
    }

    private void MarkCurrentDns()
    {
        var a = SelectedAdapter;
        foreach (var r in DnsRows)
            r.IsCurrent = a is not null && (r.Provider.IsAutomatic ? a.DnsIsAutomatic : !a.DnsIsAutomatic && a.DnsServers.Contains(r.Provider.Primary!));
    }

    // =====================================================================
    // Latência
    // =====================================================================

    [RelayCommand]
    private async Task RunPingTestAsync()
    {
        if (IsPinging) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsPinging = true;
        foreach (var p in Pings) p.Reset();
        OverallQuality = null;
        OverallText = "Medindo…";
        try
        {
            // Cada destino mede no seu ritmo: um servidor lento não atrasa os outros
            var done = new int[Pings.Count];
            await Task.WhenAll(Pings.Select(async (p, i) =>
            {
                for (var n = 0; n < Rounds; n++)
                {
                    p.Add(await _net.PingAsync(p.Target, ct));
                    done[i] = n + 1;
                    Round = (int)Math.Round(done.Average());
                    UpdateOverall();
                    // Sem nenhuma resposta nas 3 primeiras tentativas: o destino não está acessível daqui
                    if (n == 2 && p.Stats is { Received: 0 }) { done[i] = Rounds; break; }
                    await Task.Delay(250, ct);
                }
            }));
            Log.Information("Teste de latência: {Resumo}", OverallText);
        }
        catch (OperationCanceledException) { OverallText = "Teste interrompido."; }
        finally
        {
            IsPinging = false;
            Round = 0;
        }
    }

    [RelayCommand]
    private void StopPingTest() => _cts?.Cancel();

    /// <summary>A nota geral vem dos destinos "Internet" (os mais estáveis); jogos aparecem cartão a cartão.</summary>
    private void UpdateOverall()
    {
        var internet = Pings.Where(p => p.Target.Group == "Internet" && p.Stats is not null).Select(p => p.Stats!).ToList();
        if (internet.Count == 0) return;
        var best = internet.OrderBy(s => s.Quality).ThenBy(s => s.Avg).First();
        OverallQuality = best.Quality;
        OverallText = best.Quality switch
        {
            ConnectionQuality.Excelente => $"Ótima para jogos: {best.Avg:0} ms com {best.Jitter:0.#} ms de variação e {best.LossPercent:0.#}% de perda.",
            ConnectionQuality.Boa => $"Boa: {best.Avg:0} ms em média. Dá para jogar tranquilo.",
            ConnectionQuality.Regular => $"Regular: {best.Avg:0} ms e {best.Jitter:0.#} ms de variação. Se estiver no Wi-Fi, um cabo costuma ajudar.",
            ConnectionQuality.Ruim => $"Instável: {best.LossPercent:0.#}% de perda e {best.Jitter:0.#} ms de variação. Tente o reparo rápido abaixo ou reinicie o roteador.",
            _ => "Sem resposta da internet. Confira o cabo/Wi-Fi e use os reparos abaixo.",
        };
    }

    // =====================================================================
    // DNS
    // =====================================================================

    [RelayCommand]
    private async Task BenchmarkDnsAsync()
    {
        if (IsBenchmarking) return;
        IsBenchmarking = true;
        DnsMessage = null;
        try
        {
            var results = new List<(DnsRow Row, DnsBenchmark Result)>();
            foreach (var row in DnsRows)
            {
                var server = row.Provider.Primary ?? (SelectedAdapter?.DnsIsAutomatic == true ? SelectedAdapter.DnsServers.FirstOrDefault() : SelectedAdapter?.Gateway);
                if (server is null) continue;
                row.IsTesting = true;
                row.TestedServer = row.Provider.IsAutomatic ? server : null;
                try
                {
                    // Duas passadas: a primeira aquece o cache do servidor, a segunda é a que vale
                    await _net.BenchmarkDnsAsync(row.Provider, server, NetworkCatalog.BenchmarkDomains, CancellationToken.None);
                    var r = await _net.BenchmarkDnsAsync(row.Provider, server, NetworkCatalog.BenchmarkDomains, CancellationToken.None);
                    row.AvgMs = r.AvgMs;
                    row.Failures = r.Failures;
                    results.Add((row, r));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Falha no teste de DNS {Provider}", row.Provider.Name);
                    row.AvgMs = null;
                }
                finally
                {
                    row.IsTesting = false;
                    row.Tested = true;
                }
            }

            var best = NetworkStats.Best(results.Select(r => r.Result));
            var max = results.Where(r => r.Result.AvgMs is not null).Select(r => r.Result.AvgMs!.Value).DefaultIfEmpty(1).Max();
            foreach (var (row, r) in results)
            {
                row.IsBest = best is not null && ReferenceEquals(r, best);
                row.Fraction = r.AvgMs is { } ms ? Math.Max(0.04, ms / max) : 0;
            }

            var current = DnsRows.FirstOrDefault(r => r.IsCurrent);
            var bestRow = DnsRows.FirstOrDefault(r => r.IsBest);
            DnsMessage = bestRow is null ? "Nenhum servidor respondeu. Confira a conexão."
                : bestRow.IsCurrent ? $"Você já usa o DNS mais rápido ({bestRow.Provider.Name})."
                : current?.AvgMs is { } cur && bestRow.AvgMs is { } b
                    ? $"{bestRow.Provider.Name} respondeu em {b:0} ms, contra {cur:0} ms do atual. Clique em \"Usar\" para trocar."
                    : $"{bestRow.Provider.Name} foi o mais rápido.";
        }
        finally { IsBenchmarking = false; }
    }

    [RelayCommand]
    private async Task ApplyDnsAsync(DnsRow? row)
    {
        if (row is null || SelectedAdapter is not { } adapter) return;
        var what = row.Provider.IsAutomatic ? "voltar para o DNS automático do roteador" : $"usar o DNS {row.Provider.Name} ({row.Provider.Primary} e {row.Provider.Secondary})";
        if (!await _dialogs.ConfirmAsync("Trocar DNS",
                $"No adaptador \"{adapter.Name}\", {what}?\n\nA internet pode piscar por um instante. Dá para voltar quando quiser em \"Automático\".", "Trocar DNS"))
            return;
        try
        {
            await _net.SetDnsAsync(adapter, row.Provider, CancellationToken.None);
            LoadAdapters();
            DnsMessage = row.Provider.IsAutomatic ? "DNS de volta para o automático." : $"Agora usando {row.Provider.Name}. O cache de DNS foi limpo.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao trocar DNS");
            DnsMessage = ex.Message;
        }
    }

    // =====================================================================
    // Reparos
    // =====================================================================

    [RelayCommand]
    private async Task FlushDnsAsync()
    {
        await _net.FlushDnsAsync(CancellationToken.None);
        RepairMessage = "Cache de DNS limpo. Sites que não abriam por endereço antigo devem voltar a funcionar.";
    }

    [RelayCommand]
    private async Task QuickRepairAsync()
    {
        if (!await _dialogs.ConfirmAsync("Renovar conexão",
                "O computador vai pedir um endereço novo ao roteador e limpar o cache de DNS. A internet cai por alguns segundos.", "Renovar"))
            return;
        IsRepairing = true;
        try
        {
            RepairMessage = await _net.QuickRepairAsync(new Progress<string>(s => RepairMessage = s), CancellationToken.None);
            LoadAdapters();
        }
        catch (Exception ex) { RepairMessage = ex.Message; }
        finally { IsRepairing = false; }
    }

    [RelayCommand]
    private async Task FullRepairAsync()
    {
        if (!await _dialogs.ConfirmAsync("Redefinir a rede",
                "Redefine o Winsock e a configuração TCP/IP do Windows — resolve a maioria dos casos de \"conectado, sem internet\".\n\n" +
                "Depois é preciso reiniciar o computador. DNS fixo e IP manual voltam para o automático.", "Redefinir"))
            return;
        IsRepairing = true;
        try { RepairMessage = await _net.FullRepairAsync(new Progress<string>(s => RepairMessage = s), CancellationToken.None); }
        catch (Exception ex) { RepairMessage = ex.Message; }
        finally { IsRepairing = false; }
    }
}

using OtimizadorPC.Core.Network;

namespace OtimizadorPC.Core.Abstractions;

public interface INetworkService
{
    /// <summary>Adaptadores conectados (cabo, Wi-Fi), sem loopback e túneis.</summary>
    IReadOnlyList<AdapterInfo> GetAdapters();

    /// <summary>Uma medição: ping (ICMP) e, se o destino não responder a ping, tempo de conexão TCP.</summary>
    Task<PingSample> PingAsync(PingTarget target, CancellationToken ct);

    /// <summary>Tempo médio de resposta do servidor DNS para os domínios de teste.</summary>
    Task<DnsBenchmark> BenchmarkDnsAsync(DnsProvider provider, string server, IReadOnlyList<string> domains, CancellationToken ct);

    /// <summary>Troca o DNS do adaptador (IPv4). Provedor automático volta para o DNS do roteador. Exige administrador.</summary>
    Task SetDnsAsync(AdapterInfo adapter, DnsProvider provider, CancellationToken ct);

    Task FlushDnsAsync(CancellationToken ct);

    /// <summary>Renova o IP e limpa o cache de DNS. Sem reinício.</summary>
    Task<string> QuickRepairAsync(IProgress<string>? progress, CancellationToken ct);

    /// <summary>Redefine Winsock e a pilha TCP/IP. Precisa reiniciar o computador.</summary>
    Task<string> FullRepairAsync(IProgress<string>? progress, CancellationToken ct);
}

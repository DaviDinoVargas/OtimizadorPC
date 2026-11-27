using System.Buffers.Binary;
using System.Text;

namespace OtimizadorPC.Core.Network;

public enum AdapterKind { Cabo, WiFi, Outro }

public sealed record AdapterInfo(
    string Id,
    int InterfaceIndex,
    string Name,
    string Description,
    AdapterKind Kind,
    long SpeedMbps,
    string? Ipv4,
    string? Gateway,
    IReadOnlyList<string> DnsServers,
    bool DnsIsAutomatic,
    string? Mac);

public enum PingMethod { Icmp, Tcp }

/// <summary>Destino do teste de latência. <see cref="TcpPort"/> é usado quando o destino não responde a ping.</summary>
public sealed record PingTarget(string Name, string Host, string Group, int TcpPort = 443);

/// <summary>Uma medição: tempo em ms ou nulo se perdida.</summary>
public readonly record struct PingSample(double? Ms, PingMethod Method);

public enum ConnectionQuality { Excelente, Boa, Regular, Ruim, SemResposta }

public sealed record PingStats(int Sent, int Received, double Min, double Avg, double Max, double Jitter, double LossPercent)
{
    public ConnectionQuality Quality => NetworkStats.Classify(this);
}

public sealed record DnsProvider(string Name, string? Primary, string? Secondary, string Description)
{
    public bool IsAutomatic => Primary is null;
}

public sealed record DnsBenchmark(DnsProvider Provider, double? AvgMs, int Failures, int Queries);

public static class NetworkCatalog
{
    public static readonly DnsProvider Automatic = new("Automático (provedor)", null, null, "O DNS que o seu roteador / provedor de internet entrega.");

    public static IReadOnlyList<DnsProvider> DnsProviders { get; } =
    [
        new("Cloudflare", "1.1.1.1", "1.0.0.1", "Geralmente o mais rápido. Não guarda histórico de navegação."),
        new("Google", "8.8.8.8", "8.8.4.4", "Muito estável, servidores no mundo todo."),
        new("Quad9", "9.9.9.9", "149.112.112.112", "Bloqueia domínios maliciosos conhecidos."),
        new("OpenDNS", "208.67.222.222", "208.67.220.220", "Da Cisco; filtro opcional de conteúdo."),
        new("AdGuard", "94.140.14.14", "94.140.15.15", "Bloqueia anúncios e rastreadores em todo o PC."),
    ];

    /// <summary>Domínios do dia a dia usados no teste de DNS.</summary>
    public static IReadOnlyList<string> BenchmarkDomains { get; } =
    [
        "google.com", "youtube.com", "microsoft.com", "discord.com", "steampowered.com",
        "riotgames.com", "netflix.com", "whatsapp.net", "amazon.com", "twitch.tv",
    ];

    public static IReadOnlyList<PingTarget> PingTargets { get; } =
    [
        new("Cloudflare", "1.1.1.1", "Internet"),
        new("Google", "8.8.8.8", "Internet"),
        new("AWS São Paulo", "dynamodb.sa-east-1.amazonaws.com", "Servidores de jogos"),
        new("AWS Virgínia (EUA)", "dynamodb.us-east-1.amazonaws.com", "Servidores de jogos"),
        new("AWS Frankfurt", "dynamodb.eu-central-1.amazonaws.com", "Servidores de jogos"),
        new("League of Legends (BR)", "104.160.152.3", "Jogos"),
        new("Steam", "store.steampowered.com", "Jogos"),
        new("Valorant / Riot", "riotgames.com", "Jogos"),
    ];
}

/// <summary>Contas da conexão: média, variação (jitter), perda e nota.</summary>
public static class NetworkStats
{
    public static PingStats Compute(IReadOnlyList<double?> samples)
    {
        var ok = samples.Where(s => s.HasValue).Select(s => s!.Value).ToList();
        var loss = samples.Count == 0 ? 100 : 100.0 * (samples.Count - ok.Count) / samples.Count;
        if (ok.Count == 0) return new PingStats(samples.Count, 0, 0, 0, 0, 0, loss);

        // Jitter: média da diferença entre medições seguidas (como no RTP / RFC 3550, sem suavização)
        var jitter = ok.Count < 2 ? 0 : ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        return new PingStats(samples.Count, ok.Count, ok.Min(), ok.Average(), ok.Max(), jitter, loss);
    }

    /// <summary>Faixas pensando em jogos online: até 40 ms e pouca variação é excelente.</summary>
    public static ConnectionQuality Classify(PingStats s)
    {
        if (s.Received == 0) return ConnectionQuality.SemResposta;
        if (s.LossPercent >= 5 || s.Avg > 150 || s.Jitter > 40) return ConnectionQuality.Ruim;
        if (s.LossPercent >= 1 || s.Avg > 80 || s.Jitter > 15) return ConnectionQuality.Regular;
        if (s.Avg > 40 || s.Jitter > 6) return ConnectionQuality.Boa;
        return ConnectionQuality.Excelente;
    }

    /// <summary>Melhor DNS: menor tempo médio entre os que responderam tudo (ou quase tudo).</summary>
    public static DnsBenchmark? Best(IEnumerable<DnsBenchmark> results) => results
        .Where(r => r.AvgMs is not null && r.Failures <= Math.Max(1, r.Queries / 10))
        .OrderBy(r => r.AvgMs)
        .FirstOrDefault();
}

/// <summary>Monta e confere consultas DNS (registro A) sem depender do cache do Windows.</summary>
public static class DnsPacket
{
    public static byte[] BuildQuery(ushort id, string domain)
    {
        var buffer = new List<byte>(32 + domain.Length);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[0..], id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // consulta padrão, recursão desejada
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);      // 1 pergunta
        buffer.AddRange(header.ToArray());
        foreach (var label in domain.Trim('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException($"Domínio inválido: {domain}", nameof(domain));
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        buffer.Add(0);
        buffer.AddRange([0, 1, 0, 1]); // tipo A, classe IN
        return buffer.ToArray();
    }

    /// <summary>A resposta corresponde à consulta e não tem erro (RCODE 0)?</summary>
    public static bool IsValidResponse(ReadOnlySpan<byte> response, ushort id)
    {
        if (response.Length < 12) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(response) != id) return false;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        var isResponse = (flags & 0x8000) != 0;
        var rcode = flags & 0x000F;
        return isResponse && rcode == 0;
    }
}

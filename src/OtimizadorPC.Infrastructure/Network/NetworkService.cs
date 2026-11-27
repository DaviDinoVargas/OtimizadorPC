using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Network;
using OtimizadorPC.Infrastructure.Processes;
using Serilog;

namespace OtimizadorPC.Infrastructure.Network;

public sealed class NetworkService : INetworkService
{
    private const int TimeoutMs = 1500;

    public IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (v4 is null) continue;
                // Adaptadores virtuais (Hyper-V, VirtualBox, VPN) sem gateway não levam à internet
                var gateway = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (gateway is null || gateway.Equals(IPAddress.Any)) continue;

                var kind = ni.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => AdapterKind.WiFi,
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => AdapterKind.Cabo,
                    _ => AdapterKind.Outro,
                };
                var index = props.GetIPv4Properties()?.Index ?? 0;
                var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                var mac = string.Join("-", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                list.Add(new AdapterInfo(ni.Id, index, ni.Name, ni.Description, kind, ni.Speed > 0 ? ni.Speed / 1_000_000 : 0,
                    v4.ToString(), gateway.ToString(), dns, IsDnsAutomatic(ni.Id), string.IsNullOrEmpty(mac) ? null : mac));
            }
            catch (Exception ex) { Log.Debug(ex, "Adaptador ignorado: {Name}", ni.Name); }
        }
        // Cabo primeiro, depois Wi-Fi
        return list.OrderBy(a => a.Kind).ThenBy(a => a.Name).ToList();
    }

    /// <summary>DNS automático = nenhum servidor fixo (NameServer vazio) na configuração da interface.</summary>
    private static bool IsDnsAutomatic(string interfaceId)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{interfaceId}");
            return string.IsNullOrWhiteSpace(k?.GetValue("NameServer") as string);
        }
        catch (Exception) { return true; }
    }

    public async Task<PingSample> PingAsync(PingTarget target, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target.Host, TimeSpan.FromMilliseconds(TimeoutMs), cancellationToken: ct).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success) return new PingSample(reply.RoundtripTime, PingMethod.Icmp);
        }
        catch (Exception ex) when (ex is PingException or SocketException) { }

        // Muitos servidores (AWS, CDNs) bloqueiam ping: mede o tempo de abrir uma conexão TCP
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target.Host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
            if (addresses.Length == 0) return new PingSample(null, PingMethod.Tcp);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeoutMs);
            var sw = Stopwatch.StartNew();
            await socket.ConnectAsync(addresses[0], target.TcpPort, timeout.Token).ConfigureAwait(false);
            return new PingSample(sw.Elapsed.TotalMilliseconds, PingMethod.Tcp);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new PingSample(null, PingMethod.Tcp); }
        catch (SocketException) { return new PingSample(null, PingMethod.Tcp); }
    }

    public async Task<DnsBenchmark> BenchmarkDnsAsync(DnsProvider provider, string server, IReadOnlyList<string> domains, CancellationToken ct)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(server), 53);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(endpoint);
        var times = new List<double>();
        var failures = 0;
        var random = new Random();
        foreach (var domain in domains)
        {
            ct.ThrowIfCancellationRequested();
            var id = (ushort)random.Next(1, ushort.MaxValue);
            var query = DnsPacket.BuildQuery(id, domain);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeoutMs);
                var sw = Stopwatch.StartNew();
                await udp.SendAsync(query, timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    var r = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    if (DnsPacket.IsValidResponse(r.Buffer, id)) { times.Add(sw.Elapsed.TotalMilliseconds); break; }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failures++; }
            catch (SocketException) { failures++; }
        }
        return new DnsBenchmark(provider, times.Count > 0 ? times.Average() : null, failures, domains.Count);
    }

    public async Task SetDnsAsync(AdapterInfo adapter, DnsProvider provider, CancellationToken ct)
    {
        // O índice evita problemas com nomes de adaptador traduzidos ou com acentos
        var name = adapter.InterfaceIndex.ToString();
        if (provider.IsAutomatic)
        {
            await Netsh($"interface ipv4 set dnsservers name={name} source=dhcp", ct);
        }
        else
        {
            await Netsh($"interface ipv4 set dnsservers name={name} source=static address={provider.Primary} register=primary validate=no", ct);
            if (provider.Secondary is { } second)
                await Netsh($"interface ipv4 add dnsservers name={name} address={second} index=2 validate=no", ct);
        }
        await FlushDnsAsync(ct);
        Log.Information("DNS de {Adapter} alterado para {Provider}", adapter.Name, provider.Name);
    }

    private static async Task Netsh(string args, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync("netsh.exe", args, ct: ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"O Windows recusou a alteração ({r.ExitCode}): {r.Output.Trim()}. Abra o programa como administrador.");
    }

    public Task FlushDnsAsync(CancellationToken ct) => ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", ct: ct);

    public async Task<string> QuickRepairAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Limpando o cache de DNS…");
        await FlushDnsAsync(ct);
        progress?.Report("Liberando o endereço IP…");
        await ProcessRunner.RunAsync("ipconfig.exe", "/release", ct: ct);
        progress?.Report("Pedindo um endereço IP novo ao roteador…");
        var renew = await ProcessRunner.RunAsync("ipconfig.exe", "/renew", ct: ct);
        progress?.Report("Registrando o computador no DNS…");
        await ProcessRunner.RunAsync("ipconfig.exe", "/registerdns", ct: ct);
        return renew.ExitCode == 0
            ? "Conexão renovada: IP novo e cache de DNS limpo."
            : "O IP não pôde ser renovado. Confira se o cabo/Wi-Fi está conectado e se o roteador está ligado.";
    }

    public async Task<string> FullRepairAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Redefinindo o Winsock…");
        var w = await ProcessRunner.RunAsync("netsh.exe", "winsock reset", ct: ct);
        progress?.Report("Redefinindo a pilha TCP/IP…");
        var ip = await ProcessRunner.RunAsync("netsh.exe", "int ip reset", ct: ct);
        progress?.Report("Limpando o cache de DNS…");
        await FlushDnsAsync(ct);
        if (w.ExitCode != 0 || ip.ExitCode != 0)
            throw new InvalidOperationException("O Windows recusou a redefinição. Abra o programa como administrador.");
        return "Rede redefinida. Reinicie o computador para concluir. DNS fixo e IP manual voltam para o automático.";
    }
}

using OtimizadorPC.Core.Network;

namespace OtimizadorPC.Tests.Network;

public sealed class NetworkStatsTests
{
    [Fact]
    public void Calcula_media_jitter_e_perda()
    {
        var s = NetworkStats.Compute([10, 20, null, 10, 20]);
        Assert.Equal(5, s.Sent);
        Assert.Equal(4, s.Received);
        Assert.Equal(15, s.Avg);
        Assert.Equal(10, s.Min);
        Assert.Equal(20, s.Max);
        Assert.Equal(10, s.Jitter);       // |20-10|, |10-20|, |20-10| → 10
        Assert.Equal(20, s.LossPercent);
    }

    [Fact]
    public void Sem_resposta_e_perda_total()
    {
        var s = NetworkStats.Compute([null, null]);
        Assert.Equal(100, s.LossPercent);
        Assert.Equal(ConnectionQuality.SemResposta, s.Quality);
    }

    [Theory]
    [InlineData(new double[] { 12, 13, 12, 14 }, ConnectionQuality.Excelente)]
    [InlineData(new double[] { 55, 58, 54, 57 }, ConnectionQuality.Boa)]
    [InlineData(new double[] { 90, 95, 92, 91 }, ConnectionQuality.Regular)]
    [InlineData(new double[] { 20, 120, 20, 120 }, ConnectionQuality.Ruim)]   // jitter alto
    [InlineData(new double[] { 200, 210, 205, 200 }, ConnectionQuality.Ruim)]
    public void Classifica_para_jogos(double[] ms, ConnectionQuality expected)
    {
        Assert.Equal(expected, NetworkStats.Compute(ms.Select(m => (double?)m).ToList()).Quality);
    }

    [Fact]
    public void Perda_de_pacotes_rebaixa_a_nota()
    {
        var samples = Enumerable.Repeat<double?>(10, 19).Append(null).ToList(); // 5% de perda
        Assert.Equal(ConnectionQuality.Ruim, NetworkStats.Compute(samples).Quality);
    }

    [Fact]
    public void Melhor_dns_ignora_quem_falhou_muito()
    {
        var cf = new DnsBenchmark(NetworkCatalog.DnsProviders[0], 9, 5, 10);   // rápido mas falhou metade
        var g = new DnsBenchmark(NetworkCatalog.DnsProviders[1], 20, 0, 10);
        var q = new DnsBenchmark(NetworkCatalog.DnsProviders[2], 30, 1, 10);
        Assert.Same(g, NetworkStats.Best([cf, g, q]));
    }

    [Fact]
    public void Monta_consulta_dns_valida()
    {
        var q = DnsPacket.BuildQuery(0xBEEF, "google.com");
        Assert.Equal(new byte[] { 0xBE, 0xEF, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, q[..12]);
        Assert.Equal(new byte[] { 6, (byte)'g', (byte)'o', (byte)'o', (byte)'g', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1 }, q[12..]);
    }

    [Fact]
    public void Confere_resposta_dns()
    {
        byte[] ok = [0xBE, 0xEF, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0];
        byte[] nxdomain = [0xBE, 0xEF, 0x81, 0x83, 0, 1, 0, 0, 0, 0, 0, 0];
        Assert.True(DnsPacket.IsValidResponse(ok, 0xBEEF));
        Assert.False(DnsPacket.IsValidResponse(ok, 0x1234));
        Assert.False(DnsPacket.IsValidResponse(nxdomain, 0xBEEF));
    }
}

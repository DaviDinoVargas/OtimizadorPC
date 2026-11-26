using OtimizadorPC.Core.DiskAnalysis;
using OtimizadorPC.Core.Settings;
using OtimizadorPC.Infrastructure.DiskAnalysis;

namespace OtimizadorPC.Tests.DiskAnalysis;

public sealed class DinosizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "otpc-dino-" + Guid.NewGuid().ToString("N"));

    public DinosizeTests() => Directory.CreateDirectory(_root);

    private void Make(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    // ---------------- Treemap ----------------

    [Fact]
    public void Treemap_ocupa_toda_a_area_sem_sobreposicao()
    {
        var values = new double[] { 60, 30, 20, 10, 5, 3, 1 };
        var bounds = new TreemapRect(0, 0, 400, 300);
        var rects = Treemap.Squarify(values, bounds);

        Assert.Equal(bounds.Area, rects.Sum(r => r.Area), 3);
        for (var i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            Assert.True(r.X >= -1e-6 && r.Y >= -1e-6 && r.X + r.Width <= 400 + 1e-6 && r.Y + r.Height <= 300 + 1e-6);
            Assert.Equal(values[i] / values.Sum() * bounds.Area, r.Area, 3);
            for (var j = i + 1; j < rects.Count; j++)
            {
                var o = rects[j];
                var overlapW = Math.Min(r.X + r.Width, o.X + o.Width) - Math.Max(r.X, o.X);
                var overlapH = Math.Min(r.Y + r.Height, o.Y + o.Height) - Math.Max(r.Y, o.Y);
                Assert.False(overlapW > 1e-6 && overlapH > 1e-6, $"Retângulos {i} e {j} se sobrepõem");
            }
        }
    }

    [Fact]
    public void Treemap_produz_formas_proximas_de_quadrados()
    {
        var rects = Treemap.Squarify(Enumerable.Repeat(1.0, 16).ToArray(), new TreemapRect(0, 0, 400, 400));
        Assert.All(rects, r => Assert.InRange(Math.Max(r.Width / r.Height, r.Height / r.Width), 1, 2.5));
    }

    // ---------------- MFT ----------------

    [Fact]
    public void Decodifica_runs_com_deslocamento_negativo()
    {
        // 0x31: 1 byte de tamanho, 3 bytes de deslocamento. Run 1: 16 clusters no LCN 0x0A0000.
        // 0x21: 1 byte de tamanho, 2 de deslocamento. Run 2: 8 clusters, deslocamento -16 (0xFFF0).
        byte[] data = [0x31, 0x10, 0x00, 0x00, 0x0A, 0x21, 0x08, 0xF0, 0xFF, 0x00];
        var runs = MftScanner.DecodeRuns(data);
        Assert.Equal([(0x0A0000L, 16L), (0x0A0000L - 16, 8L)], runs);
    }

    // ---------------- Motor rápido ----------------

    [Fact]
    public async Task Motor_rapido_soma_tamanhos_e_lista_os_maiores()
    {
        Make(@"fotos\a.jpg", 1000);
        Make(@"fotos\viagem\b.jpg", 3000);
        Make(@"jogos\data.pak", 10_000);
        Make(@"leia.txt", 10);

        var result = await new DiskScanner().ScanAsync(_root, DiskScanMode.Rapido, null, CancellationToken.None);

        Assert.Equal(ScanMethod.Rapido, result.Method);
        Assert.Equal(14_010, result.Root.Size);
        Assert.Equal(4, result.Root.TotalFiles);
        Assert.Equal("jogos", result.Root.Children[0].Name);          // ordenado do maior para o menor
        Assert.Equal(4000, result.Root.Children[1].Size);              // fotos inclui a subpasta
        Assert.Equal("data.pak", result.LargestFiles[0].Name);
        Assert.Equal(".jpg", result.Extensions.First(e => e.Extension == ".jpg").Extension);
        Assert.Equal(2, result.Extensions.First(e => e.Extension == ".jpg").Count);
        Assert.NotNull(result.Root.Find(Path.Combine(_root, "fotos", "viagem")));
    }

    [Fact]
    public async Task Motor_rapido_nao_soma_juncoes()
    {
        Make(@"real\x.bin", 5000);
        var outside = Path.Combine(Path.GetTempPath(), "otpc-dino-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "grande.bin"), new byte[50_000]);
        try
        {
            try { Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            var result = await new DiskScanner().ScanAsync(_root, DiskScanMode.Rapido, null, CancellationToken.None);
            Assert.Equal(5000, result.Root.Size);
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task Modo_mft_sem_permissao_cai_para_o_rapido_com_aviso()
    {
        Make("a.bin", 100);
        var scanner = new DiskScanner();
        var result = await scanner.ScanAsync(_root, DiskScanMode.Mft, null, CancellationToken.None);
        if (scanner.CanUseMft(_root))
            Assert.Equal(ScanMethod.Mft, result.Method);
        else
        {
            Assert.Equal(ScanMethod.Rapido, result.Method);
            Assert.False(string.IsNullOrEmpty(result.Note));
        }
        Assert.Equal(100, result.Root.Size);
    }

    [Fact]
    public void Categoria_de_arquivo_pela_extensao()
    {
        Assert.Equal("Vídeo", FileKinds.Of(".MKV"));
        Assert.Equal("Jogo / pacote", FileKinds.Of("pak"));
        Assert.Equal("Outros", FileKinds.Of(".xyz"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }
}

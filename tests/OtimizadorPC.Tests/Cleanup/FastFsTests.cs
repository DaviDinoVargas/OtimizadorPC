using OtimizadorPC.Infrastructure.Cleanup;
using OtimizadorPC.Infrastructure.FileSystem;

namespace OtimizadorPC.Tests.Cleanup;

public sealed class FastFsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "otpc-fs-" + Guid.NewGuid().ToString("N"));

    public FastFsTests() => Directory.CreateDirectory(_root);

    private string MakeFile(string relative, int bytes, DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (lastWriteUtc is { } d) File.SetLastWriteTimeUtc(path, d);
        return path;
    }

    [Fact]
    public void Mede_arquivos_recursivamente()
    {
        MakeFile("a.tmp", 100);
        MakeFile(@"sub\b.tmp", 250);
        MakeFile(@"sub\deep\c.log", 50);

        var (bytes, files) = FastFs.Measure(_root);
        Assert.Equal(400, bytes);
        Assert.Equal(3, files);

        var (logBytes, logFiles) = FastFs.Measure(_root, pattern: "*.log");
        Assert.Equal(50, logBytes);
        Assert.Equal(1, logFiles);
    }

    [Fact]
    public void Respeita_idade_minima_dos_arquivos()
    {
        MakeFile("velho.tmp", 100, DateTime.UtcNow.AddDays(-3));
        var novo = MakeFile("novo.tmp", 100);

        var stats = FastFs.DeleteContents(_root, olderThanUtc: DateTime.UtcNow.AddDays(-1));

        Assert.Equal(100, stats.FreedBytes);
        Assert.Equal(1, stats.DeletedFiles);
        Assert.True(File.Exists(novo));
    }

    [Fact]
    public void Apaga_somente_leitura_e_remove_pastas_vazias()
    {
        var ro = MakeFile(@"x\y\ro.bin", 10);
        File.SetAttributes(ro, FileAttributes.ReadOnly);

        var stats = FastFs.DeleteContents(_root);

        Assert.Equal(1, stats.DeletedFiles);
        Assert.False(Directory.Exists(Path.Combine(_root, "x")));
        Assert.True(Directory.Exists(_root)); // raiz preservada sem removeRoot
    }

    [Fact]
    public void Arquivo_em_uso_e_ignorado_sem_falhar()
    {
        var locked = MakeFile("aberto.tmp", 10);
        MakeFile("livre.tmp", 20);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var stats = FastFs.DeleteContents(_root);
            Assert.Equal(1, stats.DeletedFiles);
            Assert.Equal(1, stats.SkippedFiles);
        }
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void Nao_segue_juncoes_para_fora_da_pasta()
    {
        var outside = Path.Combine(Path.GetTempPath(), "otpc-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "importante.txt");
        File.WriteAllText(precious, "não apagar");
        try
        {
            var link = Path.Combine(_root, "atalho");
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (IOException) { return; } // sem permissão para criar link: cenário não testável aqui
            catch (UnauthorizedAccessException) { return; }

            FastFs.DeleteContents(_root);
            Assert.True(File.Exists(precious));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task Servico_lista_categorias_e_analisa_sem_apagar()
    {
        var service = new CleanupService();
        Assert.Contains(service.Categories, c => c.Id == "temp-usuario");
        Assert.Contains(service.Categories, c => c.Id == "lixeira");

        var tempBefore = Directory.Exists(Path.GetTempPath());
        var scan = await service.ScanAsync("temp-usuario");
        Assert.True(scan.SizeBytes >= 0);
        Assert.Equal(tempBefore, Directory.Exists(Path.GetTempPath()));
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch (Exception) { }
    }
}

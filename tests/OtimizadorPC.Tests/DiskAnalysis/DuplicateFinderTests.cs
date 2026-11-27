using OtimizadorPC.Core.DiskAnalysis;

namespace OtimizadorPC.Tests.DiskAnalysis;

public sealed class DuplicateFinderTests
{
    /// <summary>Hasher falso: conteúdo definido por dicionário; conta quantas leituras completas foram feitas.</summary>
    private sealed class FakeHasher(Dictionary<string, string> content, Dictionary<string, string>? identity = null, long partialCovers = 128 * 1024) : IFileHasher
    {
        public int FullReads;
        public long PartialCoversWholeFileUpTo => partialCovers;
        public Task<string?> PartialAsync(string path, long size, CancellationToken ct) =>
            Task.FromResult<string?>(content.TryGetValue(path, out var c) ? c[..Math.Min(2, c.Length)] : null);
        public Task<string?> FullAsync(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref FullReads);
            return Task.FromResult<string?>(content.TryGetValue(path, out var c) ? c : null);
        }
        public string? Identity(string path) => identity is not null && identity.TryGetValue(path, out var id) ? id : path;
    }

    private static readonly DateTime D = new(2026, 1, 1);
    private const long Mb = 1024 * 1024;

    [Fact]
    public async Task Agrupa_arquivos_com_conteudo_igual()
    {
        var files = new[]
        {
            new DuplicateFile(@"C:\a\foto.jpg", 5 * Mb, D),
            new DuplicateFile(@"D:\b\foto.jpg", 5 * Mb, D),
            new DuplicateFile(@"D:\c\outra.jpg", 5 * Mb, D),   // mesmo tamanho, conteúdo diferente
            new DuplicateFile(@"D:\c\unico.bin", 7 * Mb, D),   // tamanho único: nem é lido
        };
        var hasher = new FakeHasher(new()
        {
            [@"C:\a\foto.jpg"] = "XYconteudo1",
            [@"D:\b\foto.jpg"] = "XYconteudo1",
            [@"D:\c\outra.jpg"] = "XYconteudo2",
        });

        var groups = await DuplicateFinder.FindAsync(files, hasher, null, CancellationToken.None);

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Files.Count);
        Assert.Equal(5 * Mb, g.Wasted);
        Assert.Equal(3, hasher.FullReads); // só os três com mesmo tamanho e mesmo início/fim
    }

    [Fact]
    public async Task Inicio_diferente_descarta_sem_ler_tudo()
    {
        var files = new[] { new DuplicateFile("a", 9 * Mb, D), new DuplicateFile("b", 9 * Mb, D) };
        var hasher = new FakeHasher(new() { ["a"] = "AAx", ["b"] = "BBx" });
        Assert.Empty(await DuplicateFinder.FindAsync(files, hasher, null, CancellationToken.None));
        Assert.Equal(0, hasher.FullReads);
    }

    [Fact]
    public async Task Arquivos_pequenos_usam_so_o_resumo_rapido()
    {
        var files = new[] { new DuplicateFile("a", 1000, D), new DuplicateFile("b", 1000, D) };
        var hasher = new FakeHasher(new() { ["a"] = "ZZ", ["b"] = "ZZ" });
        Assert.Single(await DuplicateFinder.FindAsync(files, hasher, null, CancellationToken.None));
        Assert.Equal(0, hasher.FullReads);
    }

    [Fact]
    public async Task Links_fisicos_nao_sao_duplicados()
    {
        var files = new[] { new DuplicateFile(@"C:\x\a.dll", 3 * Mb, D), new DuplicateFile(@"C:\y\a.dll", 3 * Mb, D) };
        var hasher = new FakeHasher(new() { [@"C:\x\a.dll"] = "QQ1", [@"C:\y\a.dll"] = "QQ1" },
            identity: new() { [@"C:\x\a.dll"] = "vol1:42", [@"C:\y\a.dll"] = "vol1:42" });
        Assert.Empty(await DuplicateFinder.FindAsync(files, hasher, null, CancellationToken.None));
    }

    [Fact]
    public async Task Arquivo_ilegivel_fica_de_fora()
    {
        var files = new[] { new DuplicateFile("a", 2 * Mb, D), new DuplicateFile("b", 2 * Mb, D), new DuplicateFile("bloqueado", 2 * Mb, D) };
        var hasher = new FakeHasher(new() { ["a"] = "KK1", ["b"] = "KK1" });
        var g = Assert.Single(await DuplicateFinder.FindAsync(files, hasher, null, CancellationToken.None));
        Assert.DoesNotContain(g.Files, f => f.Path == "bloqueado");
    }

    [Fact]
    public async Task Ordena_pelos_grupos_que_mais_desperdicam()
    {
        var files = new[]
        {
            new DuplicateFile("p1", 2 * Mb, D), new DuplicateFile("p2", 2 * Mb, D),
            new DuplicateFile("g1", 50 * Mb, D), new DuplicateFile("g2", 50 * Mb, D),
        };
        var hasher = new FakeHasher(new() { ["p1"] = "PP", ["p2"] = "PP", ["g1"] = "GG", ["g2"] = "GG" });
        var groups = await DuplicateFinder.FindAsync(files, hasher, null, CancellationToken.None);
        Assert.Equal(50 * Mb, groups[0].Size);
    }

    [Fact]
    public void Sugere_manter_o_original_e_nao_a_copia()
    {
        var g = new DuplicateGroup("h", 10, [
            new DuplicateFile(@"C:\Users\x\Downloads\relatorio.pdf", 10, D),
            new DuplicateFile(@"C:\Users\x\Documentos\relatorio - Cópia.pdf", 10, D.AddDays(-9)),
            new DuplicateFile(@"C:\Users\x\Documentos\Trabalho\2026\relatorio.pdf", 10, D.AddDays(3)),
        ]);
        Assert.Equal(@"C:\Users\x\Documentos\Trabalho\2026\relatorio.pdf", DuplicateFinder.SuggestKeep(g).Path);
    }

    [Fact]
    public void Pasta_com_nome_de_copia_nao_e_o_original()
    {
        var g = new DuplicateGroup("h", 10, [
            new DuplicateFile(@"A:\Game\Jogo - Copia\build\jogo.exe", 10, D),
            new DuplicateFile(@"A:\Game\Jogo\build\versao-final\jogo.exe", 10, D),
        ]);
        Assert.Equal(@"A:\Game\Jogo\build\versao-final\jogo.exe", DuplicateFinder.SuggestKeep(g).Path);
    }

    [Fact]
    public void Entre_copias_equivalentes_mantem_o_caminho_mais_curto()
    {
        var g = new DuplicateGroup("h", 10, [
            new DuplicateFile(@"D:\Fotos\2024\Viagem\img.jpg", 10, D),
            new DuplicateFile(@"D:\Fotos\img.jpg", 10, D),
        ]);
        Assert.Equal(@"D:\Fotos\img.jpg", DuplicateFinder.SuggestKeep(g).Path);
    }
}

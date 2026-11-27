using OtimizadorPC.Core.Cleanup;
using OtimizadorPC.Core.GameMode;
using OtimizadorPC.Core.Processes;

namespace OtimizadorPC.Tests.GameMode;

public sealed class GameModeRulesTests
{
    private static ProcessSample P(string name, string? path = null, bool window = true, ProcessCategory cat = ProcessCategory.Aplicativo, int pid = 100) =>
        new(pid, name, path, name, "", 1, 0, 1, window, null, cat, null);

    [Theory]
    [InlineData("cs2", @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", true)]
    [InlineData("League of Legends", @"A:\Riot Games\League of Legends\Game\League of Legends.exe", true)]
    [InlineData("GenshinImpact", @"D:\HoYoPlay\games\Genshin Impact game\GenshinImpact.exe", true)]
    [InlineData("LeagueClientUx", @"A:\Riot Games\League of Legends\LeagueClientUx.exe", false)]   // launcher
    [InlineData("steam", @"C:\Program Files (x86)\Steam\steam.exe", false)]
    [InlineData("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    public void Reconhece_jogos_e_ignora_launchers(string name, string path, bool expected)
    {
        Assert.Equal(expected, GameModeRules.IsGame(P(name, path)));
    }

    [Fact]
    public void Jogo_conhecido_fora_da_pasta_padrao_tambem_conta()
    {
        Assert.True(GameModeRules.IsGame(P("VALORANT-Win64-Shipping", @"E:\Jogos\VALORANT\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe")));
    }

    [Fact]
    public void Fecha_so_dispensaveis_e_nunca_o_proprio_programa()
    {
        var list = new[]
        {
            P("OneDrive", cat: ProcessCategory.Dispensavel, pid: 1),
            P("OtimizadorPC", cat: ProcessCategory.Dispensavel, pid: 2),
            P("Discord", cat: ProcessCategory.Aplicativo, pid: 3),
            P("svchost", cat: ProcessCategory.Sistema, pid: 4),
        };
        var close = GameModeRules.ToClose(list, ownPid: 2);
        Assert.Equal(["OneDrive"], close.Select(p => p.Name));
    }

    [Fact]
    public void Limpeza_automatica_so_com_categorias_seguras_e_rapidas()
    {
        CleanupCategory C(string id, CleanupRisk risk = CleanupRisk.Seguro, bool selected = true, bool entries = false, bool after = false) =>
            new(id, id, "", CleanupGroup.Sistema, risk, false, selected, entries, after);
        var auto = GameModeRules.AutomaticCleanup(
        [
            C("temp"), C("lixeira"), C("componentes", after: true), C("orfaos", entries: true),
            C("windows-old", CleanupRisk.Atencao), C("opcional", selected: false),
        ]);
        Assert.Equal(["temp"], auto.Select(c => c.Id));
    }

    [Fact]
    public void Calcula_a_proxima_execucao()
    {
        var quarta10h = new DateTime(2026, 11, 25, 10, 0, 0); // quarta-feira
        Assert.Equal(new DateTime(2026, 11, 25, 12, 0, 0), GameModeRules.NextRun(quarta10h, CleanupFrequency.Diaria, DayOfWeek.Monday, 12));
        Assert.Equal(new DateTime(2026, 11, 26, 9, 0, 0), GameModeRules.NextRun(quarta10h, CleanupFrequency.Diaria, DayOfWeek.Monday, 9));
        Assert.Equal(new DateTime(2026, 11, 30, 12, 0, 0), GameModeRules.NextRun(quarta10h, CleanupFrequency.Semanal, DayOfWeek.Monday, 12));
        Assert.Equal(new DateTime(2026, 12, 2, 9, 0, 0), GameModeRules.NextRun(quarta10h, CleanupFrequency.Semanal, DayOfWeek.Wednesday, 9));
    }
}

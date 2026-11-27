using OtimizadorPC.Core.Programs;

namespace OtimizadorPC.Tests.Programs;

public sealed class ProgramRulesTests
{
    private static InstalledProgram P(string name, string? publisher = null, string? uninstall = null, string? quiet = null, bool msi = false, string id = "x") => new()
    {
        Id = id, Name = name, Publisher = publisher, UninstallString = uninstall, QuietUninstallString = quiet, IsMsi = msi,
    };

    [Theory]
    [InlineData("Discord", false, null, null, true, true)]
    [InlineData("", false, null, null, true, false)]
    [InlineData("Componente", true, null, null, true, false)]
    [InlineData("Atualização", false, "Office16", null, true, false)]
    [InlineData("Security Update for X", false, null, "Security Update", true, false)]
    [InlineData("Hotfix for Windows (KB2504637)", false, null, null, true, false)]
    [InlineData("Sem desinstalador", false, null, null, false, false)]
    public void Decide_quais_entradas_aparecem(string name, bool system, string? parent, string? release, bool hasUninstaller, bool expected)
    {
        Assert.Equal(expected, ProgramRules.ShouldList(name, system, parent, release, hasUninstaller));
    }

    [Fact]
    public void Le_a_data_de_instalacao_do_registro()
    {
        Assert.Equal(new DateTime(2025, 3, 14), ProgramRules.ParseInstallDate("20250314"));
        Assert.Null(ProgramRules.ParseInstallDate("lixo"));
        Assert.Null(ProgramRules.ParseInstallDate(null));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /SILENT", "C:\\Program Files\\App\\unins000.exe", "/SILENT")]
    [InlineData("C:\\Program Files\\App\\uninstall.exe /S", "C:\\Program Files\\App\\uninstall.exe", "/S")]
    [InlineData("MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", "MsiExec.exe", "/X{11111111-2222-3333-4444-555555555555}")]
    [InlineData("\"C:\\App\\Update.exe\"", "C:\\App\\Update.exe", "")]
    [InlineData("rundll32 shell32.dll,Control_RunDLL", "rundll32", "shell32.dll,Control_RunDLL")]
    public void Separa_executavel_e_argumentos(string command, string file, string args)
    {
        var c = ProgramRules.Parse(command)!;
        Assert.Equal(file, c.FileName);
        Assert.Equal(args, c.Arguments);
    }

    [Fact]
    public void Msi_desinstala_com_x_e_modo_passivo_no_silencioso()
    {
        var p = P("App", uninstall: "MsiExec.exe /I{11111111-2222-3333-4444-555555555555}", msi: true);
        var quiet = ProgramRules.UninstallCommand(p, quiet: true)!;
        Assert.Equal("msiexec.exe", quiet.FileName);
        Assert.Equal("/x {11111111-2222-3333-4444-555555555555} /passive /norestart", quiet.Arguments);
        Assert.Equal("/x {11111111-2222-3333-4444-555555555555}", ProgramRules.UninstallCommand(p, quiet: false)!.Arguments);
    }

    [Fact]
    public void Usa_o_comando_silencioso_do_instalador_quando_existe()
    {
        var p = P("App", uninstall: "\"C:\\A\\u.exe\"", quiet: "\"C:\\A\\u.exe\" /S");
        Assert.Equal("/S", ProgramRules.UninstallCommand(p, quiet: true)!.Arguments);
        Assert.Equal("", ProgramRules.UninstallCommand(p, quiet: false)!.Arguments);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(1605, true, false)]
    [InlineData(1602, false, false)]
    [InlineData(5, false, false)]
    public void Interpreta_codigos_de_saida(int code, bool success, bool reboot)
    {
        var r = ProgramRules.InterpretExitCode(code);
        Assert.Equal(success, r.Success);
        Assert.Equal(reboot, r.RebootRequired);
    }

    [Theory]
    [InlineData("Discord", "discord")]
    [InlineData("7-Zip 24.08 (x64)", "7zip")]
    [InlineData("Notepad++ (64-bit x64)", "notepad")]
    [InlineData("Ação Câmera v2.1", "acaocamera")]
    public void Normaliza_nomes(string name, string expected)
    {
        Assert.Equal(expected, ProgramRules.Normalize(name));
    }

    [Fact]
    public void Pasta_com_o_nome_do_programa_e_sobra()
    {
        Assert.True(ProgramRules.IsLeftoverFolder("Discord", P("Discord", "Discord Inc."), []));
    }

    [Fact]
    public void Pasta_do_fabricante_so_e_sobra_se_nenhum_outro_programa_for_dele()
    {
        var removido = P("Steam Link", "Valve Corporation");
        Assert.True(ProgramRules.IsLeftoverFolder("Valve", removido, []));
        Assert.False(ProgramRules.IsLeftoverFolder("Valve", removido, [P("Steam", "Valve Corporation")]));
    }

    [Theory]
    [InlineData("Microsoft")]
    [InlineData("Common Files")]
    [InlineData("NVIDIA Corporation")]
    public void Pastas_genericas_nunca_sao_sobra(string folder)
    {
        Assert.False(ProgramRules.IsLeftoverFolder(folder, P(folder, folder), []));
    }

    [Fact]
    public void Pasta_de_outro_programa_ainda_instalado_com_mesmo_nome_nao_e_sobra()
    {
        Assert.False(ProgramRules.IsLeftoverFolder("Zoom", P("Zoom"), [P("Zoom (64-bit)", id: "y")]));
    }

    [Fact]
    public void Pasta_dividida_ou_que_contem_outra_nao_e_medida()
    {
        InstalledProgram L(string id, string? loc) => P(id, id: id) with { InstallLocation = loc };
        var ids = ProgramRules.MeasurableIds(
        [
            L("genshin", @"D:\HoYoPlay"), L("zzz", @"D:\HoYoPlay\"), // mesma pasta do launcher
            L("lol", "A:/Riot Games/League of Legends"),              // barra normal também conta
            L("riot", @"A:\Riot Games"),                              // contém a pasta de outro programa
            L("discord", @"C:\Users\x\AppData\Local\Discord"),
            L("semPasta", null),
        ]);
        Assert.Equal(["discord", "lol"], ids.Order());
    }

    [Fact]
    public void Limpa_sufixos_do_fabricante()
    {
        Assert.Equal("Valve", ProgramRules.CleanPublisher("Valve Corporation"));
        Assert.Equal("Discord", ProgramRules.CleanPublisher("Discord Inc."));
        Assert.Equal("Mozilla", ProgramRules.CleanPublisher("Mozilla"));
    }
}

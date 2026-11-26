using OtimizadorPC.Core.Cleanup;

namespace OtimizadorPC.Tests.Cleanup;

public sealed class SteamLibraryTests
{
    private const string LibraryFolders = """
        "libraryfolders"
        {
            "0"
            {
                "path"		"E:\\Steam"
                "label"		""
                "apps" { "105600" "1" }
            }
            "1"
            {
                "path"		"D:\\SteamLibrary"
            }
        }
        """;

    private const string Manifest = """
        "AppState"
        {
            "appid"		"105600"
            "name"		"Terraria"
            "installdir"		"Terraria"
        }
        """;

    [Fact]
    public void Le_as_bibliotecas_com_barras_desescapadas()
    {
        var libs = SteamLibrary.ParseLibraryFolders(LibraryFolders);
        Assert.Equal([@"E:\Steam", @"D:\SteamLibrary"], libs);
    }

    [Fact]
    public void Le_pasta_e_nome_do_manifesto()
    {
        Assert.Equal("Terraria", SteamLibrary.ParseInstallDir(Manifest));
        Assert.Equal("Terraria", SteamLibrary.ParseName(Manifest));
        Assert.Null(SteamLibrary.ParseInstallDir("{}"));
    }

    [Fact]
    public void Encontra_pastas_sem_manifesto_ignorando_maiusculas()
    {
        var pastas = new[] { "Terraria", "HonkaiImpact3rd", "BioShock Remastered", "Steamworks Shared", "stardew valley" };
        var manifestos = new[] { "terraria", "Stardew Valley" };

        var orfas = SteamLibrary.FindOrphanFolders(pastas, manifestos);

        Assert.Equal(["BioShock Remastered", "HonkaiImpact3rd"], orfas);
    }
}

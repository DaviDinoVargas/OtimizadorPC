using OtimizadorPC.Core.Settings;
using OtimizadorPC.Infrastructure.Settings;

namespace OtimizadorPC.Tests.Settings;

public sealed class JsonSettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otpc-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Sem_arquivo_usa_valores_padrao()
    {
        var service = new JsonSettingsService(FilePath);
        Assert.Equal(ThemeMode.Escuro, service.Current.Theme);
        Assert.True(service.Current.AnimationsEnabled);
    }

    [Fact]
    public void Alteracao_e_persistida_e_relida()
    {
        var service = new JsonSettingsService(FilePath);
        service.Update(s => { s.Theme = ThemeMode.Claro; s.SidebarMode = SidebarMode.Compacta; s.AccentColor = "#FF0000"; });

        var reloaded = new JsonSettingsService(FilePath);
        Assert.Equal(ThemeMode.Claro, reloaded.Current.Theme);
        Assert.Equal(SidebarMode.Compacta, reloaded.Current.SidebarMode);
        Assert.Equal("#FF0000", reloaded.Current.AccentColor);
    }

    [Fact]
    public void Arquivo_corrompido_volta_ao_padrao()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ isto nao e json");
        var service = new JsonSettingsService(FilePath);
        Assert.Equal(ThemeMode.Escuro, service.Current.Theme);
    }

    [Fact]
    public void Changed_e_disparado_ao_atualizar()
    {
        var service = new JsonSettingsService(FilePath);
        AppSettings? recebido = null;
        service.Changed += (_, s) => recebido = s;
        service.Update(s => s.UiScale = 1.25);
        Assert.NotNull(recebido);
        Assert.Equal(1.25, recebido!.UiScale);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.GameMode;
using Serilog;

namespace OtimizadorPC.App.Services;

/// <summary>Limpeza segura sem janela: usada pela tarefa agendada e pelo botão "Executar agora".</summary>
public static class SilentCleanup
{
    public static async Task<long> RunAsync(ICleanupService cleanup, ISettingsService settings, CancellationToken ct = default)
    {
        long freed = 0;
        foreach (var category in GameModeRules.AutomaticCleanup(cleanup.Categories))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await cleanup.CleanAsync(category.Id, null, null, ct);
                if (r.Success) freed += r.FreedBytes;
                else Log.Information("Limpeza automática pulou {Category}: {Error}", category.Name, r.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "Limpeza automática falhou em {Category}", category.Name);
            }
        }
        settings.Update(s =>
        {
            s.LastAutoCleanup = DateTime.Now;
            s.LastAutoCleanupBytes = freed;
        });
        Log.Information("Limpeza automática concluída: {Bytes} bytes liberados", freed);
        return freed;
    }
}

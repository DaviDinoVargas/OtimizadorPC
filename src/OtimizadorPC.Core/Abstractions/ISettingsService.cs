using OtimizadorPC.Core.Settings;

namespace OtimizadorPC.Core.Abstractions;

public interface ISettingsService
{
    AppSettings Current { get; }

    /// <summary>Disparado sempre que uma configuração é alterada e salva.</summary>
    event EventHandler<AppSettings>? Changed;

    void Update(Action<AppSettings> change);

    void Reset();
}

using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Hardware;

namespace OtimizadorPC.App.Services;

/// <summary>Guarda a última foto do sistema para que várias telas não refaçam a coleta.</summary>
public sealed class SnapshotProvider
{
    private readonly IHardwareInfoService _hardware;
    private Task<SystemSnapshot>? _pending;
    private SystemSnapshot? _cached;

    public SnapshotProvider(IHardwareInfoService hardware) => _hardware = hardware;

    public event EventHandler<SystemSnapshot>? Refreshed;

    public SystemSnapshot? Current => _cached;

    /// <summary>Devolve a foto em cache (até 30 s) ou coleta uma nova.</summary>
    public async Task<SystemSnapshot> GetAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cached is not null && DateTime.Now - _cached.TakenAt < TimeSpan.FromSeconds(30))
            return _cached;

        _pending ??= _hardware.GetSnapshotAsync();
        try
        {
            _cached = await _pending;
            Refreshed?.Invoke(this, _cached);
            return _cached;
        }
        finally
        {
            _pending = null;
        }
    }
}

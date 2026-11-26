using System.Windows;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Hardware;
using Serilog;

namespace OtimizadorPC.App.Services;

/// <summary>
/// Lê os sensores uma vez por segundo em segundo plano enquanto houver alguma tela
/// interessada, e entrega as leituras na thread da interface.
/// </summary>
public sealed class SensorMonitor : IDisposable
{
    private readonly ISensorService _sensors;
    private readonly object _gate = new();
    private readonly List<Action<LiveMetrics>> _handlers = [];
    private CancellationTokenSource? _cts;

    public SensorMonitor(ISensorService sensors) => _sensors = sensors;

    public LiveMetrics? Last { get; private set; }

    public bool HasDriverAccess => _sensors.HasDriverAccess;

    public void Subscribe(Action<LiveMetrics> handler)
    {
        lock (_gate)
        {
            _handlers.Add(handler);
            if (_handlers.Count == 1) Start();
        }
        if (Last is not null) handler(Last);
    }

    public void Unsubscribe(Action<LiveMetrics> handler)
    {
        lock (_gate)
        {
            _handlers.Remove(handler);
            if (_handlers.Count == 0) Stop();
        }
    }

    private void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                try
                {
                    var metrics = _sensors.Sample();
                    Last = metrics;
                    Action<LiveMetrics>[] copy;
                    lock (_gate) copy = [.. _handlers];
                    _ = Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        foreach (var h in copy) h(metrics);
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Falha ao ler sensores");
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
        _sensors.Dispose();
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Optimization;

namespace OtimizadorPC.Infrastructure.Optimization;

/// <summary>Ajustes do Windows feitos por registro e APIs do sistema, todos reversíveis.</summary>
[SupportedOSPlatform("windows")]
public sealed class TweakService : ITweakService
{
    private sealed record Tweak(TweakInfo Info, Func<bool> IsOn, Action<bool> Set);

    private readonly List<Tweak> _tweaks;

    private const string Hkcu = "HKEY_CURRENT_USER";
    private const string Hklm = "HKEY_LOCAL_MACHINE";
    private const string GamesTask = Hklm + @"\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
    private const string SystemProfile = Hklm + @"\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string ContentDelivery = Hkcu + @"\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    public TweakService()
    {
        _tweaks =
        [
            // ---------------- Jogos ----------------
            new(new("modo-jogo", "Modo de Jogo", "O Windows prioriza o jogo aberto e adia atualizações enquanto você joga.",
                    TweakCategory.Jogos, false, false, true),
                () => Dword(Hkcu + @"\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1) == 1,
                on => SetDword(Hkcu + @"\Software\Microsoft\GameBar", "AutoGameModeEnabled", on ? 1 : 0)),

            new(new("gamedvr-off", "Desligar gravação em segundo plano (Xbox Game Bar)", "A captura contínua de gameplay consome GPU e disco. A Game Bar continua abrindo com Win+G.",
                    TweakCategory.Jogos, false, false, true),
                () => Dword(Hkcu + @"\System\GameConfigStore", "GameDVR_Enabled", 1) == 0 &&
                      Dword(Hkcu + @"\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1) == 0,
                on =>
                {
                    SetDword(Hkcu + @"\System\GameConfigStore", "GameDVR_Enabled", on ? 0 : 1);
                    SetDword(Hkcu + @"\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", on ? 0 : 1);
                }),

            new(new("hags", "Agendamento de GPU acelerado por hardware", "A placa de vídeo gerencia a própria memória, reduzindo a latência. Requer placa e driver compatíveis.",
                    TweakCategory.Jogos, true, true, true),
                () => Dword(Hklm + @"\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 1) == 2,
                on => SetDword(Hklm + @"\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", on ? 2 : 1)),

            new(new("prioridade-jogos", "Prioridade de CPU e GPU para jogos", "Dá mais tempo de processador e GPU para jogos e áudio em vez de tarefas em segundo plano.",
                    TweakCategory.Jogos, true, false, true),
                () => Dword(SystemProfile, "SystemResponsiveness", 20) <= 10 && Dword(GamesTask, "Priority", 2) == 6,
                on =>
                {
                    SetDword(SystemProfile, "SystemResponsiveness", on ? 10 : 20);
                    SetDword(GamesTask, "GPU Priority", 8);
                    SetDword(GamesTask, "Priority", on ? 6 : 2);
                    Registry.SetValue(GamesTask, "Scheduling Category", on ? "High" : "Medium");
                    Registry.SetValue(GamesTask, "SFIO Priority", on ? "High" : "Normal");
                }),

            new(new("mouse-precisao-off", "Desligar aceleração do mouse", "Desativa \"Aprimorar precisão do ponteiro\": o movimento passa a ser 1:1, como preferem jogadores de tiro.",
                    TweakCategory.Jogos, false, false, false),
                () => Str(Hkcu + @"\Control Panel\Mouse", "MouseSpeed", "1") == "0",
                on =>
                {
                    Registry.SetValue(Hkcu + @"\Control Panel\Mouse", "MouseSpeed", on ? "0" : "1");
                    Registry.SetValue(Hkcu + @"\Control Panel\Mouse", "MouseThreshold1", on ? "0" : "6");
                    Registry.SetValue(Hkcu + @"\Control Panel\Mouse", "MouseThreshold2", on ? "0" : "10");
                    var values = on ? new[] { 0, 0, 0 } : new[] { 6, 10, 1 };
                    SystemParametersInfo(SpiSetMouse, 0, values, SpifUpdateIniFile | SpifSendChange);
                }),

            // ---------------- Desempenho ----------------
            new(new("hibernacao-off", "Desligar hibernação", "Apaga o hiberfil.sys (do tamanho da RAM) no disco do sistema. Também desliga a Inicialização Rápida.",
                    TweakCategory.Desempenho, true, false, false),
                () => Dword(Hklm + @"\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", 1) == 0,
                on => RunPowercfg(on ? "/hibernate off" : "/hibernate on")),

            new(new("apps-segundo-plano-off", "Bloquear apps da Store em segundo plano", "Impede que apps da Microsoft Store rodem escondidos. Apps como WhatsApp podem deixar de notificar.",
                    TweakCategory.Desempenho, false, false, false),
                () => Dword(Hkcu + @"\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 0) == 1,
                on => SetDword(Hkcu + @"\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", on ? 1 : 0)),

            // ---------------- Visual ----------------
            new(new("animacoes-off", "Desligar animações de janelas", "Janelas abrem, minimizam e maximizam instantaneamente.",
                    TweakCategory.Visual, false, false, false),
                () => Str(Hkcu + @"\Control Panel\Desktop\WindowMetrics", "MinAnimate", "1") == "0",
                on =>
                {
                    Registry.SetValue(Hkcu + @"\Control Panel\Desktop\WindowMetrics", "MinAnimate", on ? "0" : "1");
                    var info = new AnimationInfo { Size = (uint)Marshal.SizeOf<AnimationInfo>(), MinAnimate = on ? 0 : 1 };
                    SystemParametersInfo(SpiSetAnimation, info.Size, ref info, SpifUpdateIniFile | SpifSendChange);
                    SystemParametersInfo(SpiSetClientAreaAnimation, 0, on ? IntPtr.Zero : 1, SpifUpdateIniFile | SpifSendChange);
                }),

            new(new("transparencia-off", "Desligar transparência", "Remove o efeito de vidro da barra de tarefas e do menu Iniciar.",
                    TweakCategory.Visual, false, false, false),
                () => Dword(Hkcu + @"\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) == 0,
                on => SetDword(Hkcu + @"\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", on ? 0 : 1)),

            // ---------------- Privacidade ----------------
            new(new("dicas-off", "Desligar dicas, sugestões e anúncios do Windows", "Remove sugestões no Iniciar, dicas e \"boas-vindas\" depois das atualizações.",
                    TweakCategory.Privacidade, false, false, true),
                () => Dword(ContentDelivery, "SubscribedContent-338389Enabled", 1) == 0 && Dword(ContentDelivery, "SystemPaneSuggestionsEnabled", 1) == 0,
                on =>
                {
                    foreach (var v in new[] { "SubscribedContent-338389Enabled", "SubscribedContent-310093Enabled", "SubscribedContent-338388Enabled",
                                 "SubscribedContent-353698Enabled", "SystemPaneSuggestionsEnabled", "SoftLandingEnabled" })
                        SetDword(ContentDelivery, v, on ? 0 : 1);
                }),

            new(new("id-anuncio-off", "Desligar ID de publicidade", "Apps deixam de usar um identificador para anúncios personalizados.",
                    TweakCategory.Privacidade, false, false, true),
                () => Dword(Hkcu + @"\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1) == 0,
                on => SetDword(Hkcu + @"\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", on ? 0 : 1)),

            new(new("telemetria-minima", "Reduzir telemetria ao mínimo", "Envia à Microsoft apenas os dados de diagnóstico obrigatórios.",
                    TweakCategory.Privacidade, true, false, true),
                () => Dword(Hklm + @"\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 3) <= 1,
                on =>
                {
                    if (on) SetDword(Hklm + @"\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1);
                    else DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry");
                }),
        ];
    }

    public IReadOnlyList<TweakState> List() =>
        _tweaks.Select(t =>
        {
            bool on;
            try { on = t.IsOn(); } catch (Exception) { on = false; }
            return new TweakState(t.Info, on);
        }).ToList();

    public void Apply(string id, bool on)
    {
        var t = _tweaks.FirstOrDefault(x => x.Info.Id == id) ?? throw new ArgumentException($"Ajuste desconhecido: {id}");
        try
        {
            t.Set(on);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Este ajuste precisa do OtimizadorPC aberto como administrador.");
        }
        catch (System.Security.SecurityException)
        {
            throw new InvalidOperationException("Este ajuste precisa do OtimizadorPC aberto como administrador.");
        }
    }

    // ---------------- Utilitários ----------------

    private static int Dword(string key, string name, int fallback) =>
        Registry.GetValue(key, name, null) is int v ? v : fallback;

    private static string Str(string key, string name, string fallback) =>
        Registry.GetValue(key, name, null)?.ToString() ?? fallback;

    private static void SetDword(string key, string name, int value) =>
        Registry.SetValue(key, name, value, RegistryValueKind.DWord);

    private static void DeleteValue(RegistryKey root, string subKey, string name)
    {
        using var k = root.OpenSubKey(subKey, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void RunPowercfg(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(15000);
        if (p.ExitCode != 0) throw new InvalidOperationException("O powercfg recusou a alteração. Execute como administrador.");
    }

    private const uint SpiSetMouse = 0x0004;
    private const uint SpiSetAnimation = 0x0049;
    private const uint SpiSetClientAreaAnimation = 0x1043;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint Size;
        public int MinAnimate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] values, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref AnimationInfo info, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);
}

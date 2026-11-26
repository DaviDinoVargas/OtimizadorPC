namespace OtimizadorPC.Core.Settings;

public enum ThemeMode { Sistema, Claro, Escuro }

public enum BackdropMode { Mica, Acrilico, Abas, Nenhum }

public enum SidebarPosition { Esquerda, Direita }

public enum SidebarMode { Expandida, Compacta }

public enum LayoutDensity { Confortavel, Compacta }

public enum TransitionKind { Deslizar, Esmaecer, Zoom, Nenhuma }

public enum DiskScanMode { Automatico, Rapido, Mft }

/// <summary>Preferências do usuário, salvas em %APPDATA%\OtimizadorPC\settings.json.</summary>
public sealed class AppSettings
{
    // Aparência
    public ThemeMode Theme { get; set; } = ThemeMode.Escuro;
    public bool UseSystemAccent { get; set; }
    public string AccentColor { get; set; } = "#14B8A6";
    public BackdropMode Backdrop { get; set; } = BackdropMode.Mica;

    // Layout
    public SidebarPosition SidebarPosition { get; set; } = SidebarPosition.Esquerda;
    public SidebarMode SidebarMode { get; set; } = SidebarMode.Expandida;
    public LayoutDensity Density { get; set; } = LayoutDensity.Confortavel;
    public double UiScale { get; set; } = 1.0;

    // Animações
    public bool AnimationsEnabled { get; set; } = true;
    public TransitionKind Transition { get; set; } = TransitionKind.Deslizar;
    public double AnimationSpeed { get; set; } = 1.0;

    // Módulos
    public DiskScanMode DiskScanMode { get; set; } = DiskScanMode.Automatico;
    public bool CreateRestorePoint { get; set; } = true;
    public decimal EnergyPricePerKwh { get; set; } = 0.85m;
    public double DailyGamingHours { get; set; } = 3;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

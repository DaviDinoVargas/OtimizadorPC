namespace OtimizadorPC.Core.Optimization;

/// <summary>
/// Catálogo de serviços e apps que costumam pesar sem necessidade. Só os que existem no
/// computador aparecem na tela, e nada é alterado sem o usuário pedir.
/// </summary>
public static class OptimizationCatalog
{
    public static IReadOnlyList<KnownService> Services { get; } =
    [
        // Bancos de dados e desenvolvimento
        new("MSSQLSERVER", "Banco de dados", "SQL Server. Necessário só quando você trabalha com bancos de dados SQL Server.", ServiceStartMode.Manual),
        new("SQLSERVERAGENT", "Banco de dados", "Agente do SQL Server (tarefas agendadas do banco).", ServiceStartMode.Manual),
        new("SQLWriter", "Banco de dados", "Gravador VSS do SQL Server, usado em backups do banco.", ServiceStartMode.Manual),
        new("SQLBrowser", "Banco de dados", "Localizador de instâncias do SQL Server na rede.", ServiceStartMode.Manual),
        new("SQLTELEMETRY", "Banco de dados", "Envio de dados de uso do SQL Server para a Microsoft.", ServiceStartMode.Desativado),
        new("MySQL80", "Banco de dados", "Servidor MySQL.", ServiceStartMode.Manual),
        new("postgresql-x64-16", "Banco de dados", "Servidor PostgreSQL.", ServiceStartMode.Manual),

        // Fabricantes (RGB, utilitários)
        new("ArmouryCrateService", "Utilitários do fabricante", "Armoury Crate da ASUS. O app volta a funcionar quando aberto.", ServiceStartMode.Manual),
        new("asComSvc", "Utilitários do fabricante", "Serviço de comunicação da ASUS.", ServiceStartMode.Manual),
        new("AsusCertService", "Utilitários do fabricante", "Certificados dos apps da ASUS.", ServiceStartMode.Manual),
        new("LightingService", "Utilitários do fabricante", "Iluminação RGB ASUS Aura. Sem ele, o RGB segue o padrão da placa.", ServiceStartMode.Manual),
        new("ROG Live Service", "Utilitários do fabricante", "Atualizações e notícias da ROG.", ServiceStartMode.Manual),
        new("AsusFanControlService", "Utilitários do fabricante", "Controle de ventoinhas pelo Windows. Sem ele, valem as curvas da BIOS.", ServiceStartMode.Manual, true),
        new("GameSDK Service", "Utilitários do fabricante", "SDK de jogos da ASUS.", ServiceStartMode.Manual),
        new("LGHUBUpdaterService", "Utilitários do fabricante", "Atualizador do Logitech G HUB.", ServiceStartMode.Manual),
        new("CorsairService", "Utilitários do fabricante", "Serviço do Corsair iCUE.", ServiceStartMode.Manual, true),
        new("RzActionSvc", "Utilitários do fabricante", "Serviço do Razer Synapse.", ServiceStartMode.Manual, true),

        // Atualizadores
        new("gupdate", "Atualizadores", "Atualizador do Google (Chrome). O Chrome continua se atualizando ao ser aberto.", ServiceStartMode.Manual),
        new("gupdatem", "Atualizadores", "Atualizador do Google (verificação manual).", ServiceStartMode.Manual),
        new("edgeupdate", "Atualizadores", "Atualizador do Microsoft Edge.", ServiceStartMode.Manual),
        new("edgeupdatem", "Atualizadores", "Atualizador do Microsoft Edge (verificação manual).", ServiceStartMode.Manual),
        new("AdobeARMservice", "Atualizadores", "Atualizador do Adobe Acrobat Reader.", ServiceStartMode.Manual),
        new("AGSService", "Atualizadores", "Verificação de licenças da Adobe.", ServiceStartMode.Manual),
        new("BraveUpdateService", "Atualizadores", "Atualizador do navegador Brave.", ServiceStartMode.Manual),

        // Rede, VPN e emuladores
        new("CloudflareWARP", "Rede e VPN", "Cloudflare WARP. Com outra VPN instalada, não precisa iniciar sozinho.", ServiceStartMode.Manual),
        new("BstHdAndroidSvc", "Emuladores", "Serviço do BlueStacks.", ServiceStartMode.Manual),
        new("BlueStacksDrv", "Emuladores", "Driver do BlueStacks.", ServiceStartMode.Manual, true),

        // Windows
        new("DiagTrack", "Windows", "Telemetria do Windows (Experiências e Telemetria do Usuário Conectado).", ServiceStartMode.Desativado),
        new("dmwappushservice", "Windows", "Roteamento de mensagens push usado pela telemetria.", ServiceStartMode.Manual),
        new("MapsBroker", "Windows", "Gerenciador de mapas baixados. Só é útil com o app Mapas.", ServiceStartMode.Manual),
        new("RetailDemo", "Windows", "Modo de demonstração de lojas.", ServiceStartMode.Desativado),
        new("Fax", "Windows", "Envio e recebimento de fax.", ServiceStartMode.Manual),
        new("XblGameSave", "Windows", "Salvamento na nuvem de jogos do Xbox. Mantenha se joga pelo Game Pass.", ServiceStartMode.Manual, true),
        new("XboxNetApiSvc", "Windows", "Rede do Xbox Live. Mantenha se joga pelo Game Pass.", ServiceStartMode.Manual, true),
        new("WerSvc", "Windows", "Relatório de erros do Windows.", ServiceStartMode.Manual),
        new("Spooler", "Windows", "Impressão. Desative só se não usa impressora nem \"Imprimir para PDF\".", ServiceStartMode.Automatico, true),
        new("Warsaw Technology", "Segurança de bancos", "Módulo de segurança de bancos (Warsaw). Ele se protege contra alterações; se quiser removê-lo, desinstale pelo Windows.", ServiceStartMode.Manual, true),
    ];

    public static IReadOnlyList<KnownApp> Apps { get; } =
    [
        new("MicrosoftWindows.Client.WebExperience", "Widgets", "Painel de notícias e clima. Roda em segundo plano mesmo escondido."),
        new("Microsoft.StartExperiencesApp", "Feed do menu Iniciar", "Conteúdo de notícias exibido no Iniciar."),
        new("Microsoft.YourPhone", "Vincular ao Celular", "Integração com o celular. Pode ser reinstalado pela Microsoft Store."),
        new("MicrosoftWindows.CrossDevice", "Experiência entre Dispositivos", "Recursos de celular conectado ao Windows."),
        new("microsoft.windowscommunicationsapps", "Email e Calendário (antigo)", "App antigo substituído pelo novo Outlook."),
        new("Microsoft.Windows.DevHome", "Dev Home", "App descontinuado pela Microsoft."),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "Jogos de cartas com anúncios."),
        new("Microsoft.BingNews", "Notícias", "App de notícias do Bing."),
        new("Microsoft.BingWeather", "Clima", "App de previsão do tempo."),
        new("Microsoft.GetHelp", "Obter Ajuda", "Central de suporte da Microsoft."),
        new("Microsoft.Getstarted", "Dicas", "Dicas de uso do Windows."),
        new("Clipchamp.Clipchamp", "Clipchamp", "Editor de vídeo online da Microsoft."),
        new("Microsoft.People", "Pessoas", "Contatos (descontinuado)."),
        new("Microsoft.ZuneVideo", "Filmes e TV", "Player de vídeos antigo."),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office)", "Atalho para a loja do Office."),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", "Automação de tarefas."),
        new("Microsoft.Todos", "Microsoft To Do", "Lista de tarefas."),
        new("Microsoft.WindowsFeedbackHub", "Hub de Comentários", "Envio de feedback para a Microsoft."),
        new("Microsoft.WindowsMaps", "Mapas", "Mapas offline."),
        new("Microsoft.549981C3F5F10", "Cortana", "Assistente descontinuada."),
        new("Microsoft.MixedReality.Portal", "Portal de Realidade Misturada", "Só é útil com óculos de realidade virtual Windows MR."),
        new("Microsoft.Microsoft3DViewer", "Visualizador 3D", "Visualizador de modelos 3D."),
        new("Microsoft.MSPaint", "Paint 3D", "Editor 3D descontinuado (o Paint comum continua)."),
        new("Microsoft.SkypeApp", "Skype", "Serviço encerrado pela Microsoft."),
        new("MicrosoftTeams", "Teams (pessoal)", "Versão pessoal do Teams.", true),
        new("Microsoft.OutlookForWindows", "Outlook (novo)", "Novo cliente de email do Windows.", true),
        new("Microsoft.XboxApp", "Xbox Console Companion", "App antigo do Xbox.", true),
    ];
}

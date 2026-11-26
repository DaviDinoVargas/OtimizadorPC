namespace OtimizadorPC.App.Navigation;

/// <summary>Pedido de navegação enviado por qualquer tela (ex.: "abrir Limpeza").</summary>
public sealed record NavigateMessage(string Key);

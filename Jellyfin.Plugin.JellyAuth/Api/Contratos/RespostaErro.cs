namespace Jellyfin.Plugin.JellyAuth.Api.Contratos;

/// <summary>Resposta de erro simples (JSON) usada em todos os endpoints públicos.</summary>
public sealed record RespostaErro(string Mensagem);

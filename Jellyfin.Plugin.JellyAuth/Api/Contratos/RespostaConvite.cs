namespace Jellyfin.Plugin.JellyAuth.Api.Contratos;

/// <summary>
/// Um convite na lista do painel (sem o código: só os primeiros caracteres). <c>Situacao</c> é o texto para exibir;
/// decisões (revogar ou excluir) usam <c>Ativo</c>.
/// </summary>
public sealed record RespostaConvite(
    Guid Id,
    string Prefixo,
    string Observacao,
    DateTime CriadoEm,
    DateTime? ExpiraEm,
    int UsosMaximos,
    int Usos,
    string Situacao,
    bool Ativo,
    IReadOnlyList<string> Usuarios);

/// <summary>Convite recém-criado: a única resposta que traz o código completo.</summary>
public sealed record RespostaConviteCriado(Guid Id, string Codigo);

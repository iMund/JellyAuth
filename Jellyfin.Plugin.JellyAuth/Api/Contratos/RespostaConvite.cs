namespace Jellyfin.Plugin.JellyAuth.Api.Contratos;

/// <summary>Um convite na lista do painel (sem o código: só os primeiros caracteres).</summary>
public sealed record RespostaConvite(
    Guid Id,
    string Prefixo,
    string Observacao,
    DateTime CriadoEm,
    DateTime? ExpiraEm,
    int UsosMaximos,
    int Usos,
    string Situacao,
    IReadOnlyList<string> Usuarios);

/// <summary>Convite recém-criado: a única resposta que traz o código completo.</summary>
public sealed record RespostaConviteCriado(Guid Id, string Codigo);

namespace Jellyfin.Plugin.JellyAuth.Dados;

/// <summary>
/// Convite para o cadastro (quando o admin exige convite). O código em si nunca é guardado: só o hash SHA-256 e os
/// primeiros caracteres, para o admin reconhecer qual é qual.
/// </summary>
public sealed class Convite
{
    public Guid Id { get; init; }

    /// <summary>SHA-256 do código normalizado (maiúsculas, sem hífens nem espaços), em hexadecimal.</summary>
    public string CodigoHash { get; init; } = string.Empty;

    /// <summary>Primeiros caracteres do código, para identificação no painel.</summary>
    public string Prefixo { get; init; } = string.Empty;

    /// <summary>Anotação do admin (ex.: para quem é o convite).</summary>
    public string Observacao { get; init; } = string.Empty;

    public DateTime CriadoEm { get; init; }

    /// <summary>Depois disto o convite não vale mais; <c>null</c> = sem prazo.</summary>
    public DateTime? ExpiraEm { get; init; }

    public int UsosMaximos { get; init; } = 1;

    public int Usos { get; set; }

    public bool Revogado { get; set; }

    /// <summary>Nomes de usuário criados com este convite.</summary>
    public List<string> Usuarios { get; set; } = [];
}

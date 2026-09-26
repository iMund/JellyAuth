namespace Jellyfin.Plugin.JellyAuth.Dados;

/// <summary>
/// Código de verificação temporário e os dados do cadastro pendente, guardados apenas em memória
/// até o usuário confirmar (ou o prazo expirar). Veja ADR-002 para a decisão de não persistir em disco.
/// </summary>
public sealed class CodigoVerificacao
{
    public required string Email { get; init; }

    public required string Username { get; init; }

    public required string Password { get; init; }

    /// <summary>Convite informado no pedido (quando o admin exige convite); gasto só quando a conta é criada.</summary>
    public string? Convite { get; init; }

    public required string Codigo { get; set; }

    public DateTime ExpiraEm { get; set; }

    /// <summary>
    /// Com convite: depois disto o pendente não vale mais, nem com reenvio nem pedindo de novo com o mesmo e-mail (o uso
    /// reservado não fica preso). Sem convite: <see cref="DateTime.MaxValue"/>.
    /// </summary>
    public DateTime LimiteAte { get; init; }

    /// <summary>Chave do limite de reserva que este pedido começou a contar (para esquecê-lo se o e-mail não sair).</summary>
    public string? ChaveLimiteNova { get; init; }

    /// <summary>Código confirmado e conta sendo criada: o uso do convite segue reservado. Só muda sob a trava dos pendentes.</summary>
    public bool Confirmando { get; set; }

    /// <summary>Quantas tentativas de verificação (código errado) já aconteceram. Acesso atômico via Interlocked.</summary>
    public int TentativasVerificacao;

    /// <summary>Momento do último reenvio, para impor o cooldown.</summary>
    public DateTime UltimoReenvioEm { get; set; }
}

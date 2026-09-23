using System.Text.Json.Serialization;

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

    public required string Codigo { get; set; }

    public DateTime CriadoEm { get; set; }

    public DateTime ExpiraEm { get; set; }

    /// <summary>Quantas tentativas de verificação (código errado) já aconteceram. Acesso atômico via Interlocked.</summary>
    public int TentativasVerificacao;

    /// <summary>Momento do último reenvio, para impor o cooldown.</summary>
    [JsonIgnore]
    public DateTime UltimoReenvioEm { get; set; }
}

namespace Jellyfin.Plugin.JellyAuth.Api.Contratos;

/// <summary>Corpo de <c>POST /JellyAuth/Admin/Convites</c>.</summary>
public sealed class PedidoConvite
{
    /// <summary>Quantas contas o convite pode criar (1 = uso único).</summary>
    public int UsosMaximos { get; set; } = 1;

    /// <summary>Dias de validade; vazio ou 0 = sem prazo.</summary>
    public int? DiasValidade { get; set; }

    /// <summary>Anotação do admin (ex.: para quem é).</summary>
    public string? Observacao { get; set; }
}

namespace Jellyfin.Plugin.JellyAuth.Api.Contratos;

/// <summary>Corpo de <c>POST /JellyAuth/Verify</c> e de <c>POST /JellyAuth/Resend</c>.</summary>
public sealed class PedidoVerificacao
{
    public string? Email { get; set; }

    public string? Code { get; set; }
}

namespace Jellyfin.Plugin.JellyAuth.Api.Contratos;

/// <summary>Corpo de <c>POST /JellyAuth/Request</c>.</summary>
public sealed class PedidoRegistro
{
    public string? Username { get; set; }

    public string? Email { get; set; }

    public string? Password { get; set; }
}

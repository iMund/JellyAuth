namespace Jellyfin.Plugin.JellyAuth.Api.Contratos;

/// <summary>Corpo de <c>POST /JellyAuth/Admin/TestarEmail</c>.</summary>
public sealed class PedidoTesteEmail
{
    public string? Email { get; set; }
}

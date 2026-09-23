namespace Jellyfin.Plugin.JellyAuth.Api.Contratos;

/// <summary>Corpo de <c>POST /JellyAuth/Admin/SalvarSenhaSmtp</c>.</summary>
public sealed class PedidoSenha
{
    public string? Senha { get; set; }
}

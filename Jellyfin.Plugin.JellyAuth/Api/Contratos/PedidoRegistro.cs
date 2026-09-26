namespace Jellyfin.Plugin.JellyAuth.Api.Contratos;

/// <summary>Corpo de <c>POST /JellyAuth/Request</c>.</summary>
public sealed class PedidoRegistro
{
    public string? Username { get; set; }

    public string? Email { get; set; }

    public string? Password { get; set; }

    /// <summary>Código do convite (quando o admin exige convite).</summary>
    public string? Convite { get; set; }

    /// <summary>Token do captcha (quando o captcha está ligado).</summary>
    public string? CaptchaToken { get; set; }
}

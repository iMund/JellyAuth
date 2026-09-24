namespace Jellyfin.Plugin.JellyAuth.Configuracao;

/// <summary>Provedor de captcha usado no cadastro. <see cref="Nenhum"/> desliga.</summary>
public enum TipoCaptcha
{
    /// <summary>Sem captcha.</summary>
    Nenhum,

    /// <summary>Google reCAPTCHA v2 (caixa de seleção).</summary>
    Recaptcha,

    /// <summary>hCaptcha.</summary>
    HCaptcha,

    /// <summary>Cloudflare Turnstile.</summary>
    CloudflareTurnstile,
}

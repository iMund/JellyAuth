namespace Jellyfin.Plugin.JellyAuth.Seguranca;

/// <summary>Ajudantes para evitar gravar dados pessoais (PII) nos logs.</summary>
public static class TextoParaLog
{
    /// <summary>Mascara o e-mail para log: mantém os dois primeiros caracteres e o domínio.</summary>
    public static string MascararEmail(string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return string.Empty;
        }

        var arroba = email.IndexOf('@');
        if (arroba <= 1)
        {
            return "***";
        }

        return string.Concat(email.AsSpan(0, 2), "***", email.AsSpan(arroba));
    }
}

using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyAuth.Seguranca;

/// <summary>Ajudantes para evitar gravar dados pessoais (PII) ou conteúdo forjado nos logs.</summary>
public static class TextoParaLog
{
    private const int TamanhoMaximo = 300;

    // Detecta endereços de e-mail dentro de um texto livre (ex.: mensagem de erro do SMTP).
    private static readonly Regex EmailRegex = new(@"[^\s@]+@[^\s@]+\.[^\s@]+", RegexOptions.Compiled);

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

    /// <summary>
    /// Sanitiza texto livre (em geral <c>Exception.Message</c>) para log: remove caracteres de controle
    /// (quebra de linha / log injection), mascara e-mails (PII) e limita o tamanho.
    /// </summary>
    public static string Limpar(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return string.Empty;
        }

        // Limita antes do regex (entrada pequena e previsível).
        var limitado = texto.Length <= TamanhoMaximo ? texto : texto[..TamanhoMaximo];
        var semControle = new string(limitado.Where(c => !char.IsControl(c)).ToArray());
        return EmailRegex.Replace(semControle, match => MascararEmail(match.Value));
    }
}

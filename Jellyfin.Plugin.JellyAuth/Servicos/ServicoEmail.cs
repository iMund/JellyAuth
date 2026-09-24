using System.Net;
using System.Net.Mail;
using Jellyfin.Plugin.JellyAuth.Configuracao;
using Jellyfin.Plugin.JellyAuth.Dados;
using Jellyfin.Plugin.JellyAuth.Seguranca;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Servicos;

/// <summary>
/// Envia o código de verificação por SMTP. Usa <see cref="SmtpClient"/> (do próprio .NET), sem
/// dependência externa, mantendo o plugin como uma única DLL. A senha vem do
/// <see cref="ArmazenamentoSegredos"/> (arquivo separado, nunca da configuração).
/// </summary>
public class ServicoEmail
{
    private readonly Func<ConfiguracaoPlugin> _configuracao;
    private readonly ArmazenamentoSegredos _segredos;
    private readonly ILogger<ServicoEmail> _logger;

    public ServicoEmail(Func<ConfiguracaoPlugin> configuracao, ArmazenamentoSegredos segredos, ILogger<ServicoEmail> logger)
    {
        _configuracao = configuracao;
        _segredos = segredos;
        _logger = logger;
    }

    /// <summary>Valida se o SMTP está minimamente configurado para enviar e-mails.</summary>
    public bool EstaConfigurado()
    {
        var config = _configuracao();
        return !string.IsNullOrWhiteSpace(config.SmtpHost)
            && MailAddress.TryCreate(config.RemetenteEmail, out _);
    }

    /// <summary>Envia o código para o e-mail informado.</summary>
    public async Task EnviarCodigoAsync(string destino, string codigo, CancellationToken cancelamento)
    {
        var config = _configuracao();
        using var cliente = new SmtpClient(config.SmtpHost, config.SmtpPort)
        {
            EnableSsl = config.SmtpSsl,
            Timeout = 15_000,
        };

        if (!string.IsNullOrWhiteSpace(config.SmtpUsuario))
        {
            cliente.Credentials = new NetworkCredential(config.SmtpUsuario, _segredos.ObterSenhaSmtp());
        }

        using var mensagem = new MailMessage
        {
            From = new MailAddress(config.RemetenteEmail, config.RemetenteNome),
            Subject = config.AssuntoEmail,
            Body = MontarCorpo(config, codigo),
            IsBodyHtml = true,
        };
        mensagem.To.Add(destino);

        await cliente.SendMailAsync(mensagem, cancelamento).ConfigureAwait(false);
        _logger.LogInformation("Código de verificação enviado para {Email} via {Host}.", TextoParaLog.MascararEmail(destino), config.SmtpHost);
    }

    private static string MontarCorpo(ConfiguracaoPlugin config, string codigo)
    {
        var nomeServidor = WebUtility.HtmlEncode(config.RemetenteNome);
        var codigoSeguro = WebUtility.HtmlEncode(codigo);
        return $"""
            <div style="font-family:system-ui,sans-serif;max-width:480px;margin:auto;padding:24px;color:#20262c">
              <h2 style="margin:0 0 12px">{nomeServidor}</h2>
              <p>Use o código abaixo para concluir seu cadastro:</p>
              <p style="font-size:32px;font-weight:700;letter-spacing:8px;text-align:center;margin:24px 0">{codigoSeguro}</p>
              <p style="color:#7a8288;font-size:13px">O código expira em {config.MinutosExpiracaoCodigo} minutos.</p>
            </div>
            """;
    }
}

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
    public virtual bool EstaConfigurado()
    {
        var config = _configuracao();
        return !string.IsNullOrWhiteSpace(config.SmtpHost)
            && MailAddress.TryCreate(config.RemetenteEmail, out _);
    }

    /// <summary>
    /// Porta 465 (SSL implícito): o <see cref="SmtpClient"/> só faz STARTTLS, então a conexão espera um TLS que nunca
    /// começa e cai no timeout. Usada para dar a mensagem certa em vez de "não foi possível enviar".
    /// </summary>
    public bool PortaSemSuporte() => _configuracao().SmtpPort == 465;

    /// <summary>Envia o código para o e-mail informado.</summary>
    /// <param name="minutosValidade">Prazo real do código, quando é menor que o configurado (pedido perto do limite da reserva do convite).</param>
    public virtual async Task EnviarCodigoAsync(string destino, string codigo, CancellationToken cancelamento, int? minutosValidade = null)
    {
        var config = _configuracao();
        await EnviarAsync(config, destino, config.AssuntoEmail, MontarCorpo(config, codigo, minutosValidade ?? config.MinutosExpiracaoCodigo), cancelamento).ConfigureAwait(false);
        _logger.LogInformation("Código de verificação enviado para {Email} via {Host}.", TextoParaLog.MascararEmail(destino), config.SmtpHost);
    }

    /// <summary>
    /// Avisa o dono do e-mail que já existe uma conta com ele (alguém pediu um cadastro novo com este endereço). Sai no
    /// lugar do código, para a resposta do cadastro não revelar a terceiros que o e-mail tem conta.
    /// </summary>
    public virtual async Task EnviarAvisoContaExistenteAsync(string destino, CancellationToken cancelamento)
    {
        var config = _configuracao();
        var nomeServidor = WebUtility.HtmlEncode(config.RemetenteNome);
        var corpo = $"""
            <div style="font-family:system-ui,sans-serif;max-width:480px;margin:auto;padding:24px;color:#20262c">
              <h2 style="margin:0 0 12px">{nomeServidor}</h2>
              <p>Alguém pediu um cadastro novo com este e-mail, mas ele já tem uma conta neste servidor.</p>
              <p>Se foi você, entre com o seu nome de usuário e senha. Se esqueceu a senha, fale com o administrador do servidor.</p>
              <p style="color:#7a8288;font-size:13px">Se não foi você, pode ignorar esta mensagem.</p>
            </div>
            """;
        await EnviarAsync(config, destino, AssuntoAviso(config), corpo, cancelamento).ConfigureAwait(false);
        _logger.LogInformation("Aviso de conta existente enviado para {Email} via {Host}.", TextoParaLog.MascararEmail(destino), config.SmtpHost);
    }

    /// <summary>Assunto do aviso de conta existente: o do painel (ou o padrão), com {servidor} trocado pelo nome exibido.</summary>
    internal static string AssuntoAviso(ConfiguracaoPlugin config)
    {
        var assunto = string.IsNullOrWhiteSpace(config.AssuntoAvisoContaExistente) ? "{servidor}: você já tem uma conta" : config.AssuntoAvisoContaExistente;
        return assunto.Replace("{servidor}", config.RemetenteNome, StringComparison.Ordinal);
    }

    private async Task EnviarAsync(ConfiguracaoPlugin config, string destino, string assunto, string corpoHtml, CancellationToken cancelamento)
    {
        if (config.SmtpPort == 465)
        {
            _logger.LogWarning("JellyAuth: SMTP na porta 465 (SSL implícito), que o .NET não suporta. Use a 587 com STARTTLS.");
            throw new InvalidOperationException("Porta SMTP 465 não suportada.");
        }

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
            Subject = assunto,
            Body = corpoHtml,
            IsBodyHtml = true,
        };
        mensagem.To.Add(destino);

        await cliente.SendMailAsync(mensagem, cancelamento).ConfigureAwait(false);
    }

    private static string MontarCorpo(ConfiguracaoPlugin config, string codigo, int minutosValidade)
    {
        var nomeServidor = WebUtility.HtmlEncode(config.RemetenteNome);
        var codigoSeguro = WebUtility.HtmlEncode(codigo);
        return $"""
            <div style="font-family:system-ui,sans-serif;max-width:480px;margin:auto;padding:24px;color:#20262c">
              <h2 style="margin:0 0 12px">{nomeServidor}</h2>
              <p>Use o código abaixo para concluir seu cadastro:</p>
              <p style="font-size:32px;font-weight:700;letter-spacing:8px;text-align:center;margin:24px 0">{codigoSeguro}</p>
              <p style="color:#7a8288;font-size:13px">O código expira em {(minutosValidade == 1 ? "1 minuto" : $"{minutosValidade} minutos")}.</p>
            </div>
            """;
    }
}

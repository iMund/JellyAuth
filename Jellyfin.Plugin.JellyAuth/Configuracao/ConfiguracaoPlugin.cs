using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyAuth.Configuracao;

/// <summary>
/// Configuração editada pelo admin no painel do Jellyfin (Painel → Plugins → JellyAuth).
/// Os nomes das propriedades são os mesmos IDs dos campos em painel.html, e as seções abaixo
/// são as abas do painel.
/// </summary>
public class ConfiguracaoPlugin : BasePluginConfiguration
{
    private const int MaximoMinutosExpiracao = 1440; // 24 horas
    private const int MaximoSegundosReenvio = 3600; // 1 hora
    private const int MaximoTentativas = 100;
    private const int MaximoTentativasPorIpLimite = 10_000;
    private const int MaximoMinutosJanela = 1440;

    private int _minutosExpiracaoCodigo = 15;
    private int _minimoSegundosReenvio = 60;
    private int _maximoTentativasPorEmail = 5;
    private int _maximoTentativasPorIp = 30;
    private int _janelaTentativasMinutos = 15;
    private int _smtpPort = 587;

    // --- Aba "Geral" ---

    /// <summary>Chave-mestra: com isto desligado nenhum cadastro novo é aceito.</summary>
    public bool HabilitarCadastro { get; set; } = true;

    /// <summary>
    /// Exige o código enviado por e-mail antes de criar a conta. Desligado, o usuário é criado
    /// na hora, sem verificação (o SMTP não é usado).
    /// </summary>
    public bool ExigirVerificacaoEmail { get; set; } = true;

    /// <summary>
    /// Só aceita cadastro com um convite gerado pelo admin (aba Convites). Desligado, qualquer pessoa com acesso à tela
    /// de login pode se cadastrar.
    /// </summary>
    public bool ExigirConvite { get; set; }

    // --- Aba "SMTP" ---

    /// <summary>Host do servidor de e-mail (ex.: smtp.gmail.com).</summary>
    public string SmtpHost { get; set; } = string.Empty;

    /// <summary>Porta do servidor SMTP (587, com STARTTLS). A 465 (SSL implícito) não funciona: o SmtpClient do .NET só faz STARTTLS.</summary>
    public int SmtpPort { get => _smtpPort; set => _smtpPort = Math.Clamp(value, 1, 65535); }

    /// <summary>Usuário de autenticação SMTP (vazio = sem autenticação).</summary>
    public string SmtpUsuario { get; set; } = string.Empty;

    /// <summary>Liga SSL/TLS na conexão SMTP.</summary>
    public bool SmtpSsl { get; set; } = true;

    /// <summary>Endereço do remetente dos e-mails de verificação.</summary>
    public string RemetenteEmail { get; set; } = string.Empty;

    /// <summary>Nome exibido do remetente.</summary>
    public string RemetenteNome { get; set; } = "Jellyfin";

    /// <summary>Assunto do e-mail com o código de verificação.</summary>
    public string AssuntoEmail { get; set; } = "Seu código de verificação";

    // --- Aba "Regras de Usuário" ---

    /// <summary>Permite que os novos usuários façam download de mídia (desligado por padrão).</summary>
    public bool PermitirDownload { get; set; }

    /// <summary>
    /// IDs das bibliotecas visíveis aos novos usuários. Vazio = nenhuma (o usuário começa sem acesso
    /// a bibliotecas). Quando preenchido, libera apenas as marcadas.
    /// </summary>
    public string[] IdsBibliotecasPermitidas { get; set; } = [];

    // --- Aba "Segurança" ---

    /// <summary>Exige senha com letras e números (além do mínimo de 8 caracteres).</summary>
    public bool ExigirSenhaForte { get; set; } = true;

    /// <summary>
    /// Atrás de proxy reverso ou Cloudflare Tunnel: usa o CF-Connecting-IP/X-Forwarded-For para o rate limit por IP, mas só
    /// quando a conexão vem de endereço local (o proxy); quem conecta direto pela internet não escolhe o IP pelo cabeçalho.
    /// </summary>
    public bool ConfiarProxy { get; set; }

    /// <summary>Validade do código de verificação, em minutos.</summary>
    public int MinutosExpiracaoCodigo { get => _minutosExpiracaoCodigo; set => _minutosExpiracaoCodigo = Math.Clamp(value, 1, MaximoMinutosExpiracao); }

    /// <summary>Tempo mínimo entre reenvios de código para o mesmo e-mail, em segundos.</summary>
    public int MinimoSegundosReenvio { get => _minimoSegundosReenvio; set => _minimoSegundosReenvio = Math.Clamp(value, 0, MaximoSegundosReenvio); }

    /// <summary>Máximo de solicitações por e-mail dentro da janela de tempo (rate limit).</summary>
    public int MaximoTentativasPorEmail { get => _maximoTentativasPorEmail; set => _maximoTentativasPorEmail = Math.Clamp(value, 1, MaximoTentativas); }

    /// <summary>Máximo de solicitações por endereço IP dentro da janela de tempo (rate limit).</summary>
    public int MaximoTentativasPorIp { get => _maximoTentativasPorIp; set => _maximoTentativasPorIp = Math.Clamp(value, 1, MaximoTentativasPorIpLimite); }

    /// <summary>Janela do rate limit, em minutos.</summary>
    public int JanelaTentativasMinutos { get => _janelaTentativasMinutos; set => _janelaTentativasMinutos = Math.Clamp(value, 1, MaximoMinutosJanela); }

    // --- Aba "Captcha" ---

    /// <summary>Provedor de captcha do formulário de cadastro (<c>Nenhum</c> desliga).</summary>
    public TipoCaptcha ProvedorCaptcha { get; set; } = TipoCaptcha.Nenhum;

    /// <summary>Site key (pública) do captcha, exibida ao navegador.</summary>
    public string CaptchaSiteKey { get; set; } = string.Empty;
}

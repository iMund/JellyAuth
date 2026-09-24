using Jellyfin.Plugin.JellyAuth.Seguranca;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Dados;

/// <summary>
/// Guarda segredos (senha SMTP, secret do captcha) em arquivos próprios, fora da configuração XML do
/// plugin — assim não aparecem na API de configuração (que devolve o objeto inteiro ao navegador do
/// admin) e ficam com permissão de leitura restrita ao dono do processo (0600 no Unix).
/// </summary>
public class ArmazenamentoSegredos
{
    private const string ArquivoSenhaSmtp = "JellyAuth.smtp-senha.txt";
    private const string ArquivoSegredoCaptcha = "JellyAuth.captcha-secret.txt";

    private readonly Lock _trava = new();
    private readonly string _pasta;
    private readonly ILogger<ArmazenamentoSegredos> _logger;

    public ArmazenamentoSegredos(IApplicationPaths caminhos, ILogger<ArmazenamentoSegredos> logger)
    {
        _pasta = caminhos.PluginConfigurationsPath;
        _logger = logger;
    }

    /// <summary>Lê a senha SMTP armazenada (vazio se não existir).</summary>
    public string ObterSenhaSmtp() => Obter(ArquivoSenhaSmtp);

    /// <summary>Grava a senha SMTP.</summary>
    public void SalvarSenhaSmtp(string senha) => Salvar(ArquivoSenhaSmtp, senha);

    /// <summary>Lê o secret do captcha (vazio se não existir).</summary>
    public string ObterSegredoCaptcha() => Obter(ArquivoSegredoCaptcha);

    /// <summary>Grava o secret do captcha.</summary>
    public void SalvarSegredoCaptcha(string segredo) => Salvar(ArquivoSegredoCaptcha, segredo);

    private string Caminho(string nomeArquivo) => Path.Combine(_pasta, nomeArquivo);

    private string Obter(string nomeArquivo)
    {
        lock (_trava)
        {
            try
            {
                var caminho = Caminho(nomeArquivo);
                return File.Exists(caminho) ? File.ReadAllText(caminho).Trim() : string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Não foi possível ler o segredo {Arquivo} do JellyAuth: {Mensagem}", nomeArquivo, TextoParaLog.Limpar(ex.Message));
                return string.Empty;
            }
        }
    }

    private void Salvar(string nomeArquivo, string valor)
    {
        lock (_trava)
        {
            try
            {
                Directory.CreateDirectory(_pasta);
                var caminho = Caminho(nomeArquivo);
                var temporario = caminho + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporario, valor);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporario, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                File.Move(temporario, caminho, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Não foi possível salvar o segredo {Arquivo} do JellyAuth: {Mensagem}", nomeArquivo, TextoParaLog.Limpar(ex.Message));
                throw;
            }
        }
    }
}

using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Dados;

/// <summary>
/// Guarda a senha SMTP em um arquivo próprio, fora da configuração XML do plugin — assim ela não
/// aparece na API de configuração (que devolve o objeto inteiro ao navegador do admin) e fica com
/// permissão de leitura restrita ao dono do processo (0600 no Unix).
/// </summary>
public class ArmazenamentoSegredos
{
    private const string NomeArquivo = "JellyAuth.smtp-senha.txt";

    private readonly string _caminhoArquivo;
    private readonly ILogger<ArmazenamentoSegredos> _logger;

    public ArmazenamentoSegredos(IApplicationPaths caminhos, ILogger<ArmazenamentoSegredos> logger)
    {
        _caminhoArquivo = Path.Combine(caminhos.PluginConfigurationsPath, NomeArquivo);
        _logger = logger;
    }

    /// <summary>Lê a senha SMTP armazenada (vazio se não existir).</summary>
    public string ObterSenhaSmtp()
    {
        try
        {
            return File.Exists(_caminhoArquivo) ? File.ReadAllText(_caminhoArquivo).Trim() : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Não foi possível ler a senha SMTP do JellyAuth: {Mensagem}", ex.Message);
            return string.Empty;
        }
    }

    /// <summary>Grava a senha SMTP com permissão restrita e de forma atômica.</summary>
    public void SalvarSenhaSmtp(string senha)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_caminhoArquivo)!);
            var temporario = _caminhoArquivo + ".tmp";
            File.WriteAllText(temporario, senha);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporario, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporario, _caminhoArquivo, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Não foi possível salvar a senha SMTP do JellyAuth: {Mensagem}", ex.Message);
            throw;
        }
    }
}

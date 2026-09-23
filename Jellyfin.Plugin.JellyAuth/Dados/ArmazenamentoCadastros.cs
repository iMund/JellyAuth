using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Dados;

/// <summary>
/// Persistência em JSON dos cadastros concluídos (e-mail → usuário). O e-mail não existe no usuário
/// nativo do Jellyfin, então o plugin mantém o próprio mapeamento para impedir contas duplicadas.
/// Gravação atômica, na mesma linha do padrão de dados do JellyPix.
/// </summary>
public class ArmazenamentoCadastros
{
    private const string NomeArquivo = "JellyAuth.cadastros.json";
    private static readonly JsonSerializerOptions OpcoesJson = new() { WriteIndented = true };

    private readonly SemaphoreSlim _trava = new(1, 1);
    private readonly string _caminhoArquivo;
    private readonly ILogger<ArmazenamentoCadastros> _logger;
    private List<CadastroConcluido>? _cadastros;

    public ArmazenamentoCadastros(IApplicationPaths caminhos, ILogger<ArmazenamentoCadastros> logger)
        : this(Path.Combine(caminhos.PluginConfigurationsPath, NomeArquivo), logger)
    {
        logger.LogInformation("JellyAuth: cadastros em {Caminho}", _caminhoArquivo);
    }

    internal ArmazenamentoCadastros(string caminhoArquivo, ILogger<ArmazenamentoCadastros> logger)
    {
        _caminhoArquivo = caminhoArquivo;
        _logger = logger;
    }

    public bool EmailJaCadastrado(string email)
    {
        return Carregar().Any(c => string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Registra o cadastro de forma atômica: se o e-mail já existir, retorna <c>false</c> e não altera nada.
    /// É a conferência autoritativa contra corrida (o <see cref="EmailJaCadastrado"/> é só uma checagem rápida).
    /// </summary>
    public async Task<bool> RegistrarSeNovoAsync(string email, string username, Guid idUsuario, CancellationToken cancelamento)
    {
        await _trava.WaitAsync(cancelamento).ConfigureAwait(false);
        try
        {
            var cadastros = Carregar();
            if (cadastros.Any(c => string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            cadastros.Add(new CadastroConcluido
            {
                Email = email,
                Username = username,
                IdUsuario = idUsuario,
                DataCadastro = DateTime.UtcNow,
            });
            await SalvarAsync(cadastros, cancelamento).ConfigureAwait(false);
            _cadastros = cadastros;
            return true;
        }
        finally
        {
            _trava.Release();
        }
    }

    private List<CadastroConcluido> Carregar()
    {
        if (_cadastros is not null)
        {
            return _cadastros;
        }

        if (!File.Exists(_caminhoArquivo))
        {
            return _cadastros = [];
        }

        try
        {
            return _cadastros = JsonSerializer.Deserialize<List<CadastroConcluido>>(File.ReadAllText(_caminhoArquivo), OpcoesJson) ?? [];
        }
        catch (JsonException ex)
        {
            var copiaSeguranca = _caminhoArquivo + ".corrompido-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Copy(_caminhoArquivo, copiaSeguranca, overwrite: true);
            _logger.LogError(ex, "Arquivo de cadastros do JellyAuth corrompido. Cópia salva em {Copia}", copiaSeguranca);
            return _cadastros = [];
        }
    }

    private async Task SalvarAsync(List<CadastroConcluido> cadastros, CancellationToken cancelamento)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_caminhoArquivo)!);
        var caminhoTemporario = _caminhoArquivo + ".tmp";
        await using (var arquivo = File.Create(caminhoTemporario))
        {
            await JsonSerializer.SerializeAsync(arquivo, cadastros, OpcoesJson, cancelamento).ConfigureAwait(false);
        }

        File.Move(caminhoTemporario, _caminhoArquivo, overwrite: true);
    }
}

/// <summary>Cadastro concluído (após a confirmação do código).</summary>
public sealed class CadastroConcluido
{
    public string Email { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public Guid IdUsuario { get; set; }

    public DateTime DataCadastro { get; set; }
}

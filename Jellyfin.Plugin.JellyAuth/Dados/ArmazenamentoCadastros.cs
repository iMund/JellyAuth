using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Dados;

/// <summary>
/// Persistência em JSON dos cadastros concluídos (e-mail → usuário). O e-mail não existe no usuário
/// nativo do Jellyfin, então o plugin mantém o próprio mapeamento para impedir contas duplicadas.
/// Todas as leituras/gravações passam por um lock e as alterações trabalham numa cópia (se a gravação
/// falhar, nada muda em memória). Gravação atômica.
/// </summary>
public class ArmazenamentoCadastros
{
    private const string NomeArquivo = "JellyAuth.cadastros.json";
    private static readonly JsonSerializerOptions OpcoesJson = new() { WriteIndented = true };

    private readonly Lock _trava = new();
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

    /// <summary>Devolve o cadastro do e-mail (ou <c>null</c>).</summary>
    public CadastroConcluido? ObterPorEmail(string email)
    {
        lock (_trava)
        {
            return Localizar(email);
        }
    }

    /// <summary>Remove o cadastro do e-mail (usado quando o usuário não existe mais no Jellyfin).</summary>
    public bool Remover(string email)
    {
        lock (_trava)
        {
            var cadastros = Clonar();
            if (cadastros.RemoveAll(c => MesmoEmail(c, email)) == 0)
            {
                return false;
            }

            Salvar(cadastros);
            _cadastros = cadastros;
            return true;
        }
    }

    /// <summary>
    /// Registra o cadastro de forma atômica: se o e-mail já existir, retorna <c>false</c> e não altera nada.
    /// É a conferência autoritativa contra corrida (o <see cref="ObterPorEmail"/> é só uma checagem rápida).
    /// </summary>
    public bool RegistrarSeNovo(string email, string username, Guid idUsuario)
    {
        lock (_trava)
        {
            var cadastros = Clonar();
            if (cadastros.Any(c => MesmoEmail(c, email)))
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
            Salvar(cadastros);
            _cadastros = cadastros;
            return true;
        }
    }

    private CadastroConcluido? Localizar(string email)
        => Carregar().FirstOrDefault(c => MesmoEmail(c, email));

    private static bool MesmoEmail(CadastroConcluido cadastro, string email)
        => string.Equals(cadastro.Email, email, StringComparison.OrdinalIgnoreCase);

    // Sempre chamado sob _trava.
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

    // Sempre chamado sob _trava: trabalha numa cópia para não corromper o estado em memória se falhar.
    private List<CadastroConcluido> Clonar()
        => Carregar()
            .Select(c => new CadastroConcluido
            {
                Email = c.Email,
                Username = c.Username,
                IdUsuario = c.IdUsuario,
                DataCadastro = c.DataCadastro,
            })
            .ToList();

    // Sempre chamado sob _trava.
    private void Salvar(List<CadastroConcluido> cadastros)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_caminhoArquivo)!);
        var caminhoTemporario = _caminhoArquivo + ".tmp";
        File.WriteAllText(caminhoTemporario, JsonSerializer.Serialize(cadastros, OpcoesJson));
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

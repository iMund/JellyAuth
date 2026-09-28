using System.Globalization;
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

    // Arquivo corrompido que não deu para copiar: até este momento, falha na hora sem reler nem logar de novo.
    private DateTime _falharAte = DateTime.MinValue;

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

    /// <summary>
    /// Remove cadastros cujo usuário não existe mais no Jellyfin. Devolve quantos removeu.
    /// A existência é resolvida <b>fora</b> do lock (as consultas ao banco não seguram o cadastro).
    /// </summary>
    public int RemoverOrfaos(Func<Guid, bool> usuarioExiste)
    {
        List<CadastroConcluido> snapshot;
        lock (_trava)
        {
            snapshot = Clonar();
        }

        // Resolve a existência FORA do lock (as consultas ao banco não seguram o cadastro).
        var orfaos = snapshot
            .Where(c => c.IdUsuario == Guid.Empty || !usuarioExiste(c.IdUsuario))
            .Select(c => c.IdUsuario)
            .ToHashSet();

        if (orfaos.Count == 0)
        {
            return 0;
        }

        lock (_trava)
        {
            var cadastros = Clonar();
            var removidos = cadastros.RemoveAll(c => c.IdUsuario == Guid.Empty || orfaos.Contains(c.IdUsuario));
            if (removidos == 0)
            {
                return 0;
            }

            Salvar(cadastros);
            _cadastros = cadastros;
            return removidos;
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

        if (DateTime.UtcNow < _falharAte)
        {
            throw new IOException("Arquivo de cadastros do JellyAuth corrompido e sem cópia (ver o erro anterior no log).");
        }

        if (!File.Exists(_caminhoArquivo))
        {
            return _cadastros = [];
        }

        try
        {
            // Itens nulos no arquivo (editado à mão) são descartados, como nos convites: sem NullReferenceException
            // envenenando o cache em memória até reiniciar.
            var lidos = JsonSerializer.Deserialize<List<CadastroConcluido?>>(File.ReadAllText(_caminhoArquivo), OpcoesJson) ?? [];
            if (lidos.Contains(null))
            {
                _logger.LogWarning("O arquivo de cadastros do JellyAuth tem itens vazios; eles serão descartados na próxima gravação.");
            }

            return _cadastros = lidos.OfType<CadastroConcluido>().ToList();
        }
        catch (JsonException ex)
        {
            var copiaSeguranca = _caminhoArquivo + ".corrompido-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            try
            {
                File.Copy(_caminhoArquivo, copiaSeguranca, overwrite: true);
            }
            catch (Exception erroCopia) when (erroCopia is IOException or UnauthorizedAccessException)
            {
                // Sem a cópia, seguir com a lista vazia deixaria a próxima gravação apagar a única cópia dos
                // cadastros: falha até o admin resolver o arquivo (mesma política dos convites).
                // Tenta de novo (e registra de novo no log) só daqui a um minuto.
                _logger.LogError(ex, "Arquivo de cadastros do JellyAuth corrompido e não foi possível copiá-lo ({Mensagem}); nada será gravado até ele ser corrigido.", erroCopia.Message);
                _falharAte = DateTime.UtcNow.AddMinutes(1);
                throw new IOException("Arquivo de cadastros do JellyAuth corrompido e sem cópia (ver o erro anterior no log).", ex);
            }

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
        // Contém e-mails (PII): já nasce com a leitura restrita ao dono do processo.
        ArquivoProtegido.EscreverTexto(caminhoTemporario, JsonSerializer.Serialize(cadastros, OpcoesJson));
        File.Move(caminhoTemporario, _caminhoArquivo, overwrite: true);
    }
}

/// <summary>
/// Um cadastro concluído (após a confirmação do código; imutável), como fica no <c>JellyAuth.cadastros.json</c>. <b>Contrato com o JellyPix:</b> ele lê este
/// arquivo (só leitura) para preencher o e-mail na página de pagamento e no painel; os nomes <c>Email</c> e
/// <c>IdUsuario</c> e o formato (lista JSON, id como GUID) não podem mudar sem mudar lá também.
/// </summary>
public sealed class CadastroConcluido
{
    public string Email { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public Guid IdUsuario { get; init; }

    public DateTime DataCadastro { get; init; }
}

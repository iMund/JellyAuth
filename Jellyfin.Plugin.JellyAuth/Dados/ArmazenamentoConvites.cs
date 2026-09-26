using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Dados;

/// <summary>
/// Convites de cadastro em <c>JellyAuth.convites.json</c> (gravação atômica, lock, alterações numa cópia — mesmo padrão
/// dos cadastros). O código de 12 caracteres (60 bits) é mostrado ao admin uma única vez, na criação; aqui fica só o hash.
/// Adivinhar um código esbarra no rate limit do pedido de cadastro, que vem antes da conferência do convite.
/// </summary>
public class ArmazenamentoConvites
{
    private const string NomeArquivo = "JellyAuth.convites.json";

    /// <summary>Sem letras e números que se confundem (0/O, 1/I/L).</summary>
    private const string Alfabeto = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private const int TamanhoCodigo = 12;
    private const int TamanhoPrefixo = 4;
    internal const int TamanhoMaximoObservacao = 100;
    internal const int MaximoUsos = 1000;
    internal const int MaximoDiasValidade = 3650;

    private static readonly JsonSerializerOptions OpcoesJson = new() { WriteIndented = true };

    private readonly Lock _trava = new();
    private readonly string _caminhoArquivo;
    private readonly TimeProvider _relogio;
    private readonly ILogger<ArmazenamentoConvites> _logger;
    private List<Convite>? _convites;

    // Arquivo corrompido que não deu para copiar: até este momento, falha na hora sem reler nem logar de novo.
    private DateTime _falharAte = DateTime.MinValue;

    public ArmazenamentoConvites(IApplicationPaths caminhos, TimeProvider relogio, ILogger<ArmazenamentoConvites> logger)
        : this(Path.Combine(caminhos.PluginConfigurationsPath, NomeArquivo), relogio, logger)
    {
    }

    internal ArmazenamentoConvites(string caminhoArquivo, TimeProvider relogio, ILogger<ArmazenamentoConvites> logger)
    {
        _caminhoArquivo = caminhoArquivo;
        _relogio = relogio;
        _logger = logger;
    }

    /// <summary>Cria um convite e devolve o código (a única vez que ele existe em claro).</summary>
    public (Convite Convite, string Codigo) Criar(int usosMaximos, int? diasValidade, string? observacao)
    {
        var codigo = GerarCodigo();
        var agora = _relogio.GetUtcNow().UtcDateTime;
        var anotacao = (observacao ?? string.Empty).Trim();
        var convite = new Convite
        {
            Id = Guid.NewGuid(),
            CodigoHash = Hash(codigo),
            Prefixo = codigo[..TamanhoPrefixo],
            Observacao = anotacao.Length > TamanhoMaximoObservacao ? anotacao[..TamanhoMaximoObservacao] : anotacao,
            CriadoEm = agora,
            ExpiraEm = diasValidade is > 0 ? agora.AddDays(Math.Min(diasValidade.Value, MaximoDiasValidade)) : null,
            UsosMaximos = Math.Clamp(usosMaximos, 1, MaximoUsos),
        };

        lock (_trava)
        {
            var convites = Clonar();
            convites.Add(convite);
            Salvar(convites);
            _convites = convites;
        }

        return (convite, Formatar(codigo));
    }

    /// <summary>
    /// Quantos usos o código ainda tem agora; <c>0</c> se não existe, foi revogado, expirou ou esgotou. As reservas de
    /// cadastros que aguardam a confirmação do e-mail são descontadas em <see cref="ArmazenamentoCodigos"/>.
    /// </summary>
    public int UsosLivres(string? codigo)
    {
        lock (_trava)
        {
            return Localizar(Carregar(), codigo) is { } convite && Ativo(convite) ? convite.UsosMaximos - convite.Usos : 0;
        }
    }

    /// <summary>
    /// Gasta um uso do convite para o usuário criado, conferindo tudo de novo sob o lock (dois cadastros simultâneos
    /// com o mesmo convite de uso único: só um consome). <c>false</c> se o convite deixou de valer.
    /// </summary>
    public bool Consumir(string? codigo, string username)
    {
        lock (_trava)
        {
            var convites = Clonar();
            var convite = Localizar(convites, codigo);
            if (convite is null || !Ativo(convite))
            {
                return false;
            }

            convite.Usos++;
            convite.Usuarios.Add(username);
            Salvar(convites);
            _convites = convites;
            return true;
        }
    }

    public IReadOnlyList<Convite> Listar()
    {
        lock (_trava)
        {
            return Clonar().OrderByDescending(c => c.CriadoEm).ToList();
        }
    }

    public bool Revogar(Guid id)
    {
        lock (_trava)
        {
            var convites = Clonar();
            var convite = convites.FirstOrDefault(c => c.Id == id);
            if (convite is null || convite.Revogado)
            {
                return false;
            }

            convite.Revogado = true;
            Salvar(convites);
            _convites = convites;
            return true;
        }
    }

    /// <summary>Apaga um convite que já não vale (revogado, esgotado ou expirado); um ativo precisa ser revogado antes.</summary>
    public bool Excluir(Guid id)
    {
        lock (_trava)
        {
            var convites = Clonar();
            var convite = convites.FirstOrDefault(c => c.Id == id);
            if (convite is null || Ativo(convite))
            {
                return false;
            }

            convites.Remove(convite);
            Salvar(convites);
            _convites = convites;
            return true;
        }
    }

    /// <summary>Situação legível para o painel.</summary>
    public string Situacao(Convite convite)
        => convite.Revogado ? "Revogado"
            : convite.Usos >= convite.UsosMaximos ? "Esgotado"
            : convite.ExpiraEm is { } expira && expira <= _relogio.GetUtcNow().UtcDateTime ? "Expirado"
            : "Ativo";

    /// <summary>Maiúsculas, sem hífens nem espaços: o que a pessoa digita ou cola vira o mesmo código.</summary>
    internal static string Normalizar(string? codigo)
        => new string((codigo ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());

    /// <summary>O convite vale agora (não revogado, não esgotado, não expirado)?</summary>
    public bool Ativo(Convite convite)
        => !convite.Revogado
            && convite.Usos < convite.UsosMaximos
            && !(convite.ExpiraEm is { } expira && expira <= _relogio.GetUtcNow().UtcDateTime);

    private static Convite? Localizar(List<Convite> convites, string? codigo)
    {
        var normalizado = Normalizar(codigo);
        if (normalizado.Length != TamanhoCodigo)
        {
            return null;
        }

        var hash = Hash(normalizado);
        // Comparação em tempo constante com cada hash (a lista é pequena).
        return convites.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c.CodigoHash), Encoding.ASCII.GetBytes(hash)));
    }

    private static string GerarCodigo()
        => string.Create(TamanhoCodigo, 0, (destino, _) =>
        {
            for (var i = 0; i < destino.Length; i++)
            {
                destino[i] = Alfabeto[RandomNumberGenerator.GetInt32(Alfabeto.Length)];
            }
        });

    /// <summary>XXXX-XXXX-XXXX, mais fácil de ditar e de ler.</summary>
    private static string Formatar(string codigo) => string.Join('-', codigo.Chunk(4).Select(p => new string(p)));

    private static string Hash(string codigoNormalizado)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(codigoNormalizado)));

    // Sempre chamado sob _trava.
    private List<Convite> Carregar()
    {
        if (_convites is not null)
        {
            return _convites;
        }

        if (_relogio.GetUtcNow().UtcDateTime < _falharAte)
        {
            throw new IOException("Arquivo de convites do JellyAuth corrompido e sem cópia (ver o erro anterior no log).");
        }

        if (!File.Exists(_caminhoArquivo))
        {
            return _convites = [];
        }

        try
        {
            // Campos nulos no arquivo (editado à mão) viram vazios: nada de NullReferenceException, e o convite continua
            // no arquivo (um hash vazio só nunca confere).
            var lidos = JsonSerializer.Deserialize<List<Convite?>>(File.ReadAllText(_caminhoArquivo), OpcoesJson) ?? [];
            if (lidos.Contains(null))
            {
                _logger.LogWarning("O arquivo de convites do JellyAuth tem itens vazios; eles serão descartados na próxima gravação.");
            }

            return _convites = lidos.OfType<Convite>().Select(Copiar).ToList();
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
                // Sem a cópia, seguir com a lista vazia deixaria a próxima gravação apagar a única cópia dos convites: falha
                // (cadastro com convite responde 503, painel mostra erro) até o admin resolver o arquivo.
                // Tenta de novo (e registra de novo no log) só daqui a um minuto.
                _logger.LogError(ex, "Arquivo de convites do JellyAuth corrompido e não foi possível copiá-lo ({Mensagem}); nada será gravado até ele ser corrigido.", erroCopia.Message);
                _falharAte = _relogio.GetUtcNow().UtcDateTime.AddMinutes(1);
                throw;
            }

            _logger.LogError(ex, "Arquivo de convites do JellyAuth corrompido. Cópia salva em {Copia}", copiaSeguranca);
            return _convites = [];
        }
    }

    // Sempre chamado sob _trava: trabalha numa cópia para não corromper o estado em memória se falhar.
    private List<Convite> Clonar() => Carregar().Select(Copiar).ToList();

    private static Convite Copiar(Convite c) => new()
    {
        Id = c.Id,
        CodigoHash = c.CodigoHash ?? string.Empty,
        Prefixo = c.Prefixo ?? string.Empty,
        Observacao = c.Observacao ?? string.Empty,
        CriadoEm = c.CriadoEm,
        ExpiraEm = c.ExpiraEm,
        UsosMaximos = c.UsosMaximos,
        Usos = c.Usos,
        Revogado = c.Revogado,
        Usuarios = [.. c.Usuarios ?? []],
    };

    // Sempre chamado sob _trava.
    private void Salvar(List<Convite> convites)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_caminhoArquivo)!);
        var caminhoTemporario = _caminhoArquivo + ".tmp";
        File.WriteAllText(caminhoTemporario, JsonSerializer.Serialize(convites, OpcoesJson));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(caminhoTemporario, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(caminhoTemporario, _caminhoArquivo, overwrite: true);
    }
}

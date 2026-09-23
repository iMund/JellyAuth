using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyAuth.Configuracao;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Dados;

/// <summary>
/// Guarda os códigos de verificação e o controle de rate limit/cooldown, tudo em memória.
/// Os códigos expiram em poucos minutos e nunca são gravados em disco — ver ADR-002.
/// Entradas expiradas são podadas por <see cref="LimparExpirados"/> (rotina em background) e há
/// um teto de tamanho para impedir crescimento sem limite.
/// </summary>
public class ArmazenamentoCodigos
{
    private const int TamanhoCodigo = 6;
    private const int MaximoTentativasVerificacao = 5;
    private const int TetoPendentes = 50_000;
    private const int TetoTentativas = 50_000;

    private readonly ConcurrentDictionary<string, CodigoVerificacao> _pendentes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<long>> _tentativasEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<long>> _tentativasIp = new(StringComparer.Ordinal);
    private readonly Func<ConfiguracaoPlugin> _configuracao;
    private readonly TimeProvider _relogio;
    private readonly ILogger<ArmazenamentoCodigos> _logger;

    public ArmazenamentoCodigos(Func<ConfiguracaoPlugin> configuracao, TimeProvider relogio, ILogger<ArmazenamentoCodigos> logger)
    {
        _configuracao = configuracao;
        _relogio = relogio;
        _logger = logger;
    }

    /// <summary>Cria (ou substitui) o código pendente para o e-mail e devolve o código em claro.</summary>
    public string CriarCodigo(string email, string username, string password)
    {
        var agora = _relogio.GetUtcNow();
        var validade = TimeSpan.FromMinutes(_configuracao().MinutosExpiracaoCodigo);
        var codigo = GerarCodigo();
        var pendente = new CodigoVerificacao
        {
            Email = email,
            Username = username,
            Password = password,
            Codigo = codigo,
            CriadoEm = agora.UtcDateTime,
            ExpiraEm = agora.UtcDateTime.Add(validade),
            UltimoReenvioEm = agora.UtcDateTime,
        };

        if (_pendentes.Count >= TetoPendentes)
        {
            LimparPendentesExpirados();
        }

        _pendentes[email] = pendente;
        return codigo;
    }

    /// <summary>Devolve o cadastro pendente do e-mail, descartando os já expirados.</summary>
    public CodigoVerificacao? ObterPendente(string email)
    {
        if (!_pendentes.TryGetValue(email, out var pendente))
        {
            return null;
        }

        if (pendente.ExpiraEm <= _relogio.GetUtcNow())
        {
            _pendentes.TryRemove(email, out _);
            return null;
        }

        return pendente;
    }

    /// <summary>
    /// Confere o código informado contra o pendente, em tempo constante. Consome (remove) o
    /// cadastro em caso de sucesso.
    /// </summary>
    public bool Confirmar(string email, string codigo, out CodigoVerificacao? pendente)
    {
        pendente = ObterPendente(email);

        // Trabalho equivalente mesmo quando não há pendente, para não vazar a existência do e-mail por timing.
        var bytesEsperados = pendente is null
            ? new byte[TamanhoCodigo]
            : Encoding.ASCII.GetBytes(pendente.Codigo);
        var bytesInformados = Encoding.ASCII.GetBytes(codigo.Length == TamanhoCodigo ? codigo : string.Empty);

        if (pendente is null)
        {
            CryptographicOperations.FixedTimeEquals(bytesEsperados, bytesInformados);
            return false;
        }

        if (bytesInformados.Length != bytesEsperados.Length
            || !CryptographicOperations.FixedTimeEquals(bytesEsperados, bytesInformados))
        {
            var tentativas = Interlocked.Increment(ref pendente.TentativasVerificacao);
            if (tentativas >= MaximoTentativasVerificacao)
            {
                _pendentes.TryRemove(email, out _);
                _logger.LogWarning("Código de verificação de {Email} removido após várias tentativas erradas.", email);
            }

            return false;
        }

        _pendentes.TryRemove(email, out _);
        return true;
    }

    /// <summary>Reenvia: gera um código novo e renova o prazo, respeitando o cooldown.</summary>
    public string? Reenviar(string email, out TimeSpan? aguardar)
    {
        aguardar = null;
        var pendente = ObterPendente(email);
        if (pendente is null)
        {
            return null;
        }

        var agora = _relogio.GetUtcNow();
        var cooldown = TimeSpan.FromSeconds(_configuracao().MinimoSegundosReenvio);
        var desdeUltimo = agora.UtcDateTime - pendente.UltimoReenvioEm;
        if (desdeUltimo < cooldown)
        {
            aguardar = cooldown - desdeUltimo;
            return null;
        }

        var codigo = GerarCodigo();
        pendente.Codigo = codigo;
        pendente.CriadoEm = agora.UtcDateTime;
        pendente.ExpiraEm = agora.UtcDateTime.Add(TimeSpan.FromMinutes(_configuracao().MinutosExpiracaoCodigo));
        pendente.UltimoReenvioEm = agora.UtcDateTime;
        Interlocked.Exchange(ref pendente.TentativasVerificacao, 0);
        return codigo;
    }

    /// <summary>Rate limit de solicitação de cadastro: por e-mail e por IP.</summary>
    public bool PermitirSolicitacao(string email, string? ip)
    {
        var config = _configuracao();
        var agora = _relogio.GetUtcNow().UtcTicks;
        var janela = TimeSpan.FromMinutes(config.JanelaTentativasMinutos).Ticks;

        var listaEmail = _tentativasEmail.GetOrAdd(email, _ => []);
        var listaIp = string.IsNullOrWhiteSpace(ip) ? null : _tentativasIp.GetOrAdd(ip, _ => []);

        lock (listaEmail)
        {
            listaEmail.RemoveAll(t => agora - t > janela);
        }

        if (listaIp is not null)
        {
            lock (listaIp)
            {
                listaIp.RemoveAll(t => agora - t > janela);
            }
        }

        var emailOk = listaEmail.Count < config.MaximoTentativasPorEmail;
        var ipOk = listaIp is null || listaIp.Count < config.MaximoTentativasPorIp;
        if (!emailOk || !ipOk)
        {
            return false;
        }

        if (_tentativasEmail.Count >= TetoTentativas || (listaIp is not null && _tentativasIp.Count >= TetoTentativas))
        {
            LimparTentativasExpiradas();
        }

        lock (listaEmail)
        {
            listaEmail.Add(agora);
        }

        if (listaIp is not null)
        {
            lock (listaIp)
            {
                listaIp.Add(agora);
            }
        }

        return true;
    }

    /// <summary>Rate limit de confirmação/reenvio: por IP apenas (o código já limita tentativas).</summary>
    public bool PermitirVerificacao(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return true;
        }

        var config = _configuracao();
        var agora = _relogio.GetUtcNow().UtcTicks;
        var janela = TimeSpan.FromMinutes(config.JanelaTentativasMinutos).Ticks;
        var lista = _tentativasIp.GetOrAdd(ip, _ => []);

        lock (lista)
        {
            lista.RemoveAll(t => agora - t > janela);
            if (lista.Count >= config.MaximoTentativasPorIp)
            {
                return false;
            }

            lista.Add(agora);
            return true;
        }
    }

    /// <summary>Remove pendentes e contadores de rate limit expirados. Chamado periodicamente e no cap de tamanho.</summary>
    public void LimparExpirados()
    {
        LimparPendentesExpirados();
        LimparTentativasExpiradas();
    }

    private void LimparPendentesExpirados()
    {
        var agora = _relogio.GetUtcNow();
        foreach (var chave in _pendentes.Keys)
        {
            if (_pendentes.TryGetValue(chave, out var pendente) && pendente.ExpiraEm <= agora.UtcDateTime)
            {
                _pendentes.TryRemove(chave, out _);
            }
        }
    }

    private void LimparTentativasExpiradas()
    {
        var agora = _relogio.GetUtcNow().UtcTicks;
        var janela = TimeSpan.FromMinutes(_configuracao().JanelaTentativasMinutos).Ticks;

        foreach (var par in _tentativasEmail)
        {
            lock (par.Value)
            {
                par.Value.RemoveAll(t => agora - t > janela);
            }

            if (par.Value.Count == 0)
            {
                _tentativasEmail.TryRemove(par.Key, out _);
            }
        }

        foreach (var par in _tentativasIp)
        {
            lock (par.Value)
            {
                par.Value.RemoveAll(t => agora - t > janela);
            }

            if (par.Value.Count == 0)
            {
                _tentativasIp.TryRemove(par.Key, out _);
            }
        }
    }

    private static string GerarCodigo()
    {
        Span<char> codigo = stackalloc char[TamanhoCodigo];
        for (var i = 0; i < TamanhoCodigo; i++)
        {
            codigo[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        }

        return new string(codigo);
    }
}

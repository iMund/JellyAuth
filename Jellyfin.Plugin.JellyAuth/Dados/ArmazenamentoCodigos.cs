using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyAuth.Configuracao;
using Jellyfin.Plugin.JellyAuth.Seguranca;
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

    /// <summary>Um pendente com convite vale no máximo este número de prazos do código, somando reenvios e novos pedidos.</summary>
    internal const int MultiploLimiteReserva = 3;

    private readonly ConcurrentDictionary<string, CodigoVerificacao> _pendentes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<long>> _tentativasEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<long>> _tentativasIp = new(StringComparer.Ordinal);

    // Serializa o rate limit: mantém checagem e incremento atômicos (as listas internas não são thread-safe).
    private readonly Lock _travaRateLimit = new();

    // Serializa o acesso aos cadastros pendentes (cooldown do reenvio, teto rígido e reservas de convite).
    private readonly Lock _travaPendentes = new();

    // Pendentes com convite, pelo código normalizado: cada um reserva um uso do convite enquanto existir (ExpiraEm, que
    // não passa de LimiteAte) e, depois de confirmado, enquanto a conta é criada (Confirmando, até LiberarReserva). Sob
    // _travaPendentes; os que saíram de _pendentes (substituídos, expirados) são podados ao consultar o convite e na limpeza.
    private readonly Dictionary<string, List<CodigoVerificacao>> _reservas = new(StringComparer.Ordinal);

    // Limite de reserva de cada e-mail com cada convite, lembrado por um dia (sob _travaPendentes): depois do limite, pedir
    // de novo ainda cria o pendente, mas ele não reserva mais o uso — quem não confirmou não segura o convite em ciclos.
    private readonly Dictionary<string, (DateTime Limite, DateTime Esquecer)> _limitesReserva = new(StringComparer.Ordinal);
    private static readonly TimeSpan TempoLembrarLimite = TimeSpan.FromDays(1);

    private readonly Func<ConfiguracaoPlugin> _configuracao;
    private readonly TimeProvider _relogio;
    private readonly ILogger<ArmazenamentoCodigos> _logger;
    private long _ultimaPodaTicks;

    public ArmazenamentoCodigos(Func<ConfiguracaoPlugin> configuracao, TimeProvider relogio, ILogger<ArmazenamentoCodigos> logger)
    {
        _configuracao = configuracao;
        _relogio = relogio;
        _logger = logger;
    }

    /// <summary>Cria (ou substitui) o código pendente para o e-mail, sem convite. Devolve o código, ou <c>null</c> se a capacidade estiver esgotada.</summary>
    public string? CriarCodigo(string email, string username, string password)
        => CriarCodigo(email, username, password, null, () => 0, out _);

    /// <summary>
    /// Cria (ou substitui) o código pendente, reservando um uso do convite (quando há) enquanto o pendente existir.
    /// <paramref name="usosLivres"/> é consultado sob a trava, junto com as reservas: se os pendentes de outros e-mails já
    /// seguram todos os usos, não cria nada e marca <paramref name="conviteReservado"/>. Um pendente com convite nunca
    /// passa de <see cref="MultiploLimiteReserva"/> prazos desde o primeiro pedido — nem com reenvio, nem pedindo de novo
    /// com o mesmo e-mail —, e depois disso novos pedidos do mesmo e-mail não reservam mais (por um dia), para ninguém
    /// segurar um convite sem confirmar.
    /// </summary>
    public string? CriarCodigo(string email, string username, string password, string? convite, Func<int> usosLivres, out bool conviteReservado)
    {
        conviteReservado = false;
        var agora = _relogio.GetUtcNow().UtcDateTime;
        var validade = TimeSpan.FromMinutes(_configuracao().MinutosExpiracaoCodigo);
        var codigo = GerarCodigo();

        lock (_travaPendentes)
        {
            List<CodigoVerificacao>? reservas = null;
            var limite = DateTime.MaxValue;
            string? chaveLimite = null;
            (DateTime Limite, DateTime Esquecer) lembrado = default;
            var limiteNovo = false;
            List<CodigoVerificacao> descartar = [];
            if (convite is not null)
            {
                var chave = ArmazenamentoConvites.Normalizar(convite);
                reservas = ReservasVigentes(chave, agora);
                if (!TemUsoLivreSemLock(reservas, email, username, password, usosLivres(), out descartar))
                {
                    conviteReservado = true;
                    return null;
                }

                // Pedir de novo com o mesmo e-mail e o mesmo convite não renova o limite do primeiro pedido. O limite só é
                // gravado quando o pendente é criado de fato (abaixo), e é esquecido se o e-mail do pedido não sair.
                chaveLimite = email.ToLowerInvariant() + "\n" + chave;
                if (!_limitesReserva.TryGetValue(chaveLimite, out lembrado) || lembrado.Esquecer <= agora)
                {
                    lembrado = (agora.Add(validade * MultiploLimiteReserva), agora.Add(TempoLembrarLimite));
                    limiteNovo = true;
                }

                if (lembrado.Limite > agora)
                {
                    limite = lembrado.Limite;
                }
                else
                {
                    reservas = null; // passou do limite: o pedido segue, mas sem reservar (a conta só sai se o uso ainda estiver livre)
                }
            }

            if (!_pendentes.ContainsKey(email) && _pendentes.Count >= TetoPendentes)
            {
                LimparPendentesExpiradosSemLock();
                if (_pendentes.Count >= TetoPendentes)
                {
                    return null; // teto rígido: não cresce além do limite
                }
            }

            var pendente = new CodigoVerificacao
            {
                Email = email,
                Username = username,
                Password = password,
                Convite = convite,
                Codigo = codigo,
                ExpiraEm = Minimo(agora.Add(validade), limite),
                LimiteAte = limite,
                UltimoReenvioEm = agora,
                ChaveLimiteNova = limiteNovo ? chaveLimite : null,
            };
            // Só agora que o pedido novo vai existir: os pedidos antigos da mesma pessoa que ocupavam o uso saem.
            foreach (var anterior in descartar)
            {
                _pendentes.TryRemove(KeyValuePair.Create(anterior.Email, anterior));
                reservas?.Remove(anterior);
            }

            _pendentes[email] = pendente;
            reservas?.Add(pendente);

            // Teto como o dos pendentes: lotado, o pedido segue só com o limite do próprio pendente.
            if (limiteNovo && _limitesReserva.Count < TetoPendentes)
            {
                _limitesReserva[chaveLimite!] = lembrado;
            }
        }

        return codigo;
    }

    /// <summary>
    /// Para o cadastro sem verificação por e-mail (conta criada na hora): o convite ainda tem uso que não esteja reservado
    /// por pendentes de outros e-mails? Os pedidos da própria pessoa (mesmo nome e senha) com outro e-mail não contam; só
    /// consulta, não apaga nada.
    /// </summary>
    public bool ConviteTemUsoLivre(string convite, string email, string username, string password, Func<int> usosLivres)
    {
        lock (_travaPendentes)
        {
            var reservas = ReservasVigentes(ArmazenamentoConvites.Normalizar(convite), _relogio.GetUtcNow().UtcDateTime);
            return TemUsoLivreSemLock(reservas, email, username, password, usosLivres(), out _);
        }
    }

    /// <summary>Minutos inteiros (arredondados para cima, no mínimo 1) até o código pendente do e-mail vencer.</summary>
    public int? MinutosAteExpirar(string email)
    {
        var pendente = ObterPendente(email);
        return pendente is null ? null : Math.Max(1, (int)Math.Ceiling((pendente.ExpiraEm - _relogio.GetUtcNow().UtcDateTime).TotalMinutes));
    }

    /// <summary>
    /// O e-mail do pedido não saiu: descarta o pendente (a reserva do convite acaba junto) e, se foi este pedido que
    /// começou a contar o limite daquele e-mail com aquele convite, esquece o limite — a pessoa não perde prazo por uma
    /// falha do servidor.
    /// </summary>
    public void DescartarPedidoSemEmail(string email, string codigo)
    {
        lock (_travaPendentes)
        {
            if (!_pendentes.TryGetValue(email, out var pendente) || pendente.Codigo != codigo)
            {
                return;
            }

            _pendentes.TryRemove(KeyValuePair.Create(email, pendente));
            if (pendente.ChaveLimiteNova is not null)
            {
                _limitesReserva.Remove(pendente.ChaveLimiteNova);
            }
        }
    }

    /// <summary>A conta do pendente confirmado foi criada (convite gasto) ou falhou: a reserva dele acaba.</summary>
    public void LiberarReserva(CodigoVerificacao pendente)
    {
        if (pendente.Convite is null)
        {
            return;
        }

        lock (_travaPendentes)
        {
            pendente.Confirmando = false;
            if (_reservas.TryGetValue(ArmazenamentoConvites.Normalizar(pendente.Convite), out var reservas))
            {
                reservas.Remove(pendente);
            }
        }
    }

    // Sempre chamado sob _travaPendentes; não altera nada. O pedido anterior deste mesmo e-mail é substituído pelo novo e
    // não conta. Sem uso livre, os pedidos da mesma pessoa com outro e-mail (mesmo nome de usuário E mesma senha — quem só
    // sabe o nome não derruba o pedido de ninguém; é o caso de quem digitou o e-mail errado e pediu de novo) também não
    // contam e voltam em <paramref name="descartar"/>, para quem cria o pedido novo apagá-los — só se ele passar.
    private static bool TemUsoLivreSemLock(List<CodigoVerificacao> reservas, string email, string username, string password, int usosLivres, out List<CodigoVerificacao> descartar)
    {
        descartar = [];
        var deOutroEmail = reservas.Where(p => !string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase)).ToList();
        if (deOutroEmail.Count < usosLivres)
        {
            return true;
        }

        var daMesmaPessoa = deOutroEmail.Where(p => !p.Confirmando && MesmaPessoa(p, username, password)).ToList();
        if (deOutroEmail.Count - daMesmaPessoa.Count >= usosLivres)
        {
            return false;
        }

        descartar = daMesmaPessoa;
        return true;
    }

    private static bool MesmaPessoa(CodigoVerificacao pendente, string username, string password)
        => string.Equals(pendente.Username, username, StringComparison.OrdinalIgnoreCase)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pendente.Password), Encoding.UTF8.GetBytes(password));

    private static DateTime Minimo(DateTime a, DateTime b) => a < b ? a : b;

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
        lock (_travaPendentes)
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
                    _logger.LogWarning("Código de verificação de {Email} removido após várias tentativas erradas.", TextoParaLog.MascararEmail(email));
                }

                return false;
            }

            // Até a conta ser criada (e o convite gasto) ou falhar, o uso continua reservado: ver LiberarReserva.
            pendente.Confirmando = true;
            _pendentes.TryRemove(email, out _);
            return true;
        }
    }

    /// <summary>
    /// Reenvia: gera um código novo e renova o prazo (até o limite do pendente com convite), respeitando o cooldown.
    /// <c>null</c> sem pendente, dentro do cooldown (com <paramref name="aguardar"/>) ou a menos de um minuto do limite.
    /// </summary>
    public string? Reenviar(string email, out TimeSpan? aguardar)
    {
        aguardar = null;

        lock (_travaPendentes)
        {
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

            // Pendente com convite a menos de um minuto do limite: um código novo venceria antes de a pessoa usá-lo.
            if (pendente.LimiteAte - agora.UtcDateTime < TimeSpan.FromMinutes(1))
            {
                return null;
            }

            var codigo = GerarCodigo();
            pendente.Codigo = codigo;
            pendente.ExpiraEm = Minimo(agora.UtcDateTime.Add(TimeSpan.FromMinutes(_configuracao().MinutosExpiracaoCodigo)), pendente.LimiteAte);
            pendente.UltimoReenvioEm = agora.UtcDateTime;
            Interlocked.Exchange(ref pendente.TentativasVerificacao, 0);
            return codigo;
        }
    }

    /// <summary>Rate limit de solicitação de cadastro: por e-mail e por IP.</summary>
    public bool PermitirSolicitacao(string email, string? ip)
    {
        var config = _configuracao();
        var agora = _relogio.GetUtcNow().UtcTicks;
        var janela = TimeSpan.FromMinutes(config.JanelaTentativasMinutos).Ticks;

        lock (_travaRateLimit)
        {
            // Poda primeiro: se ela remover listas vazias, o GetOrAdd abaixo recria as entradas usadas.
            PodarSeNecessario(janela, agora);

            // Teto rígido: se mesmo após a poda o dicionário está cheio, rejeita.
            if (_tentativasEmail.Count >= TetoTentativas || _tentativasIp.Count >= TetoTentativas)
            {
                return false;
            }

            var listaEmail = _tentativasEmail.GetOrAdd(email, _ => []);
            listaEmail.RemoveAll(t => agora - t > janela);
            if (listaEmail.Count >= config.MaximoTentativasPorEmail)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(ip))
            {
                var listaIp = _tentativasIp.GetOrAdd(ip, _ => []);
                listaIp.RemoveAll(t => agora - t > janela);
                if (listaIp.Count >= config.MaximoTentativasPorIp)
                {
                    return false;
                }

                listaIp.Add(agora);
            }

            listaEmail.Add(agora);
            return true;
        }
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

        lock (_travaRateLimit)
        {
            PodarSeNecessario(janela, agora);

            if (_tentativasIp.Count >= TetoTentativas)
            {
                return false;
            }

            var lista = _tentativasIp.GetOrAdd(ip, _ => []);
            lista.RemoveAll(t => agora - t > janela);
            if (lista.Count >= config.MaximoTentativasPorIp)
            {
                return false;
            }

            lista.Add(agora);
            return true;
        }
    }

    // Sempre chamado sob _travaRateLimit: poda no máximo uma vez por minuto, e só quando algum dicionário está no teto.
    // (Limitar a frequência evita transformar o teto em DoS de CPU com uma poda O(n) por request.)
    private void PodarSeNecessario(long janela, long agora)
    {
        if (_tentativasEmail.Count < TetoTentativas && _tentativasIp.Count < TetoTentativas)
        {
            return;
        }

        if (agora - _ultimaPodaTicks < TimeSpan.FromMinutes(1).Ticks)
        {
            return;
        }

        _ultimaPodaTicks = agora;
        LimparTentativasExpiradas(janela, agora);
    }

    /// <summary>Remove pendentes e contadores de rate limit expirados. Chamado periodicamente e no cap de tamanho.</summary>
    public void LimparExpirados()
    {
        LimparPendentesExpirados();

        lock (_travaRateLimit)
        {
            LimparTentativasExpiradas(
                TimeSpan.FromMinutes(_configuracao().JanelaTentativasMinutos).Ticks,
                _relogio.GetUtcNow().UtcTicks);
        }
    }

    private void LimparPendentesExpirados()
    {
        lock (_travaPendentes)
        {
            LimparPendentesExpiradosSemLock();

            // Só na rotina periódica (não no caminho do teto, que roda a cada pedido quando os pendentes lotam).
            var agora = _relogio.GetUtcNow().UtcDateTime;
            foreach (var chave in _limitesReserva.Where(par => par.Value.Esquecer <= agora).Select(par => par.Key).ToList())
            {
                _limitesReserva.Remove(chave);
            }

            foreach (var convite in _reservas.Keys.ToList())
            {
                if (ReservasVigentes(convite, agora).Count == 0)
                {
                    _reservas.Remove(convite);
                }
            }
        }
    }

    // Sempre chamado sob _travaPendentes.
    private void LimparPendentesExpiradosSemLock()
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

    // Sempre chamado sob _travaPendentes: as reservas do convite cujo pendente continua o mesmo e no prazo, mais as de
    // pendentes confirmados cuja conta ainda está sendo criada.
    private List<CodigoVerificacao> ReservasVigentes(string convite, DateTime agora)
    {
        if (!_reservas.TryGetValue(convite, out var reservas))
        {
            reservas = [];
            _reservas[convite] = reservas;
        }

        reservas.RemoveAll(p => !p.Confirmando
            && (p.ExpiraEm <= agora || !_pendentes.TryGetValue(p.Email, out var atual) || !ReferenceEquals(atual, p)));
        return reservas;
    }

    // Sempre chamado sob _travaRateLimit.
    private void LimparTentativasExpiradas(long janela, long agora)
    {
        foreach (var par in _tentativasEmail)
        {
            par.Value.RemoveAll(t => agora - t > janela);
            if (par.Value.Count == 0)
            {
                _tentativasEmail.TryRemove(par.Key, out _);
            }
        }

        foreach (var par in _tentativasIp)
        {
            par.Value.RemoveAll(t => agora - t > janela);
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

using System.Net;
using Jellyfin.Plugin.JellyAuth.Api.Contratos;
using Jellyfin.Plugin.JellyAuth.Configuracao;
using Jellyfin.Plugin.JellyAuth.Seguranca;
using Jellyfin.Plugin.JellyAuth.Servicos;
using Jellyfin.Plugin.JellyAuth.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Api;

/// <summary>Endpoints públicos do auto-cadastro (login não é necessário).</summary>
[ApiController]
[Route("JellyAuth")]
[AllowAnonymous]
public class ControladorCadastro(
    ServicoCadastro cadastro,
    Func<ConfiguracaoPlugin> configuracao,
    ILogger<ControladorCadastro> logger) : ControllerBase
{
    private const int TamanhoMaximoEntrada = 500;

    // O token do Turnstile é longo (centenas/milhares de caracteres), então tem limite próprio.
    private const int TamanhoMaximoTokenCaptcha = 4096;

    /// <summary>Estado do cadastro, para o script da interface web decidir se mostra o botão.</summary>
    [HttpGet("Status")]
    public RespostaStatus Status()
    {
        var config = configuracao();
        Response.Headers.XContentTypeOptions = "nosniff";
        var captchaLigado = config.ProvedorCaptcha != TipoCaptcha.Nenhum;
        return new RespostaStatus(
            config.HabilitarCadastro,
            config.ExigirVerificacaoEmail,
            config.MinimoSegundosReenvio,
            config.ExigirSenhaForte,
            config.ProvedorCaptcha.ToString(),
            captchaLigado ? config.CaptchaSiteKey : string.Empty,
            config.ExigirConvite,
            config.MinutosExpiracaoCodigo);
    }

    /// <summary>Script da interface web (botão "Criar conta" e tela de cadastro), injetado no index.html.</summary>
    [HttpGet("client.js")]
    public ActionResult Script()
    {
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        return Content(ScriptWeb.Conteudo, "application/javascript; charset=utf-8");
    }

    /// <summary>Solicita o cadastro: valida os dados, gera e envia o código por e-mail.</summary>
    [HttpPost("Request")]
    public async Task<ActionResult> Solicitar([FromBody] PedidoRegistro pedido, CancellationToken cancelamento)
    {
        if (pedido is null)
        {
            return BadRequest(new RespostaErro("Corpo da requisição ausente."));
        }

        var username = pedido.Username?.Trim() ?? string.Empty;
        var email = pedido.Email?.Trim() ?? string.Empty;
        var password = pedido.Password ?? string.Empty;

        if (username.Length > TamanhoMaximoEntrada || email.Length > TamanhoMaximoEntrada || password.Length > TamanhoMaximoEntrada
            || (pedido.Convite?.Length ?? 0) > TamanhoMaximoEntrada
            || (pedido.CaptchaToken?.Length ?? 0) > TamanhoMaximoTokenCaptcha)
        {
            return BadRequest(new RespostaErro("Dados muito longos."));
        }

        var ip = ResolverIpCliente();
        AvisarProxiesInvalidos();
        try
        {
            var criado = await cadastro.SolicitarAsync(username, email, password, pedido.Convite, pedido.CaptchaToken, ip, cancelamento).ConfigureAwait(false);
            logger.LogInformation("Solicitação de cadastro aceita para {Email} (IP {Ip}).", TextoParaLog.MascararEmail(email), ip ?? "?");
            return Ok(new { sucesso = true, criado });
        }
        catch (ErroCadastro ex)
        {
            return StatusCode(ex.StatusCode, new RespostaErro(ex.Message));
        }
    }

    /// <summary>Confirma o código recebido por e-mail e cria o usuário.</summary>
    [HttpPost("Verify")]
    public async Task<ActionResult<RespostaCadastro>> Verificar([FromBody] PedidoVerificacao pedido)
    {
        if (pedido is null)
        {
            return BadRequest(new RespostaErro("Corpo da requisição ausente."));
        }

        var email = pedido.Email?.Trim() ?? string.Empty;
        var code = pedido.Code?.Trim() ?? string.Empty;

        if (email.Length > TamanhoMaximoEntrada || code.Length > TamanhoMaximoEntrada)
        {
            return BadRequest(new RespostaErro("Dados muito longos."));
        }

        var ip = ResolverIpCliente();
        try
        {
            await cadastro.VerificarAsync(email, code, ip).ConfigureAwait(false);
            return Ok(new RespostaCadastro(true));
        }
        catch (ErroCadastro ex)
        {
            return StatusCode(ex.StatusCode, new RespostaErro(ex.Message));
        }
    }

    /// <summary>Reenvia um novo código, respeitando o cooldown.</summary>
    [HttpPost("Resend")]
    public async Task<ActionResult> Reenviar([FromBody] PedidoVerificacao pedido, CancellationToken cancelamento)
    {
        if (pedido is null)
        {
            return BadRequest(new RespostaErro("Corpo da requisição ausente."));
        }

        var email = pedido.Email?.Trim() ?? string.Empty;
        if (email.Length > TamanhoMaximoEntrada)
        {
            return BadRequest(new RespostaErro("Dados muito longos."));
        }

        var ip = ResolverIpCliente();
        try
        {
            await cadastro.ReenviarAsync(email, ip, cancelamento).ConfigureAwait(false);
            return Ok(new { sucesso = true });
        }
        catch (ErroCadastro ex)
        {
            return StatusCode(ex.StatusCode, new RespostaErro(ex.Message));
        }
    }

    /// <summary>
    /// Resolve o IP do cliente. Se o admin confiar em proxy reverso (config.ConfiarProxy) <b>e</b> a conexão vier de um
    /// endereço local (a própria máquina ou a rede interna, onde ficam o cloudflared, o Nginx ou o contêiner do proxy),
    /// usa o X-Forwarded-For de trás para a frente: o primeiro IP que não é de um proxy confiável é o do visitante (os
    /// da frente são controlados por ele). O CF-Connecting-IP só entra se não houver X-Forwarded-For: atrás de Nginx ou
    /// Caddy ele chega do jeito que o visitante mandou, enquanto o X-Forwarded-For o proxy completa (Cloudflare, Caddy e
    /// o Nginx com proxy_add_x_forwarded_for acrescentam o IP real no fim). Conexão vinda da internet direto (porta
    /// aberta) nunca escolhe o próprio IP pelo cabeçalho.
    /// </summary>
    private string? ResolverIpCliente()
        => IpDoVisitante(
            HttpContext.Connection.RemoteIpAddress,
            configuracao(),
            Request.Headers["X-Forwarded-For"].ToString(),
            Request.Headers["CF-Connecting-IP"].ToString());

    internal static string? IpDoVisitante(IPAddress? conexao, ConfiguracaoPlugin config, string xff, string cf)
    {
        if (config.ConfiarProxy && conexao is not null && ProxyConfiavel(conexao, config.ProxiesConfiaveis))
        {
            if (xff.Length is > 0 and <= 1024)
            {
                var partes = xff.Split(',');
                for (var i = partes.Length - 1; i >= 0; i--)
                {
                    if (!IPAddress.TryParse(partes[i].Trim(), out var ip))
                    {
                        break; // item inválido: dali para a frente não dá para confiar
                    }

                    if (i == 0 || !ProxyConfiavel(ip, config.ProxiesConfiaveis))
                    {
                        return ip.ToString();
                    }
                }
            }
            else if (xff.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(cf) && IPAddress.TryParse(cf.Trim(), out var ipCf))
                {
                    return ipCf.ToString();
                }
            }
        }

        return conexao?.ToString();
    }

    private static string? _proxiesJaAvisados;

    /// <summary>Entrada da lista de proxies que não é IP nem faixa (ex.: um nome): avisa no log uma vez por texto salvo.</summary>
    private void AvisarProxiesInvalidos()
    {
        var texto = configuracao().ProxiesConfiaveis ?? string.Empty;
        if (_proxiesJaAvisados == texto)
        {
            return;
        }

        _proxiesJaAvisados = texto;
        var invalidos = InterpretarProxies(texto).Invalidos;
        if (invalidos.Count > 0)
        {
            logger.LogWarning("JellyAuth: entradas ignoradas em 'IPs de proxies' (use IP ou faixa CIDR, sem nomes): {Invalidos}", TextoParaLog.Limpar(string.Join(", ", invalidos)));
        }
    }

    /// <summary>A conexão vem de onde um proxy do admin fica: rede local/interna ou um IP que ele listou no painel.</summary>
    internal static bool ProxyConfiavel(IPAddress conexao, string? listados)
    {
        if (EnderecoLocal(conexao))
        {
            return true;
        }

        var normalizado = conexao.IsIPv4MappedToIPv6 ? conexao.MapToIPv4() : conexao;
        return InterpretarProxies(listados).Redes.Any(rede => rede.Contains(normalizado));
    }

    // Referência trocada inteira (atômica): requisições simultâneas nunca veem texto de uma lista com redes de outra.
    private static ProxiesInterpretados? _proxiesInterpretados;

    private sealed record ProxiesInterpretados(string Texto, IReadOnlyList<IPNetwork> Redes, IReadOnlyList<string> Invalidos);

    /// <summary>
    /// Lê a lista do painel: IPs ou faixas CIDR (ex.: 203.0.113.10, 203.0.113.0/24, 2001:db8::/64), separados por vírgula,
    /// ponto e vírgula, espaço ou linha. Guarda o resultado até o texto mudar.
    /// </summary>
    internal static (IReadOnlyList<IPNetwork> Redes, IReadOnlyList<string> Invalidos) InterpretarProxies(string? listados)
    {
        var texto = listados ?? string.Empty;
        var guardado = _proxiesInterpretados;
        if (guardado is not null && guardado.Texto == texto)
        {
            return (guardado.Redes, guardado.Invalidos);
        }

        var redes = new List<IPNetwork>();
        var invalidos = new List<string>();
        foreach (var item in texto.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(item, out var rede))
            {
                redes.Add(rede);
            }
            else if (IPAddress.TryParse(item, out var ip))
            {
                var normalizado = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
                redes.Add(new IPNetwork(normalizado, normalizado.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                invalidos.Add(item);
            }
        }

        _proxiesInterpretados = new ProxiesInterpretados(texto, redes, invalidos);
        return (redes, invalidos);
    }

    /// <summary>
    /// Loopback, faixa privada (10/8, 172.16/12, 192.168/16, fc00::/7), CGNAT/Tailscale (100.64/10) ou link-local
    /// (169.254/16, fe80::/10) — de onde um proxy do próprio servidor ou da rede do admin conecta.
    /// </summary>
    internal static bool EnderecoLocal(IPAddress endereco)
    {
        if (endereco.IsIPv4MappedToIPv6)
        {
            endereco = endereco.MapToIPv4();
        }

        if (IPAddress.IsLoopback(endereco))
        {
            return true;
        }

        var b = endereco.GetAddressBytes();
        return endereco.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 169 && b[1] == 254)
            : (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
    }
}

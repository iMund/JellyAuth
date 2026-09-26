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
            config.ExigirConvite);
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
    /// usa o CF-Connecting-IP ou o <b>último</b> IP válido do X-Forwarded-For (o anexado pelo proxy); o primeiro item é
    /// controlado pelo cliente. Conexão vinda da internet direto (porta aberta) nunca escolhe o próprio IP pelo cabeçalho.
    /// </summary>
    private string? ResolverIpCliente()
    {
        var conexao = HttpContext.Connection.RemoteIpAddress;
        if (configuracao().ConfiarProxy && conexao is not null && EnderecoLocal(conexao))
        {
            // Cloudflare define CF-Connecting-IP na borda (não forjável quando o tráfego passa pela Cloudflare).
            var cf = Request.Headers["CF-Connecting-IP"].ToString();
            if (!string.IsNullOrWhiteSpace(cf) && IPAddress.TryParse(cf.Trim(), out var ipCf))
            {
                return ipCf.ToString();
            }

            // Proxies genéricos anexam o IP real ao final do X-Forwarded-For.
            var xff = Request.Headers["X-Forwarded-For"].ToString();
            if (xff.Length is > 0 and <= 256)
            {
                var partes = xff.Split(',');
                for (var i = partes.Length - 1; i >= 0; i--)
                {
                    var candidato = partes[i].Trim();
                    if (IPAddress.TryParse(candidato, out var ip))
                    {
                        return ip.ToString();
                    }
                }
            }
        }

        return conexao?.ToString();
    }

    /// <summary>Loopback ou faixa privada (10/8, 172.16/12, 192.168/16, fc00::/7) — de onde um proxy do próprio servidor conecta.</summary>
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
            : (b[0] & 0xFE) == 0xFC;
    }
}

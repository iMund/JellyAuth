using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using Jellyfin.Plugin.JellyAuth.Api.Contratos;
using Jellyfin.Plugin.JellyAuth.Dados;
using Jellyfin.Plugin.JellyAuth.Servicos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Api;

/// <summary>Endpoints do painel do admin (somente admins).</summary>
[ApiController]
[Route("JellyAuth/Admin")]
[Authorize(Policy = "RequiresElevation")]
public class ControladorAdmin(
    ServicoEmail email,
    ArmazenamentoSegredos segredos,
    ILogger<ControladorAdmin> logger) : ControllerBase
{
    /// <summary>Salva a senha SMTP em arquivo separado (não vai para a configuração XML).</summary>
    [HttpPost("SalvarSenhaSmtp")]
    public ActionResult SalvarSenhaSmtp([FromBody] PedidoSenha pedido)
    {
        if (pedido is null)
        {
            return BadRequest(new RespostaErro("Corpo da requisição ausente."));
        }

        var senha = pedido.Senha ?? string.Empty;
        if (senha.Length > 500)
        {
            return BadRequest(new RespostaErro("Senha muito longa."));
        }

        segredos.SalvarSenhaSmtp(senha);
        logger.LogInformation("Senha SMTP do JellyAuth atualizada.");
        return Ok(new RespostaErro("Senha SMTP salva."));
    }

    /// <summary>Envia um e-mail de teste para conferir as credenciais SMTP.</summary>
    [HttpPost("TestarEmail")]
    public async Task<ActionResult> TestarEmail([FromBody] PedidoTesteEmail pedido, CancellationToken cancelamento)
    {
        if (pedido is null)
        {
            return BadRequest(new RespostaErro("Corpo da requisição ausente."));
        }

        var destino = pedido.Email?.Trim() ?? string.Empty;
        if (destino.Length > 200 || !new EmailAddressAttribute().IsValid(destino))
        {
            return BadRequest(new RespostaErro("Informe um e-mail válido."));
        }

        if (!email.EstaConfigurado())
        {
            return BadRequest(new RespostaErro("Configure o host SMTP e o remetente antes de testar."));
        }

        try
        {
            await email.EnviarCodigoAsync(destino, "123456", cancelamento).ConfigureAwait(false);
            logger.LogInformation("E-mail de teste do JellyAuth enviado para {Email}.", destino);
            return Ok(new RespostaErro("E-mail de teste enviado."));
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning("Falha no e-mail de teste do JellyAuth: {Mensagem}", ex.Message);
            return StatusCode(StatusCodes.Status502BadGateway, new RespostaErro("Falha ao enviar: " + ex.Message));
        }
    }
}

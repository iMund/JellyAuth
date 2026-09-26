using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using Jellyfin.Plugin.JellyAuth.Api.Contratos;
using Jellyfin.Plugin.JellyAuth.Dados;
using Jellyfin.Plugin.JellyAuth.Seguranca;
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
    ArmazenamentoConvites convites,
    ILogger<ControladorAdmin> logger) : ControllerBase
{
    /// <summary>Convites de cadastro, do mais novo para o mais antigo (sem os códigos).</summary>
    [HttpGet("Convites")]
    public ActionResult<IEnumerable<RespostaConvite>> ListarConvites()
    {
        try
        {
            return Ok(convites.Listar().Select(c =>
            {
                var (situacao, ativo) = convites.Estado(c);
                return new RespostaConvite(c.Id, c.Prefixo, c.Observacao, c.CriadoEm, c.ExpiraEm, c.UsosMaximos, c.Usos, situacao, ativo, c.Usuarios);
            }).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Falha ao ler os convites.");
            return StatusCode(StatusCodes.Status500InternalServerError, new RespostaErro("Não foi possível ler o arquivo de convites. Veja o log do servidor."));
        }
    }

    /// <summary>Cria um convite. O código só aparece nesta resposta; depois fica guardado apenas o hash.</summary>
    [HttpPost("Convites")]
    public ActionResult<RespostaConviteCriado> CriarConvite([FromBody] PedidoConvite pedido)
    {
        if (pedido is null)
        {
            return BadRequest(new RespostaErro("Corpo da requisição ausente."));
        }

        if (pedido.UsosMaximos is < 1 or > ArmazenamentoConvites.MaximoUsos)
        {
            return BadRequest(new RespostaErro($"Usos: de 1 a {ArmazenamentoConvites.MaximoUsos}."));
        }

        if (pedido.DiasValidade is < 0 or > ArmazenamentoConvites.MaximoDiasValidade)
        {
            return BadRequest(new RespostaErro($"Validade: de 0 a {ArmazenamentoConvites.MaximoDiasValidade} dias (0 = sem prazo)."));
        }

        if ((pedido.Observacao?.Length ?? 0) > ArmazenamentoConvites.TamanhoMaximoObservacao
            || (pedido.Observacao?.Any(char.IsControl) ?? false))
        {
            return BadRequest(new RespostaErro($"Observação: até {ArmazenamentoConvites.TamanhoMaximoObservacao} caracteres, sem quebras de linha."));
        }

        try
        {
            var (convite, codigo) = convites.Criar(pedido.UsosMaximos, pedido.DiasValidade, pedido.Observacao);
            logger.LogInformation("Convite {Prefixo} criado ({Usos} uso(s)).", convite.Prefixo, convite.UsosMaximos);
            return Ok(new RespostaConviteCriado(convite.Id, codigo));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Falha ao gravar o convite.");
            return StatusCode(StatusCodes.Status500InternalServerError, new RespostaErro("Não foi possível gravar o convite."));
        }
    }

    /// <summary>Revoga um convite: ele deixa de valer para cadastros novos (as contas já criadas continuam).</summary>
    [HttpPost("Convites/{id}/Revogar")]
    public ActionResult RevogarConvite([FromRoute] Guid id)
    {
        try
        {
            return convites.Revogar(id) ? NoContent() : NotFound(new RespostaErro("Convite não encontrado ou já revogado."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Falha ao revogar o convite {Id}.", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new RespostaErro("Não foi possível revogar o convite."));
        }
    }

    /// <summary>Apaga um convite que já não vale (revogado, esgotado ou expirado), para a lista não crescer sem fim.</summary>
    [HttpPost("Convites/{id}/Excluir")]
    public ActionResult ExcluirConvite([FromRoute] Guid id)
    {
        try
        {
            return convites.Excluir(id) ? NoContent() : NotFound(new RespostaErro("Convite não encontrado ou ainda ativo (revogue antes de excluir)."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Falha ao excluir o convite {Id}.", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new RespostaErro("Não foi possível excluir o convite."));
        }
    }

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

        try
        {
            segredos.SalvarSenhaSmtp(senha);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Falha ao salvar a senha SMTP do JellyAuth: {Mensagem}", TextoParaLog.Limpar(ex.Message));
            return StatusCode(StatusCodes.Status500InternalServerError, new RespostaErro("Não foi possível salvar a senha SMTP."));
        }

        logger.LogInformation("Senha SMTP do JellyAuth atualizada.");
        return Ok(new RespostaMensagem("Senha SMTP salva."));
    }

    /// <summary>Salva o secret do captcha em arquivo separado (não vai para a configuração XML).</summary>
    [HttpPost("SalvarSegredoCaptcha")]
    public ActionResult SalvarSegredoCaptcha([FromBody] PedidoSenha pedido)
    {
        if (pedido is null)
        {
            return BadRequest(new RespostaErro("Corpo da requisição ausente."));
        }

        var segredo = pedido.Senha ?? string.Empty;
        if (segredo.Length > 200)
        {
            return BadRequest(new RespostaErro("Valor muito longo."));
        }

        try
        {
            segredos.SalvarSegredoCaptcha(segredo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Falha ao salvar o secret do captcha: {Mensagem}", TextoParaLog.Limpar(ex.Message));
            return StatusCode(StatusCodes.Status500InternalServerError, new RespostaErro("Não foi possível salvar o secret do captcha."));
        }

        logger.LogInformation("Secret do captcha do JellyAuth atualizado.");
        return Ok(new RespostaMensagem("Secret do captcha salvo."));
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

        if (email.PortaSemSuporte())
        {
            return BadRequest(new RespostaErro("A porta 465 (SSL implícito) não é suportada. Use a 587 com SSL ligado (STARTTLS)."));
        }

        try
        {
            await email.EnviarCodigoAsync(destino, "123456", cancelamento).ConfigureAwait(false);
            logger.LogInformation("E-mail de teste do JellyAuth enviado para {Email}.", TextoParaLog.MascararEmail(destino));
            return Ok(new RespostaMensagem("E-mail de teste enviado."));
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException)
        {
            logger.LogWarning("Falha no e-mail de teste do JellyAuth: {Mensagem}", TextoParaLog.Limpar(ex.Message));
            return StatusCode(StatusCodes.Status502BadGateway, new RespostaErro("Não foi possível enviar o e-mail de teste. Confira as credenciais SMTP."));
        }
    }
}

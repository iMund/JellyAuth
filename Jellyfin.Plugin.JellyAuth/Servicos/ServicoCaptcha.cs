using System.Text.Json;
using Jellyfin.Plugin.JellyAuth.Configuracao;
using Jellyfin.Plugin.JellyAuth.Dados;
using Jellyfin.Plugin.JellyAuth.Seguranca;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Servicos;

/// <summary>
/// Valida o token do Cloudflare Turnstile junto ao provedor. O secret fica no
/// <see cref="ArmazenamentoSegredos"/>, nunca na configuração.
/// </summary>
public class ServicoCaptcha(
    Func<ConfiguracaoPlugin> configuracao,
    ArmazenamentoSegredos segredos,
    IHttpClientFactory httpFactory,
    ILogger<ServicoCaptcha> logger)
{
    private const string UrlVerificacao = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    /// <summary>Valida o token informado; lança <see cref="ErroCadastro"/> se inválido.</summary>
    public async Task ValidarAsync(string? token, string? ip, CancellationToken cancelamento)
    {
        var provedor = configuracao().ProvedorCaptcha;
        if (provedor == TipoCaptcha.Nenhum)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ErroCadastro("Confirme o captcha para continuar.");
        }

        var secret = segredos.ObterSegredoCaptcha();
        if (string.IsNullOrWhiteSpace(secret))
        {
            logger.LogError("Captcha habilitado, mas sem secret configurado no JellyAuth.");
            throw new ErroCadastro("O captcha não está configurado corretamente. Fale com o administrador.", StatusCodes.Status503ServiceUnavailable);
        }

        using var conteudo = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["secret"] = secret,
            ["response"] = token,
            ["remoteip"] = ip ?? string.Empty,
        });

        bool sucesso;
        try
        {
            using var cliente = httpFactory.CreateClient();
            cliente.Timeout = TimeSpan.FromSeconds(10);
            using var resposta = await cliente.PostAsync(UrlVerificacao, conteudo, cancelamento).ConfigureAwait(false);
            resposta.EnsureSuccessStatusCode();
            var corpo = await resposta.Content.ReadAsStringAsync(cancelamento).ConfigureAwait(false);
            using var documento = JsonDocument.Parse(corpo);
            sucesso = documento.RootElement.TryGetProperty("success", out var valor) && valor.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Falha ao validar o captcha (Turnstile): {Mensagem}", TextoParaLog.Limpar(ex.Message));
            throw new ErroCadastro("Não foi possível validar o captcha agora. Tente novamente em instantes.", StatusCodes.Status502BadGateway);
        }

        if (!sucesso)
        {
            throw new ErroCadastro("Captcha inválido. Tente novamente.");
        }
    }
}

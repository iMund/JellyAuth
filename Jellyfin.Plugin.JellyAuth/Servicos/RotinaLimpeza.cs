using Jellyfin.Plugin.JellyAuth.Dados;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Servicos;

/// <summary>
/// Poda periódica:
/// - códigos e contadores de rate limit expirados (impede crescimento de memória — ver ADR-002);
/// - cadastros cujo usuário foi removido do Jellyfin (libera o e-mail para um novo cadastro).
/// </summary>
public class RotinaLimpeza(
    ArmazenamentoCodigos codigos,
    ArmazenamentoCadastros cadastros,
    IUserManager usuarios,
    ILogger<RotinaLimpeza> logger) : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken parada)
    {
        using var temporizador = new PeriodicTimer(Intervalo);
        while (await temporizador.WaitForNextTickAsync(parada).ConfigureAwait(false))
        {
            try
            {
                codigos.LimparExpirados();
                var removidos = cadastros.RemoverOrfaos(id => usuarios.GetUserById(id) is not null);
                if (removidos > 0)
                {
                    logger.LogInformation("JellyAuth: {Quantidade} cadastro(s) órfão(s) removido(s).", removidos);
                }
            }
            catch (Exception ex)
            {
                // Uma falha na poda não deve derrubar a rotina de limpeza.
                logger.LogWarning(ex, "Falha ao limpar dados expirados do JellyAuth.");
            }
        }
    }
}

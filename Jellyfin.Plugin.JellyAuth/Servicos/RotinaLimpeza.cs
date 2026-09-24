using Jellyfin.Plugin.JellyAuth.Dados;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Servicos;

/// <summary>
/// Podas periodicamente códigos e contadores de rate limit expirados, impedindo que os dicionários
/// em memória cresçam sem limite (mitigação de DoS de memória — ver ADR-002).
/// </summary>
public class RotinaLimpeza(ArmazenamentoCodigos codigos, ILogger<RotinaLimpeza> logger) : BackgroundService
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
            }
            catch (Exception ex)
            {
                // Uma falha na poda não deve derrubar a rotina de limpeza.
                logger.LogWarning(ex, "Falha ao limpar dados expirados do JellyAuth.");
            }
        }
    }
}

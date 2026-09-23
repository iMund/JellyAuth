using Jellyfin.Plugin.JellyAuth.Dados;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.JellyAuth.Servicos;

/// <summary>
/// Podas periodicamente códigos e contadores de rate limit expirados, impedindo que os dicionários
/// em memória cresçam sem limite (mitigação de DoS de memória — ver ADR-002).
/// </summary>
public class RotinaLimpeza(ArmazenamentoCodigos codigos) : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken parada)
    {
        using var temporizador = new PeriodicTimer(Intervalo);
        while (await temporizador.WaitForNextTickAsync(parada).ConfigureAwait(false))
        {
            codigos.LimparExpirados();
        }
    }
}

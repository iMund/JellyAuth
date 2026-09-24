using Jellyfin.Plugin.JellyAuth.Configuracao;
using Jellyfin.Plugin.JellyAuth.Dados;
using Jellyfin.Plugin.JellyAuth.Seguranca;
using Jellyfin.Plugin.JellyAuth.Servicos;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.JellyAuth;

/// <summary>Registra os serviços do plugin no container de injeção de dependência do Jellyfin.</summary>
public class RegistroServicos : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection servicos, IServerApplicationHost aplicacao)
    {
        // Sempre lê a configuração atual, para refletir alterações feitas no painel sem reiniciar.
        servicos.AddSingleton<Func<ConfiguracaoPlugin>>(_ => () => Plugin.Instancia!.Configuration);
        servicos.TryAddSingleton(TimeProvider.System);

        servicos.AddSingleton<ArmazenamentoCodigos>();
        servicos.AddSingleton<ArmazenamentoCadastros>();
        servicos.AddSingleton<ArmazenamentoSegredos>();
        servicos.AddSingleton<ServicoEmail>();
        servicos.AddSingleton<ServicoCaptcha>();
        servicos.AddSingleton<ServicoCadastro>();

        // Poda periódica de códigos/rate limit expirados (mitigação de DoS de memória).
        servicos.AddHostedService<RotinaLimpeza>();

        // Middleware do plugin: injeta o script da interface web na resposta do index.html,
        // sem escrever no arquivo (funciona em Docker e sobrevive a atualizações do Jellyfin).
        servicos.AddTransient<IStartupFilter, InicioCadastro>();
    }
}

using Jellyfin.Plugin.JellyAuth.Servicos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.JellyAuth.Seguranca;

/// <summary>
/// Coloca o middleware do plugin na frente das rotas do Jellyfin, para injetar o script do
/// auto-cadastro na entrega do index.html da interface web.
/// </summary>
public class InicioCadastro : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> proximo)
        => aplicacao =>
        {
            aplicacao.UseMiddleware<InjetorScriptWeb>();
            proximo(aplicacao);
        };
}

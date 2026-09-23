using Jellyfin.Plugin.JellyAuth.Configuracao;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyAuth;

/// <summary>Plugin de auto-cadastro de usuários com verificação por e-mail.</summary>
public class Plugin : BasePlugin<ConfiguracaoPlugin>, IHasWebPages
{
    public Plugin(IApplicationPaths caminhos, IXmlSerializer serializadorXml)
        : base(caminhos, serializadorXml)
    {
        Instancia = this;
    }

    /// <summary>Instância carregada pelo Jellyfin (usada para ler a configuração atual).</summary>
    public static Plugin? Instancia { get; private set; }

    public override string Name => "JellyAuth";

    public override Guid Id => Guid.Parse("6591c9c1-2d2d-463b-b4e0-560fc466024b");

    public override string Description => "Auto-cadastro de usuários com verificação por e-mail.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "JellyAuth",
                DisplayName = "JellyAuth",
                EmbeddedResourcePath = GetType().Namespace + ".Configuracao.painel.html",
                EnableInMainMenu = true,
                MenuIcon = "person_add",
            },
        ];
    }
}

using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.JellyAuth.Web;

/// <summary>
/// Script da interface web (client.js) com a versão carimbada. A versão muda a cada alteração do
/// script, para que navegadores que o mantêm em cache busquem a versão nova sozinhos.
/// </summary>
public static class ScriptWeb
{
    internal const string MarcadorVersao = "__VERSAO_SCRIPT__";
    private const string NomeRecurso = ".Web.client.js";

    private static readonly Lazy<(string Conteudo, string Versao)> Carregado = new(Carregar);

    public static string Versao => Carregado.Value.Versao;

    public static string Conteudo => Carregado.Value.Conteudo;

    private static (string Conteudo, string Versao) Carregar()
    {
        using var recurso = Assembly.GetExecutingAssembly().GetManifestResourceStream(typeof(Plugin).Namespace + NomeRecurso)
            ?? throw new InvalidOperationException("client.js não encontrado no plugin.");
        using var leitor = new StreamReader(recurso, Encoding.UTF8);
        var original = leitor.ReadToEnd();
        var versao = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)))[..12].ToLowerInvariant();
        return (original.Replace(MarcadorVersao, versao, StringComparison.Ordinal), versao);
    }
}

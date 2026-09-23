using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyAuth.Web;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Servicos;

/// <summary>
/// Coloca o script do auto-cadastro na interface web, injetando a tag &lt;script&gt; <b>na resposta HTTP</b>
/// do index.html — sem escrever no arquivo. Assim funciona em qualquer instalação (inclusive Docker sem
/// permissão de escrita na pasta web) e sobrevive às atualizações do Jellyfin. O Jellyfin não tem API para
/// alterar o index.html, então a forma limpa é interceptar a entrega dele. Ver ADR-001.
/// </summary>
public class InjetorScriptWeb(
    RequestDelegate proximo,
    IApplicationPaths caminhos,
    ILogger<InjetorScriptWeb> logger)
{
    internal const string Marcador = "<!-- jellyauth -->";

    private static readonly Regex TagExistente =
        new("<script defer src=\"\\.\\./JellyAuth/client\\.js[^\"]*\"></script>" + Marcador, RegexOptions.Compiled);

    private readonly Lock _trava = new();
    private string? _htmlInjetado;
    private DateTime _fonteAtualizadaEm;
    private long _fonteTamanho = -1;
    private string? _versaoDoScript;

    public async Task InvokeAsync(HttpContext contexto)
    {
        if (EhPaginaIndex(contexto.Request) && ObterHtmlInjetado() is { } html)
        {
            contexto.Response.ContentType = "text/html; charset=utf-8";
            contexto.Response.Headers.CacheControl = "no-cache";
            await contexto.Response.WriteAsync(html, Encoding.UTF8).ConfigureAwait(false);
            return;
        }

        await proximo(contexto).ConfigureAwait(false);
    }

    internal static string TagScript(string versao) => $"<script defer src=\"../JellyAuth/client.js?v={versao}\"></script>{Marcador}";

    internal static string Transformar(string html, string versao)
    {
        var semTag = TagExistente.Replace(html, string.Empty);
        var tag = TagScript(versao);
        if (html.Contains(tag, StringComparison.Ordinal))
        {
            return html;
        }

        var posicao = semTag.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return posicao < 0 ? semTag + tag : semTag.Insert(posicao, tag);
    }

    private static bool EhPaginaIndex(HttpRequest requisicao)
    {
        if (!HttpMethods.IsGet(requisicao.Method))
        {
            return false;
        }

        var caminho = requisicao.Path.Value ?? string.Empty;
        return caminho.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || caminho.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || caminho.EndsWith("/web", StringComparison.OrdinalIgnoreCase);
    }

    private string? ObterHtmlInjetado()
    {
        var pastaWeb = caminhos.WebPath;
        if (string.IsNullOrEmpty(pastaWeb))
        {
            return null;
        }

        var caminhoIndex = Path.Combine(pastaWeb, "index.html");
        FileInfo arquivo;
        try
        {
            arquivo = new FileInfo(caminhoIndex);
            if (!arquivo.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        lock (_trava)
        {
            if (_htmlInjetado is not null
                && arquivo.LastWriteTimeUtc == _fonteAtualizadaEm
                && arquivo.Length == _fonteTamanho
                && _versaoDoScript == ScriptWeb.Versao)
            {
                return _htmlInjetado;
            }

            try
            {
                var html = File.ReadAllText(caminhoIndex);
                _htmlInjetado = Transformar(html, ScriptWeb.Versao);
                _fonteAtualizadaEm = arquivo.LastWriteTimeUtc;
                _fonteTamanho = arquivo.Length;
                _versaoDoScript = ScriptWeb.Versao;
                return _htmlInjetado;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Não foi possível ler o index.html para injetar o script de cadastro: {Mensagem}", ex.Message);
                return null;
            }
        }
    }
}

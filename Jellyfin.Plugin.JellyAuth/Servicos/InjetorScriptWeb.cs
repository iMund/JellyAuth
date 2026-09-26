using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyAuth.Seguranca;
using Jellyfin.Plugin.JellyAuth.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.JellyAuth.Servicos;

/// <summary>
/// Coloca o script do auto-cadastro na interface web, inserindo a tag &lt;script&gt; <b>na resposta HTTP</b> do
/// index.html — sem escrever no arquivo. Assim funciona em qualquer instalação (inclusive Docker sem permissão de
/// escrita na pasta web) e sobrevive às atualizações do Jellyfin. O Jellyfin não tem API para alterar o index.html,
/// então a forma limpa é interceptar a entrega dele. Ver ADR-001.
/// <para>
/// A entrega segue o caminho normal (arquivo estático do Jellyfin ou outro plugin que também mexa no index.html) e só
/// o HTML que volta ganha a tag: nunca respondemos sozinhos. Com dois plugins que injetam script (o JellyPix, por
/// exemplo), a página sai com os dois, em qualquer ordem — antes, o que ficasse na frente entregava o index.html do
/// disco e o outro ficava de fora.
/// </para>
/// </summary>
public class InjetorScriptWeb(
    RequestDelegate proximo,
    ILogger<InjetorScriptWeb> logger)
{
    internal const string Marcador = "<!-- jellyauth -->";

    /// <summary>index.html maior que isto não é mexido (o do Jellyfin tem poucos KB): passa direto, sem ficar na memória.</summary>
    internal const int TamanhoMaximoHtml = 2 * 1024 * 1024;

    /// <summary>UTF-8 que acusa bytes inválidos em vez de trocá-los por "�" (reescrever estragaria os acentos).</summary>
    private static readonly UTF8Encoding Utf8Estrito = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Regex TagExistente =
        new("<script defer src=\"\\.\\./JellyAuth/client\\.js[^\"]*\"></script>" + Marcador, RegexOptions.Compiled);

    public async Task InvokeAsync(HttpContext contexto)
    {
        if (!EhPaginaIndex(contexto.Request))
        {
            await proximo(contexto).ConfigureAwait(false);
            return;
        }

        // O HTML precisa voltar inteiro e legível: sem compressão e sem "não mudou" (304) de quem está atrás. A validação
        // de cache fica com a gente, sobre o HTML já com a tag (ETag próprio, abaixo).
        var cabecalhos = contexto.Request.Headers;
        var etagDoNavegador = cabecalhos.IfNoneMatch.ToString();
        cabecalhos.AcceptEncoding = StringValues.Empty;
        cabecalhos.IfNoneMatch = StringValues.Empty;
        cabecalhos.IfModifiedSince = StringValues.Empty;
        cabecalhos.Range = StringValues.Empty;

        var resposta = contexto.Response;
        var corpoOriginal = resposta.Body;
        using var buffer = new BufferLimitado(corpoOriginal, TamanhoMaximoHtml);
        resposta.Body = buffer;
        try
        {
            await proximo(contexto).ConfigureAwait(false);
        }
        finally
        {
            resposta.Body = corpoOriginal;
        }

        if (buffer.Transbordou)
        {
            return; // maior que qualquer index.html: já foi transmitido direto, sem ficar na memória
        }

        if (resposta.StatusCode != StatusCodes.Status200OK
            || resposta.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true
            || !StringValues.IsNullOrEmpty(resposta.Headers.ContentEncoding))
        {
            // Não é o index.html que dá para editar (erro, redirecionamento, compactado): segue como veio.
            await buffer.EnviarAsync(contexto.RequestAborted).ConfigureAwait(false);
            return;
        }

        string html;
        try
        {
            html = Utf8Estrito.GetString(buffer.Conteudo);
        }
        catch (DecoderFallbackException ex)
        {
            // Outra codificação: reescrever trocaria os acentos. Entrega como veio, sem o script.
            logger.LogWarning("JellyAuth: index.html em outra codificação, entregue sem o script do cadastro: {Mensagem}", TextoParaLog.Limpar(ex.Message));
            await buffer.EnviarAsync(contexto.RequestAborted).ConfigureAwait(false);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(Transformar(html, ScriptWeb.Versao));
        var etag = "\"ja-" + Convert.ToHexString(SHA256.HashData(bytes), 0, 8) + "\"";
        resposta.Headers.CacheControl = "no-cache";
        resposta.Headers.ETag = etag;
        // O conteúdo mudou: a data do arquivo original não vale para ele.
        resposta.Headers.LastModified = StringValues.Empty;
        if (etagDoNavegador.Contains(etag, StringComparison.Ordinal))
        {
            resposta.StatusCode = StatusCodes.Status304NotModified;
            resposta.ContentLength = null;
            return;
        }

        // O corpo sai sempre em UTF-8: o cabeçalho diz isso, qualquer que fosse o de quem entregou.
        resposta.ContentType = "text/html; charset=utf-8";
        resposta.ContentLength = bytes.Length;
        await corpoOriginal.WriteAsync(bytes, contexto.RequestAborted).ConfigureAwait(false);
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

    /// <summary>
    /// Corpo da resposta que fica na memória só até o limite; passou disso, despeja o que guardou no corpo real e segue
    /// transmitindo direto. Assim uma rota que termine em "/web" e devolva algo grande não vai inteira para a RAM.
    /// </summary>
    private sealed class BufferLimitado(Stream destino, int limite) : Stream
    {
        private readonly MemoryStream _memoria = new();

        public bool Transbordou { get; private set; }

        public ReadOnlySpan<byte> Conteudo => _memoria.GetBuffer().AsSpan(0, (int)_memoria.Length);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public Task EnviarAsync(CancellationToken cancelamento)
        {
            _memoria.Position = 0;
            return _memoria.CopyToAsync(destino, cancelamento);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> dados, CancellationToken cancelamento = default)
        {
            if (!Transbordou && _memoria.Length + dados.Length > limite)
            {
                Transbordou = true;
                await EnviarAsync(cancelamento).ConfigureAwait(false);
                _memoria.SetLength(0);
            }

            if (Transbordou)
            {
                await destino.WriteAsync(dados, cancelamento).ConfigureAwait(false);
            }
            else
            {
                _memoria.Write(dados.Span);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!Transbordou && _memoria.Length + count <= limite)
            {
                _memoria.Write(buffer, offset, count);
                return;
            }

            if (!Transbordou)
            {
                Transbordou = true;
                _memoria.Position = 0;
                _memoria.CopyTo(destino);
                _memoria.SetLength(0);
            }

            destino.Write(buffer, offset, count);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Transbordou ? destino.FlushAsync(cancellationToken) : Task.CompletedTask;

        public override void Flush()
        {
            if (Transbordou)
            {
                destino.Flush();
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _memoria.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

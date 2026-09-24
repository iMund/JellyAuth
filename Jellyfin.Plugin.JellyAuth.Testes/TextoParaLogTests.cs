using Jellyfin.Plugin.JellyAuth.Seguranca;
using Xunit;

namespace Jellyfin.Plugin.JellyAuth.Testes;

public class TextoParaLogTests
{
    [Theory]
    [InlineData("ab@x.com", "ab***@x.com")]
    [InlineData("a@x.com", "***")]
    [InlineData("", "")]
    public void MascararEmail(string entrada, string esperado)
    {
        Assert.Equal(esperado, TextoParaLog.MascararEmail(entrada));
    }

    [Fact]
    public void Limpar_RemoveCaracteresDeControle()
    {
        var saida = TextoParaLog.Limpar("linha1\nlinha2\r\tfim");

        Assert.DoesNotContain("\n", saida);
        Assert.DoesNotContain("\r", saida);
        Assert.DoesNotContain("\t", saida);
        Assert.Contains("linha1linha2", saida);
    }

    [Fact]
    public void Limpar_MascaraEmails()
    {
        var saida = TextoParaLog.Limpar("falha ao enviar para fulano@exemplo.com agora");

        Assert.Contains("fu***@exemplo.com", saida);
        Assert.DoesNotContain("fulano@exemplo.com", saida);
    }

    [Fact]
    public void Limpar_TruncaTextoLongo()
    {
        var saida = TextoParaLog.Limpar(new string('a', 1000));

        Assert.True(saida.Length <= 300);
    }
}

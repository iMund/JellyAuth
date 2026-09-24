using Jellyfin.Plugin.JellyAuth.Configuracao;
using Xunit;

namespace Jellyfin.Plugin.JellyAuth.Testes;

public class ConfiguracaoPluginTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(70000, 65535)]
    [InlineData(587, 587)]
    public void SmtpPort_Clampa(int entrada, int esperado)
    {
        var config = new ConfiguracaoPlugin { SmtpPort = entrada };

        Assert.Equal(esperado, config.SmtpPort);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99999, 1440)]
    [InlineData(15, 15)]
    public void MinutosExpiracaoCodigo_Clampa(int entrada, int esperado)
    {
        var config = new ConfiguracaoPlugin { MinutosExpiracaoCodigo = entrada };

        Assert.Equal(esperado, config.MinutosExpiracaoCodigo);
    }

    [Fact]
    public void Defaults_SaoSeguros()
    {
        var config = new ConfiguracaoPlugin();

        Assert.False(config.PermitirDownload);
        Assert.True(config.ExigirSenhaForte);
        Assert.False(config.ConfiarProxy);
        Assert.True(config.ExigirVerificacaoEmail);
        Assert.Equal(TipoCaptcha.Nenhum, config.ProvedorCaptcha);
        Assert.Empty(config.IdsBibliotecasPermitidas);
    }
}

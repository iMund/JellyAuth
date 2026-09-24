using Jellyfin.Plugin.JellyAuth.Configuracao;
using Jellyfin.Plugin.JellyAuth.Dados;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyAuth.Testes;

public class ArmazenamentoCodigosTests
{
    private static readonly DateTimeOffset Inicio = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (ArmazenamentoCodigos Armazenamento, FakeTimeProvider Relogio) Criar(ConfiguracaoPlugin? config = null)
    {
        var relogio = new FakeTimeProvider(Inicio);
        var armazenamento = new ArmazenamentoCodigos(
            () => config ?? new ConfiguracaoPlugin(),
            relogio,
            NullLogger<ArmazenamentoCodigos>.Instance);
        return (armazenamento, relogio);
    }

    [Fact]
    public void CriarCodigo_DevolveSeisDigitosNumericos()
    {
        var (armazenamento, _) = Criar();

        var codigo = armazenamento.CriarCodigo("a@b.com", "user", "senha");

        Assert.NotNull(codigo);
        Assert.Equal(6, codigo!.Length);
        Assert.All(codigo, c => Assert.True(char.IsAsciiDigit(c)));
    }

    [Fact]
    public void Confirmar_CodigoCorreto_ConsomePendente()
    {
        var (armazenamento, _) = Criar();
        var codigo = armazenamento.CriarCodigo("a@b.com", "user", "senha")!;

        Assert.True(armazenamento.Confirmar("a@b.com", codigo, out var pendente));
        Assert.NotNull(pendente);
        Assert.Equal("user", pendente!.Username);

        // Já consumido: a segunda confirmação falha.
        Assert.False(armazenamento.Confirmar("a@b.com", codigo, out _));
    }

    [Fact]
    public void Confirmar_CodigoErrado_RemoveAposCincoTentativas()
    {
        var (armazenamento, _) = Criar();
        armazenamento.CriarCodigo("a@b.com", "user", "senha");

        for (var i = 0; i < 5; i++)
        {
            Assert.False(armazenamento.Confirmar("a@b.com", "000000", out _));
        }

        Assert.Null(armazenamento.ObterPendente("a@b.com"));
    }

    [Fact]
    public void ObterPendente_Expirado_DevolveNull()
    {
        var config = new ConfiguracaoPlugin { MinutosExpiracaoCodigo = 15 };
        var (armazenamento, relogio) = Criar(config);
        armazenamento.CriarCodigo("a@b.com", "user", "senha");

        relogio.Avancar(TimeSpan.FromMinutes(16));

        Assert.Null(armazenamento.ObterPendente("a@b.com"));
    }

    [Fact]
    public void Reenviar_RespeitaCooldown()
    {
        var config = new ConfiguracaoPlugin { MinimoSegundosReenvio = 60, MinutosExpiracaoCodigo = 15 };
        var (armazenamento, relogio) = Criar(config);
        armazenamento.CriarCodigo("a@b.com", "user", "senha");

        // Logo após criar, o reenvio é bloqueado pelo cooldown.
        Assert.Null(armazenamento.Reenviar("a@b.com", out var aguardar));
        Assert.NotNull(aguardar);

        relogio.Avancar(TimeSpan.FromSeconds(61));

        Assert.NotNull(armazenamento.Reenviar("a@b.com", out var aguardar2));
        Assert.Null(aguardar2);
    }

    [Fact]
    public void PermitirSolicitacao_RespeitaLimitePorEmail()
    {
        var config = new ConfiguracaoPlugin { MaximoTentativasPorEmail = 2, MaximoTentativasPorIp = 100 };
        var (armazenamento, _) = Criar(config);

        Assert.True(armazenamento.PermitirSolicitacao("a@b.com", "1.1.1.1"));
        Assert.True(armazenamento.PermitirSolicitacao("a@b.com", "1.1.1.1"));
        Assert.False(armazenamento.PermitirSolicitacao("a@b.com", "1.1.1.1"));
    }

    [Fact]
    public void PermitirSolicitacao_RespeitaLimitePorIp()
    {
        var config = new ConfiguracaoPlugin { MaximoTentativasPorEmail = 100, MaximoTentativasPorIp = 2 };
        var (armazenamento, _) = Criar(config);

        Assert.True(armazenamento.PermitirSolicitacao("a@b.com", "9.9.9.9"));
        Assert.True(armazenamento.PermitirSolicitacao("c@d.com", "9.9.9.9"));
        Assert.False(armazenamento.PermitirSolicitacao("e@f.com", "9.9.9.9"));

        // Outro IP continua liberado.
        Assert.True(armazenamento.PermitirSolicitacao("e@f.com", "8.8.8.8"));
    }
}

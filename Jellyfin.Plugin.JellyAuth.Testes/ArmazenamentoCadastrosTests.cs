using Jellyfin.Plugin.JellyAuth.Dados;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.JellyAuth.Testes;

public class ArmazenamentoCadastrosTests : IDisposable
{
    private readonly string _caminho = Path.Combine(Path.GetTempPath(), "jellyauth-test-" + Guid.NewGuid().ToString("N") + ".json");

    private ArmazenamentoCadastros Criar() => new(_caminho, NullLogger<ArmazenamentoCadastros>.Instance);

    [Fact]
    public void RegistrarSeNovo_BloqueiaEmailDuplicado()
    {
        var armazenamento = Criar();

        Assert.True(armazenamento.RegistrarSeNovo("a@b.com", "user", Guid.NewGuid()));
        Assert.False(armazenamento.RegistrarSeNovo("a@b.com", "outro", Guid.NewGuid()));
        Assert.True(armazenamento.RegistrarSeNovo("c@d.com", "user2", Guid.NewGuid()));
    }

    [Fact]
    public void PersisteERelêDoDisco()
    {
        var id = Guid.NewGuid();
        Criar().RegistrarSeNovo("a@b.com", "user", id);

        // Nova instância lê o mesmo arquivo.
        var outro = Criar();
        var cadastro = outro.ObterPorEmail("a@b.com");

        Assert.NotNull(cadastro);
        Assert.Equal(id, cadastro!.IdUsuario);
        Assert.Null(outro.ObterPorEmail("inexistente@b.com"));
    }

    [Fact]
    public void RemoverOrfaos_RemoveUsuarioInexistenteELiberaEmail()
    {
        var armazenamento = Criar();
        var existente = Guid.NewGuid();
        var deletado = Guid.NewGuid();
        armazenamento.RegistrarSeNovo("vivo@b.com", "vivo", existente);
        armazenamento.RegistrarSeNovo("morto@b.com", "morto", deletado);

        var removidos = armazenamento.RemoverOrfaos(id => id == existente);

        Assert.Equal(1, removidos);
        Assert.NotNull(armazenamento.ObterPorEmail("vivo@b.com"));
        Assert.Null(armazenamento.ObterPorEmail("morto@b.com"));
        // E-mail liberado pode ser recadastrado.
        Assert.True(armazenamento.RegistrarSeNovo("morto@b.com", "novo", Guid.NewGuid()));
    }

    public void Dispose()
    {
        if (File.Exists(_caminho))
        {
            File.Delete(_caminho);
        }
    }
}

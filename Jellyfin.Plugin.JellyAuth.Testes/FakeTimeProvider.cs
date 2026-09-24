namespace Jellyfin.Plugin.JellyAuth.Testes;

/// <summary>Relógio controlável para os testes.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset inicio) : TimeProvider
{
    public DateTimeOffset Agora { get; set; } = inicio;

    public override DateTimeOffset GetUtcNow() => Agora;

    public void Avancar(TimeSpan duracao) => Agora = Agora.Add(duracao);
}

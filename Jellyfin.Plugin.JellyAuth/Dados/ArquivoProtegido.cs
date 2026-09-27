namespace Jellyfin.Plugin.JellyAuth.Dados;

/// <summary>
/// Escrita de arquivo que já nasce com leitura restrita ao dono do processo (0600 no Unix), para
/// segredos e dados pessoais: criar com as permissões padrão e restringir depois deixaria uma janela
/// (e, numa queda no meio, um arquivo temporário) legível por outros usuários do host.
/// </summary>
internal static class ArquivoProtegido
{
    /// <summary>Grava o texto no caminho (cria ou sobrescreve), com permissão 0600 no Unix.</summary>
    public static void EscreverTexto(string caminho, string conteudo)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(caminho, conteudo);
            return;
        }

        // Sobrou um temporário de uma execução anterior: restringe antes de reescrever (o modo do
        // FileStream só vale para arquivo novo).
        if (File.Exists(caminho))
        {
            File.SetUnixFileMode(caminho, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using var arquivo = new FileStream(caminho, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        using var escritor = new StreamWriter(arquivo);
        escritor.Write(conteudo);
    }
}

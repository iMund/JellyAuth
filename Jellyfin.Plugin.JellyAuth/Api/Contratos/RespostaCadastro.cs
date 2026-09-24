namespace Jellyfin.Plugin.JellyAuth.Api.Contratos;

/// <summary>Resposta de sucesso da confirmação de cadastro.</summary>
public sealed record RespostaCadastro(bool Sucesso);

/// <summary>Estado público do cadastro, consultado pelo script da interface web.</summary>
public sealed record RespostaStatus(
    bool Habilitado,
    bool ExigirVerificacaoEmail,
    int MinimoSegundosReenvio,
    bool ExigirSenhaForte,
    string CaptchaProvedor,
    string CaptchaSiteKey);

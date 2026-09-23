using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyAuth.Configuracao;
using Jellyfin.Plugin.JellyAuth.Dados;
using Jellyfin.Plugin.JellyAuth.Seguranca;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyAuth.Servicos;

/// <summary>
/// Regras de negócio do auto-cadastro: validação, rate limit, geração/envio do código e a criação
/// do usuário no Jellyfin após a confirmação.
/// </summary>
public class ServicoCadastro
{
    // Mesmo padrão estrito do Jellyfin (UserManager.ThrowIfInvalidUsername): símbolos Unicode de palavra,
    // números, hífen, sublinhado, apóstrofo, ponto, arroba, mais e espaços (sem espaços nas pontas).
    // "." e ".." também são recusados, como no Jellyfin.
    private static readonly Regex UsuarioValido = new(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)$", RegexOptions.Compiled);
    private const int TamanhoMaximoUsuario = 255;
    private const int TamanhoMaximoEmail = 200;
    private const int TamanhoMinimoSenha = 8;

    private readonly IUserManager _usuarios;
    private readonly ArmazenamentoCodigos _codigos;
    private readonly ArmazenamentoCadastros _cadastros;
    private readonly ServicoEmail _email;
    private readonly Func<ConfiguracaoPlugin> _configuracao;
    private readonly ILogger<ServicoCadastro> _logger;

    public ServicoCadastro(
        IUserManager usuarios,
        ArmazenamentoCodigos codigos,
        ArmazenamentoCadastros cadastros,
        ServicoEmail email,
        Func<ConfiguracaoPlugin> configuracao,
        ILogger<ServicoCadastro> logger)
    {
        _usuarios = usuarios;
        _codigos = codigos;
        _cadastros = cadastros;
        _email = email;
        _configuracao = configuracao;
        _logger = logger;
    }

    /// <summary>
    /// Solicita o cadastro. Com verificação por e-mail, gera e envia o código; sem verificação,
    /// cria o usuário na hora. O rate limit (por e-mail e por IP) vale para os dois caminhos.
    /// </summary>
    /// <returns><c>true</c> se o usuário já foi criado; <c>false</c> se aguarda o código por e-mail.</returns>
    public async Task<bool> SolicitarAsync(string username, string email, string password, string? ip, CancellationToken cancelamento)
    {
        var config = _configuracao();
        if (!config.HabilitarCadastro)
        {
            throw new ErroCadastro("O cadastro de novos usuários está desativado neste servidor.", StatusCodes.Status403Forbidden);
        }

        var usuario = username.Trim();
        var endereco = email.Trim();
        ValidarDados(usuario, endereco, password);

        if (!_codigos.PermitirSolicitacao(endereco, ip))
        {
            _logger.LogWarning("Rate limit de cadastro atingido para {Email} (IP {Ip}).", TextoParaLog.MascararEmail(endereco), ip ?? "?");
            throw new ErroCadastro("Muitas tentativas. Aguarde alguns minutos antes de tentar novamente.", StatusCodes.Status429TooManyRequests);
        }

        if (!config.ExigirVerificacaoEmail)
        {
            await CriarUsuarioAsync(usuario, password, endereco, cancelamento).ConfigureAwait(false);
            return true;
        }

        if (!_email.EstaConfigurado())
        {
            throw new ErroCadastro("O envio de e-mails não está configurado neste servidor. Fale com o administrador.", StatusCodes.Status503ServiceUnavailable);
        }

        var codigo = _codigos.CriarCodigo(endereco, usuario, password);

        try
        {
            await _email.EnviarCodigoAsync(endereco, codigo, cancelamento).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or OperationCanceledException)
        {
            _logger.LogWarning("Falha ao enviar e-mail de verificação para {Email}: {Mensagem}", TextoParaLog.MascararEmail(endereco), ex.Message);
            throw new ErroCadastro("Não foi possível enviar o e-mail de verificação. Tente novamente em alguns minutos.", StatusCodes.Status502BadGateway);
        }

        return false;
    }

    /// <summary>Confirma o código e cria o usuário definitivamente no Jellyfin.</summary>
    public async Task<Guid> VerificarAsync(string email, string code, string? ip, CancellationToken cancelamento)
    {
        var endereco = email.Trim();
        var codigo = code.Trim();

        if (!_codigos.PermitirVerificacao(ip))
        {
            throw new ErroCadastro("Muitas tentativas. Aguarde alguns minutos antes de tentar novamente.", StatusCodes.Status429TooManyRequests);
        }

        if (!_codigos.Confirmar(endereco, codigo, out var pendente) || pendente is null)
        {
            // Mensagem única de propósito: não revela se o e-mail existe nem se o código expirou.
            throw new ErroCadastro("Código inválido ou expirado. Peça um novo código.");
        }

        return await CriarUsuarioAsync(pendente.Username, pendente.Password, pendente.Email, cancelamento).ConfigureAwait(false);
    }

    /// <summary>Reenvia o código, respeitando o cooldown configurado.</summary>
    public async Task ReenviarAsync(string email, string? ip, CancellationToken cancelamento)
    {
        var endereco = email.Trim();

        if (!_codigos.PermitirVerificacao(ip))
        {
            throw new ErroCadastro("Muitas tentativas. Aguarde alguns minutos antes de tentar novamente.", StatusCodes.Status429TooManyRequests);
        }

        var codigo = _codigos.Reenviar(endereco, out var aguardar);

        if (codigo is null)
        {
            if (aguardar is not null)
            {
                var segundos = Math.Max(1, (int)Math.Ceiling(aguardar.Value.TotalSeconds));
                throw new ErroCadastro($"Aguarde {segundos}s antes de reenviar.", StatusCodes.Status429TooManyRequests);
            }

            // Mensagem única: não revela se o e-mail existe.
            throw new ErroCadastro("Nenhum cadastro pendente. Inicie o cadastro novamente.");
        }

        try
        {
            await _email.EnviarCodigoAsync(endereco, codigo, cancelamento).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or OperationCanceledException)
        {
            _logger.LogWarning("Falha ao reenviar e-mail de verificação para {Email}: {Mensagem}", TextoParaLog.MascararEmail(endereco), ex.Message);
            throw new ErroCadastro("Não foi possível reenviar o e-mail. Tente novamente em alguns minutos.", StatusCodes.Status502BadGateway);
        }
    }

    private void ValidarDados(string usuario, string endereco, string password)
    {
        if (!UsuarioValido.IsMatch(usuario) || usuario is "." or ".." || usuario.Length > TamanhoMaximoUsuario)
        {
            throw new ErroCadastro("Nome de usuário inválido. Use letras, números, hífen (-), sublinhado (_), apóstrofo ('), ponto (.) ou arroba (@).");
        }

        if (endereco.Length > TamanhoMaximoEmail || !new EmailAddressAttribute().IsValid(endereco))
        {
            throw new ErroCadastro("Informe um e-mail válido.");
        }

        if (password.Length < TamanhoMinimoSenha)
        {
            throw new ErroCadastro($"A senha deve ter pelo menos {TamanhoMinimoSenha} caracteres.");
        }

        if (_configuracao().ExigirSenhaForte && (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)))
        {
            throw new ErroCadastro("A senha deve conter letras e números.");
        }

        // Mensagem única de propósito: não revela qual dos dois (usuário ou e-mail) já existe.
        if (_usuarios.GetUserByName(usuario) is not null || _cadastros.EmailJaCadastrado(endereco))
        {
            throw new ErroCadastro("Este nome de usuário ou e-mail já está em uso.");
        }
    }

    private async Task<Guid> CriarUsuarioAsync(string username, string password, string email, CancellationToken cancelamento)
    {
        var config = _configuracao();

        Jellyfin.Database.Implementations.Entities.User usuario;
        try
        {
            usuario = await _usuarios.CreateUserAsync(username).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // Corrida: o nome passou a existir entre a validação e a criação.
            throw new ErroCadastro("Este nome de usuário ou e-mail já está em uso.");
        }

#if NET10_0_OR_GREATER
        // Jellyfin 12: ChangePassword recebe o id do usuário.
        await _usuarios.ChangePassword(usuario.Id, password).ConfigureAwait(false);
#else
        // Jellyfin 10.11: ChangePassword recebe a entidade User.
        await _usuarios.ChangePassword(usuario, password).ConfigureAwait(false);
#endif

        AplicarRegrasDeUsuario(usuario, config);
        await _usuarios.UpdateUserAsync(usuario).ConfigureAwait(false);

        if (!await _cadastros.RegistrarSeNovoAsync(email, username, usuario.Id, cancelamento).ConfigureAwait(false))
        {
            // Corrida: o e-mail foi registrado por outra requisição. Remove o usuário recém-criado.
            try
            {
                await _usuarios.DeleteUserAsync(usuario.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Não foi possível remover o usuário {Username} após e-mail duplicado: {Mensagem}", username, ex.Message);
            }

            throw new ErroCadastro("Este nome de usuário ou e-mail já está em uso.");
        }

        _logger.LogInformation("Usuário {Username} criado via auto-cadastro (e-mail {Email}).", username, TextoParaLog.MascararEmail(email));

        return usuario.Id;
    }

    private void AplicarRegrasDeUsuario(Jellyfin.Database.Implementations.Entities.User usuario, ConfiguracaoPlugin config)
    {
        // Download de mídia (desligado por padrão).
        usuario.SetPermission(PermissionKind.EnableContentDownloading, config.PermitirDownload);

        // Bibliotecas ficam desligadas por padrão: outro plugin (JellyPix) libera o acesso
        // conforme a situação de cada usuário. Sem EnabledFolders, o usuário não vê nenhuma biblioteca.
        usuario.SetPermission(PermissionKind.EnableAllFolders, false);
    }
}

/// <summary>Erro de negócio do auto-cadastro, com status HTTP e mensagem amigável ao usuário.</summary>
public sealed class ErroCadastro : Exception
{
    public ErroCadastro(string mensagem, int statusCode = StatusCodes.Status400BadRequest)
        : base(mensagem)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

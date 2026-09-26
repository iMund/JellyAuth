using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
    // Uma mensagem só para convite inexistente, revogado, esgotado ou reservado por outro cadastro: a resposta não revela
    // se um código testado existe.
    private const string MensagemConviteInvalido = "Convite inválido, expirado, já usado ou em uso num cadastro que aguarda a confirmação do e-mail. Tente de novo mais tarde ou peça um novo convite ao administrador do servidor.";

    // A assinatura de IUserManager.ChangePassword mudou dentro da própria série 10.11
    // (User → Guid). Resolvemos em tempo de execução para um único build funcionar em qualquer versão.
    private static readonly MethodInfo MetodoChangePassword =
        typeof(IUserManager).GetMethod(nameof(IUserManager.ChangePassword), BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("IUserManager.ChangePassword não encontrado nesta versão do Jellyfin.");

    // O primeiro parâmetro é Guid (12) ou User (10.11 antigo) — resolvido uma única vez.
    private static readonly bool SenhaPorId = MetodoChangePassword.GetParameters()[0].ParameterType == typeof(Guid);

    private readonly IUserManager _usuarios;
    private readonly ArmazenamentoCodigos _codigos;
    private readonly ArmazenamentoCadastros _cadastros;
    private readonly ArmazenamentoConvites _convites;
    private readonly ServicoEmail _email;
    private readonly ServicoCaptcha _captcha;
    private readonly Func<ConfiguracaoPlugin> _configuracao;
    private readonly ILogger<ServicoCadastro> _logger;

    public ServicoCadastro(
        IUserManager usuarios,
        ArmazenamentoCodigos codigos,
        ArmazenamentoCadastros cadastros,
        ArmazenamentoConvites convites,
        ServicoEmail email,
        ServicoCaptcha captcha,
        Func<ConfiguracaoPlugin> configuracao,
        ILogger<ServicoCadastro> logger)
    {
        _usuarios = usuarios;
        _codigos = codigos;
        _cadastros = cadastros;
        _convites = convites;
        _email = email;
        _captcha = captcha;
        _configuracao = configuracao;
        _logger = logger;
    }

    /// <summary>
    /// Solicita o cadastro. Com verificação por e-mail, gera e envia o código; sem verificação,
    /// cria o usuário na hora. O rate limit (por e-mail e por IP) vale para os dois caminhos.
    /// </summary>
    /// <returns><c>true</c> se o usuário já foi criado; <c>false</c> se aguarda o código por e-mail.</returns>
    public async Task<bool> SolicitarAsync(string username, string email, string password, string? convite, string? captchaToken, string? ip, CancellationToken cancelamento)
    {
        var config = _configuracao();
        if (!config.HabilitarCadastro)
        {
            throw new ErroCadastro("O cadastro de novos usuários está desativado neste servidor.", StatusCodes.Status403Forbidden);
        }

        var usuario = username.Trim();
        var endereco = NormalizarEmail(email);

        ValidarFormato(usuario, endereco, password);

        // Rate limit antes de consultar o banco (rejeita cedo, evita trabalho para quem está bloqueado).
        if (!_codigos.PermitirSolicitacao(endereco, ip))
        {
            _logger.LogWarning("Rate limit de cadastro atingido para {Email} (IP {Ip}).", TextoParaLog.MascararEmail(endereco), ip ?? "?");
            throw new ErroCadastro("Muitas tentativas. Aguarde alguns minutos antes de tentar novamente.", StatusCodes.Status429TooManyRequests);
        }

        // Captcha depois do rate limit: a validação é uma chamada HTTP externa, então fica limitada por IP/e-mail.
        await _captcha.ValidarAsync(captchaToken, ip, cancelamento).ConfigureAwait(false);

        // Convite depois do rate limit: adivinhar códigos esbarra no limite por e-mail e por IP. Aqui só confere; o uso
        // é gasto quando a conta é criada (um pedido que nunca confirma o e-mail não queima o convite). Os cadastros de
        // outros e-mails que aguardam a confirmação com o mesmo convite reservam um uso cada (ver CriarCodigo).
        var conviteUsado = config.ExigirConvite ? convite : null;
        if (config.ExigirConvite && UsosLivresDoConvite(convite) == 0)
        {
            throw new ErroCadastro(MensagemConviteInvalido);
        }

        VerificarDisponibilidade(usuario, endereco);

        if (!config.ExigirVerificacaoEmail)
        {
            // Sem verificação a conta sai na hora, mas os pendentes de antes da troca da configuração seguem reservando.
            if (conviteUsado is not null && !_codigos.ConviteTemUsoLivre(conviteUsado, endereco, usuario, password, () => UsosLivresDoConvite(conviteUsado)))
            {
                throw new ErroCadastro(MensagemConviteInvalido);
            }

            await CriarUsuarioAsync(usuario, password, endereco, conviteUsado).ConfigureAwait(false);
            return true;
        }

        if (!_email.EstaConfigurado())
        {
            throw new ErroCadastro("O envio de e-mails não está configurado neste servidor. Fale com o administrador.", StatusCodes.Status503ServiceUnavailable);
        }

        // Os usos livres são lidos de novo sob a trava dos pendentes, junto com as reservas.
        var codigo = _codigos.CriarCodigo(endereco, usuario, password, conviteUsado, () => UsosLivresDoConvite(conviteUsado), out var conviteReservado);
        if (conviteReservado)
        {
            throw new ErroCadastro(MensagemConviteInvalido);
        }

        if (codigo is null)
        {
            throw new ErroCadastro("O servidor está com muitas solicitações pendentes. Tente novamente em alguns minutos.", StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            await _email.EnviarCodigoAsync(endereco, codigo, cancelamento, _codigos.MinutosAteExpirar(endereco)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException)
        {
            _logger.LogWarning("Falha ao enviar e-mail de verificação para {Email}: {Mensagem}", TextoParaLog.MascararEmail(endereco), TextoParaLog.Limpar(ex.Message));
            _codigos.DescartarPedidoSemEmail(endereco, codigo);
            throw new ErroCadastro("Não foi possível enviar o e-mail de verificação. Tente novamente em alguns minutos.", StatusCodes.Status502BadGateway);
        }
        catch
        {
            // Envio cancelado (a pessoa fechou a página) ou outra falha: o pedido sem e-mail não segura o convite.
            _codigos.DescartarPedidoSemEmail(endereco, codigo);
            throw;
        }

        return false;
    }

    /// <summary>Confirma o código e cria o usuário definitivamente no Jellyfin.</summary>
    public async Task VerificarAsync(string email, string code, string? ip)
    {
        var endereco = NormalizarEmail(email);
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

        // A exigência vale como está agora: ligada depois do pedido, um pendente sem convite não vale mais; desligada,
        // o convite do pedido é ignorado (nem conferido, nem gasto).
        try
        {
            var exigirConvite = _configuracao().ExigirConvite;
            var convite = exigirConvite ? pendente.Convite : null;
            if (exigirConvite && convite is null)
            {
                throw new ErroCadastro("Este servidor agora exige convite. Comece o cadastro de novo com o seu convite.");
            }

            // Revogado ou expirado enquanto o e-mail não era confirmado: recusa antes de criar a conta.
            if (convite is not null && UsosLivresDoConvite(convite) == 0)
            {
                throw new ErroCadastro(MensagemConviteInvalido);
            }

            await CriarUsuarioAsync(pendente.Username, pendente.Password, pendente.Email, convite).ConfigureAwait(false);
        }
        finally
        {
            // Convite gasto (ou cadastro que falhou): o uso deixa de estar reservado para este pedido.
            _codigos.LiberarReserva(pendente);
        }
    }

    /// <summary>Reenvia o código, respeitando o cooldown configurado.</summary>
    public async Task ReenviarAsync(string email, string? ip, CancellationToken cancelamento)
    {
        var endereco = NormalizarEmail(email);

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
            await _email.EnviarCodigoAsync(endereco, codigo, cancelamento, _codigos.MinutosAteExpirar(endereco)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException)
        {
            _logger.LogWarning("Falha ao reenviar e-mail de verificação para {Email}: {Mensagem}", TextoParaLog.MascararEmail(endereco), TextoParaLog.Limpar(ex.Message));
            throw new ErroCadastro("Não foi possível reenviar o e-mail. Tente novamente em alguns minutos.", StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Normaliza o e-mail (trim + NFC) para dedup e rate limit consistentes.</summary>
    private static string NormalizarEmail(string email)
    {
        var limpo = email.Trim();
        try
        {
            return limpo.Normalize();
        }
        catch (ArgumentException)
        {
            // Unicode inválido: usa como veio.
            return limpo;
        }
    }

    /// <summary>Validações de formato (sem consultar o banco), feitas antes do rate limit.</summary>
    private void ValidarFormato(string usuario, string endereco, string password)
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
    }

    /// <summary>Checagens que consultam o Jellyfin (usuário/e-mail já existentes) — feitas após o rate limit.</summary>
    private void VerificarDisponibilidade(string usuario, string endereco)
    {
        // Um cadastro cujo usuário foi apagado no Jellyfin não deve bloquear o e-mail para um novo cadastro.
        var cadastroExistente = _cadastros.ObterPorEmail(endereco);
        if (cadastroExistente is not null
            && (cadastroExistente.IdUsuario == Guid.Empty || _usuarios.GetUserById(cadastroExistente.IdUsuario) is null))
        {
            try
            {
                if (_cadastros.Remover(endereco))
                {
                    cadastroExistente = null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Não conseguiu limpar: mantém bloqueado (falha segura, evita conta duplicada).
                _logger.LogWarning("Não foi possível limpar o cadastro órfão de {Email}: {Mensagem}", TextoParaLog.MascararEmail(endereco), TextoParaLog.Limpar(ex.Message));
            }
        }

        // Mensagem única de propósito: não revela qual dos dois (usuário ou e-mail) já existe.
        if (_usuarios.GetUserByName(usuario) is not null || cadastroExistente is not null)
        {
            throw new ErroCadastro("Este nome de usuário ou e-mail já está em uso.");
        }
    }

    private async Task CriarUsuarioAsync(string username, string password, string email, string? convite)
    {
        var config = _configuracao();
        var usuario = await CriarContaJellyfinAsync(username, password, config).ConfigureAwait(false);

        bool registrado;
        try
        {
            registrado = _cadastros.RegistrarSeNovo(email, username, usuario.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Falha ao gravar: desfaz o usuário para não deixá-lo órfão (sem e-mail registrado).
            _logger.LogError(ex, "Falha ao gravar o cadastro de {Email}; desfazendo o usuário {Username}.", TextoParaLog.MascararEmail(email), username);
            await ApagarUsuarioAsync(usuario.Id).ConfigureAwait(false);
            throw new ErroCadastro("Não foi possível concluir o cadastro agora. Tente novamente em instantes.", StatusCodes.Status503ServiceUnavailable);
        }

        if (!registrado)
        {
            // Corrida: o e-mail foi registrado por outra requisição. Remove o usuário recém-criado.
            await ApagarUsuarioAsync(usuario.Id).ConfigureAwait(false);
            throw new ErroCadastro("Este nome de usuário ou e-mail já está em uso.");
        }

        if (convite is not null && !ConsumirConvite(convite, username))
        {
            // Outro cadastro gastou o último uso do convite enquanto este esperava: desfaz a conta e o registro.
            DesfazerCadastro(email);
            await ApagarUsuarioAsync(usuario.Id).ConfigureAwait(false);
            throw new ErroCadastro("Este convite acabou de ser usado por outra pessoa. Peça um novo convite ao administrador do servidor.");
        }

        _logger.LogInformation("Usuário {Username} criado via auto-cadastro (e-mail {Email}).", username, TextoParaLog.MascararEmail(email));
    }

    private int UsosLivresDoConvite(string? convite)
    {
        try
        {
            return _convites.UsosLivres(convite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Falha ao ler o arquivo de convites do JellyAuth.");
            throw new ErroCadastro("Não foi possível conferir o convite agora. Tente novamente em instantes.", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private bool ConsumirConvite(string convite, string username)
    {
        try
        {
            return _convites.Consumir(convite, username);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Falha ao gravar o uso do convite do usuário {Username}.", username);
            return false;
        }
    }

    private void DesfazerCadastro(string email)
    {
        try
        {
            _cadastros.Remover(email);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Não foi possível desfazer o cadastro de {Email}: {Mensagem}", TextoParaLog.MascararEmail(email), TextoParaLog.Limpar(ex.Message));
        }
    }

    private async Task<Jellyfin.Database.Implementations.Entities.User> CriarContaJellyfinAsync(string username, string password, ConfiguracaoPlugin config)
    {
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

        try
        {
            // Ordem importa: UpdateUserAsync copia os valores do objeto (SetValues) e sobrescreveria a
            // senha com null caso ChangePassword viesse antes. Por isso a senha é definida por último.
            AplicarRegrasDeUsuario(usuario, config);
            await _usuarios.UpdateUserAsync(usuario).ConfigureAwait(false);
            await TrocarSenhaAsync(usuario, password).ConfigureAwait(false);
        }
        catch
        {
            // Qualquer falha após criar: desfaz para não deixar um usuário sem senha/inutilizável.
            await ApagarUsuarioAsync(usuario.Id).ConfigureAwait(false);
            throw;
        }

        return usuario;
    }

    private async Task ApagarUsuarioAsync(Guid id)
    {
        try
        {
            await _usuarios.DeleteUserAsync(id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Não foi possível remover o usuário {Id}: {Mensagem}", id, TextoParaLog.Limpar(ex.Message));
        }
    }

    private async Task TrocarSenhaAsync(Jellyfin.Database.Implementations.Entities.User usuario, string senha)
    {
        object alvo = SenhaPorId ? usuario.Id : usuario;

        Task tarefa;
        try
        {
            tarefa = (Task)MetodoChangePassword.Invoke(_usuarios, new object[] { alvo, senha })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Propaga a exceção real (sem o embrulho da reflexão).
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        await tarefa.ConfigureAwait(false);
    }

    private static void AplicarRegrasDeUsuario(Jellyfin.Database.Implementations.Entities.User usuario, ConfiguracaoPlugin config)
    {
        // O usuário tem senha local (login por usuário/senha).
        usuario.EnableLocalPassword = true;

        // Bloqueio após 3 tentativas de login inválidas (mesmo padrão do Jellyfin), contra força bruta.
        usuario.LoginAttemptsBeforeLockout = 3;

        // Download de mídia (desligado por padrão).
        usuario.SetPermission(PermissionKind.EnableContentDownloading, config.PermitirDownload);

        // Bibliotecas: nenhuma marcada = o usuário começa sem acesso a nenhuma;
        // caso contrário, libera apenas as selecionadas.
        usuario.SetPermission(PermissionKind.EnableAllFolders, false);
        if (config.IdsBibliotecasPermitidas.Length > 0)
        {
            usuario.SetPreference(PreferenceKind.EnabledFolders, config.IdsBibliotecasPermitidas);
        }
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

/* JellyAuth: botão "Criar conta" na tela de login e a rota #/register com o formulário de
   cadastro e a verificação por e-mail. Injetado na resposta do index.html pelo plugin (ver ADR-001).
   Roda também dentro dos apps oficiais que embutem a interface web. */
(function () {
  'use strict';
  if (window.__jellyAuth) return;
  window.__jellyAuth = true;

  // Carimbada pelo servidor ao entregar o script (ScriptWeb.cs).
  var VERSAO = '__VERSAO_SCRIPT__';
  var script = document.currentScript;
  var BASE = script ? script.src.replace(/\/JellyAuth\/client\.js.*$/i, '') : '';
  var ROTA_REGISTRO = '#/register';
  var ROTA_LOGIN = '#/login';
  // Convite recebido pelo link (#/register?convite=XXXX-XXXX-XXXX): guardado na aba até o cadastro terminar, porque o
  // Jellyfin pode mandar primeiro para o login (#/login?...&url=%2Fregister%3Fconvite%3D...).
  var CHAVE_CONVITE = 'jellyauth-convite';
  // Último convite com que uma conta foi criada nesta aba: voltar no histórico para o link não o traz de novo.
  var CHAVE_CONVITE_USADO = 'jellyauth-convite-usado';
  // Só letras e números, em maiúsculas: "abcd efgh-jkmn" e "ABCD-EFGH-JKMN" são o mesmo convite (como no servidor).
  function normalizarConvite(v) { return String(v || '').toUpperCase().replace(/[^A-Z0-9]/g, ''); }

  // Cores/visual do tema escuro do Jellyfin (veja "04 - Frontend & UI/Componentes e CSS Variables.md").
  var COR_ACCENT = '#00a4dc';
  var COR_FUNDO = '#101010';
  var COR_CARTAO = '#1c2126';
  var COR_TEXTO = '#eef2f5';
  var COR_SECUNDARIA = '#aab6c0';
  var COR_ERRO = '#f2555a';
  var COR_SUCESSO = '#3ecf8e';

  var estado = { habilitado: false, exigirVerificacao: true, cooldownReenvio: 60, exigirSenhaForte: true, captchaProvedor: 'Nenhum', captchaSiteKey: '', exigirConvite: false, consultado: false, logado: false };
  var overlay = null;
  var temporizadorReenvio = null;
  var dadosFormulario = null;
  var widgetCaptcha = null;
  // Tela que está no overlay ('formulario', 'verificacao' ou 'sucesso') e a configuração com que o formulário foi montado.
  var telaAtual = 'formulario';
  var formularioMontadoCom = '';
  // A rota #/register não existe no Jellyfin: ele marca a página como "Página indisponível". Enquanto o formulário
  // está na tela, o título é o nosso (reaplicado a cada verificação, porque o Jellyfin pode trocá-lo depois). Ao sair,
  // volta o último título visto fora do cadastro (o do index.html, se a pessoa entrou direto pelo link do cadastro).
  var TITULO_CADASTRO = 'Criar conta';
  var tituloForaDoCadastro = document.title || 'Jellyfin';

  function naRotaRegistro() {
    var h = (location.hash || '').split('?')[0];
    return h === ROTA_REGISTRO || h === ROTA_REGISTRO + '/';
  }

  function lerSessao(chave) { try { return sessionStorage.getItem(chave) || ''; } catch (e) { return ''; } }
  function gravarSessao(chave, valor) { try { if (valor) sessionStorage.setItem(chave, valor); else sessionStorage.removeItem(chave); } catch (e) { /* sem armazenamento: segue sem lembrar */ } }

  /**
   * Pega o convite do link (direto ou dentro do "url=" do redirecionamento para o login) e leva para o cadastro.
   * Uma vez por endereço: depois do cadastro o endereço ainda tem o convite, e ler de novo devolveria à aba o convite
   * que acabou de ser usado. Roda no ciclo de 1 s, e não só no hashchange, porque o Jellyfin leva ao login por
   * pushState (o roteador dele), que não dispara hashchange.
   */
  var ultimoEnderecoLido = null;
  var levarAoCadastro = false; // veio convite pelo link: ir ao cadastro quando o status disser que ele está ligado
  function capturarConviteDoLink() {
    var h = location.hash || '';
    if (h === ultimoEnderecoLido) return;
    ultimoEnderecoLido = h;
    var texto = h;
    try { texto = h + ' ' + decodeURIComponent(h); } catch (e) { /* hash com % solto: usa como veio */ }
    var achado = /[?&]convite=([A-Za-z0-9-]{4,40})/.exec(texto);
    if (!achado) return;
    var convite = achado[1].toUpperCase();
    if (normalizarConvite(convite) === lerSessao(CHAVE_CONVITE_USADO)) return;
    gravarSessao(CHAVE_CONVITE, convite);
    // Formulário já montado (a pessoa passou pelo cadastro antes nesta aba): põe o convite novo no campo.
    var campo = overlay && overlay.querySelector('#ja-convite');
    if (campo) campo.value = convite;
    levarAoCadastro = true;
  }

  /** Depois da consulta de status: com o cadastro ligado, leva ao formulário; desligado, não deixa na página indisponível. */
  function seguirLinkDoConvite() {
    if (!levarAoCadastro || !estado.consultado) return;
    levarAoCadastro = false;
    if (estaLogado()) return;
    if (estado.habilitado && !naRotaRegistro()) location.hash = ROTA_REGISTRO;
    else if (!estado.habilitado && naRotaRegistro()) location.hash = ROTA_LOGIN;
  }

  function estaLogado() {
    try {
      if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
        return !!window.ApiClient.accessToken();
      }
      var credenciais = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
      var servidores = (credenciais.Servers || []).filter(function (s) { return s.AccessToken; });
      return servidores.length > 0;
    } catch (e) {
      return false;
    }
  }

  function consultarStatus() {
    return fetch(BASE + '/JellyAuth/Status')
      .then(function (r) { return r.ok ? r.json() : { Habilitado: false, ExigirVerificacaoEmail: true, MinimoSegundosReenvio: 60, ExigirSenhaForte: true }; })
      .catch(function () { return { Habilitado: false, ExigirVerificacaoEmail: true, MinimoSegundosReenvio: 60, ExigirSenhaForte: true }; })
      .then(function (s) {
        estado.habilitado = !!(s && s.Habilitado);
        estado.exigirVerificacao = !(s && s.ExigirVerificacaoEmail === false);
        estado.cooldownReenvio = (s && s.MinimoSegundosReenvio > 0) ? s.MinimoSegundosReenvio : 60;
        estado.exigirSenhaForte = !(s && s.ExigirSenhaForte === false);
        estado.captchaProvedor = (s && s.CaptchaProvedor) || 'Nenhum';
        estado.captchaSiteKey = (s && s.CaptchaSiteKey) || '';
        estado.exigirConvite = !!(s && s.ExigirConvite);
        estado.consultado = true;
        // Formulário na tela montado com outra configuração (o admin mudou algo): monta de novo, antes de a pessoa digitar.
        if (overlay && overlay.style.display !== 'none' && telaAtual === 'formulario' && formularioMontadoCom !== configuracaoDoFormulario()) {
          renderizarCadastro();
        }
        atualizarInterface();
      });
  }

  function atualizarInterface() {
    capturarConviteDoLink();
    seguirLinkDoConvite();
    estado.logado = estaLogado();
    if (estado.habilitado && naRotaRegistro() && !estado.logado) {
      mostrarOverlay();
    } else {
      esconderOverlay();
    }

    if (estado.habilitado && !estado.logado && !naRotaRegistro()) {
      injetarBotaoLogin();
    }
  }

  // O token de login muda quando o usuário entra/sai; observamos o hash e o token de tempos em tempos.
  window.addEventListener('hashchange', atualizarInterface);
  setInterval(atualizarInterface, 1000);
  capturarConviteDoLink();

  function garantirEstilo() {
    if (document.getElementById('jellyauth-estilo')) return;
    var estilo = document.createElement('style');
    estilo.id = 'jellyauth-estilo';
    estilo.textContent =
      '#jellyauth-overlay{position:fixed;inset:0;z-index:10000;display:flex;align-items:center;justify-content:center;' +
      'padding:16px;background:' + COR_FUNDO + ';overflow:auto}' +
      '#jellyauth-overlay .ja-cartao{width:100%;max-width:420px;background:' + COR_CARTAO + ';color:' + COR_TEXTO + ';' +
      'border:1px solid rgba(255,255,255,.08);border-radius:16px;padding:28px 24px 22px;font-family:system-ui,sans-serif;' +
      'box-shadow:0 20px 60px rgba(0,0,0,.5)}' +
      '#jellyauth-overlay h1{margin:0 0 4px;font-size:22px;font-weight:700}' +
      '#jellyauth-overlay .ja-sub{margin:0 0 20px;color:' + COR_SECUNDARIA + ';font-size:14px;line-height:1.5}' +
      '#jellyauth-overlay .ja-campo{margin:0 0 14px}' +
      '#jellyauth-overlay label{display:block;margin:0 0 5px;font-size:13px;color:' + COR_SECUNDARIA + '}' +
      '#jellyauth-overlay input{box-sizing:border-box;width:100%;padding:12px;border-radius:10px;border:1px solid rgba(255,255,255,.15);' +
      'background:rgba(0,0,0,.25);color:' + COR_TEXTO + ';font:400 15px system-ui,sans-serif}' +
      '#jellyauth-overlay input:focus{outline:none;border-color:' + COR_ACCENT + '}' +
      '#jellyauth-overlay .ja-botao{display:block;width:100%;padding:13px;border-radius:12px;font:600 15px system-ui,sans-serif;' +
      'cursor:pointer;border:0;background:' + COR_ACCENT + ';color:#fff}' +
      '#jellyauth-overlay .ja-botao:hover{filter:brightness(1.08)}' +
      '#jellyauth-overlay .ja-botao[disabled]{opacity:.5;cursor:default}' +
      '#jellyauth-overlay .ja-botao-secundario{background:transparent;color:' + COR_SECUNDARIA + ';border:1px solid rgba(255,255,255,.18)}' +
      '#jellyauth-overlay .ja-erro{color:' + COR_ERRO + ';font-size:13px;margin:8px 0;min-height:1em;line-height:1.4}' +
      '#jellyauth-overlay .ja-sucesso{color:' + COR_SUCESSO + ';font-size:14px;margin:10px 0;text-align:center}' +
      '#jellyauth-overlay .ja-codigo{font-size:28px;font-weight:700;letter-spacing:10px;text-align:center}' +
      '#jellyauth-overlay .ja-voltar{background:none;border:0;color:' + COR_SECUNDARIA + ';font:inherit;cursor:pointer;' +
      'padding:0;margin-top:14px;text-decoration:underline}' +
      '#jellyauth-overlay .ja-timer{text-align:center;color:' + COR_SECUNDARIA + ';font-size:13px;margin:8px 0}' +
      '#jellyauth-overlay .ja-captcha{display:flex;justify-content:center;margin:0 0 14px}';
    document.head.appendChild(estilo);
  }

  var CAPTCHA_URLS = {
    CloudflareTurnstile: 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit'
  };

  function captchaLigado() {
    return !!(estado.captchaProvedor && estado.captchaProvedor !== 'Nenhum' && estado.captchaSiteKey);
  }

  function apiCaptchaGlobal() {
    if (estado.captchaProvedor === 'CloudflareTurnstile') return window.turnstile;
    return null;
  }

  function carregarApiCaptcha(aoCarregar) {
    var url = CAPTCHA_URLS[estado.captchaProvedor];
    if (!url) return;
    if (apiCaptchaGlobal()) { aoCarregar(); return; }
    var existente = document.querySelector('script[data-jellyauth-captcha="' + estado.captchaProvedor + '"]');
    if (existente) { existente.addEventListener('load', aoCarregar); return; }
    var s = document.createElement('script');
    s.src = url; s.async = true; s.defer = true;
    s.setAttribute('data-jellyauth-captcha', estado.captchaProvedor);
    s.addEventListener('load', aoCarregar);
    document.head.appendChild(s);
  }

  function montarCaptcha() {
    if (!captchaLigado()) return;
    var alvo = overlay.querySelector('#ja-captcha');
    if (!alvo) return;
    widgetCaptcha = null;
    carregarApiCaptcha(function () {
      try {
        if (estado.captchaProvedor === 'CloudflareTurnstile' && window.turnstile) {
          alvo.innerHTML = '';
          widgetCaptcha = window.turnstile.render(alvo, { sitekey: estado.captchaSiteKey });
        }
      } catch (e) { /* widget indisponível */ }
    });
  }

  function obterTokenCaptcha() {
    try {
      var api = apiCaptchaGlobal();
      if (api && typeof api.getResponse === 'function') {
        return (widgetCaptcha != null ? api.getResponse(widgetCaptcha) : api.getResponse()) || '';
      }
    } catch (e) { /* sem token */ }
    return '';
  }

  function resetarCaptcha() {
    try {
      var api = apiCaptchaGlobal();
      if (api && typeof api.reset === 'function') {
        if (widgetCaptcha != null) api.reset(widgetCaptcha); else api.reset();
      }
    } catch (e) { /* nada a fazer */ }
  }

  function injetarBotaoLogin() {
    if (document.getElementById('jellyauth-criar-conta')) return; // já injetado

    var referencia = document.querySelector('.btnForgotPassword') || document.querySelector('.btnSelectServer');
    if (!referencia) return; // a tela de login ainda não renderizou

    // Usa HTML (não createElement) para o emby-button ser registrado/upgradado como na própria tela de login.
    referencia.insertAdjacentHTML('afterend',
      '<button is="emby-button" type="button" id="jellyauth-criar-conta" class="raised cancel block"><span>Criar conta</span></button>');

    var botao = document.getElementById('jellyauth-criar-conta');
    botao.addEventListener('click', function () { location.hash = ROTA_REGISTRO; });
  }

  function mostrarOverlay() {
    garantirEstilo();
    if (document.title !== TITULO_CADASTRO) document.title = TITULO_CADASTRO;
    if (overlay) {
      if (overlay.style.display === 'none') {
        // Voltou ao cadastro: começa do formulário (não da tela de sucesso nem do código de um cadastro largado) e relê
        // o status, que o admin pode ter mudado com a página aberta (convite, verificação, captcha).
        overlay.style.display = '';
        if (telaAtual !== 'formulario') renderizarCadastro();
        consultarStatus();
      }
      return;
    }
    overlay = document.createElement('div');
    overlay.id = 'jellyauth-overlay';
    document.body.appendChild(overlay);
    renderizarCadastro();
    // A página pode estar aberta há horas (a TV na tela de login): relê o status ao abrir o cadastro.
    consultarStatus();
  }

  function esconderOverlay() {
    if (overlay) overlay.style.display = 'none';
    // Devolve o título só se ainda for o nosso (a tela seguinte do Jellyfin pode já ter posto o dela).
    if (document.title === TITULO_CADASTRO) document.title = tituloForaDoCadastro;
    else if (document.title && !naRotaRegistro()) tituloForaDoCadastro = document.title;
  }

  function limparOverlay() { overlay.innerHTML = ''; }

  function erroNoCampo(campo, mensagem, neutra) {
    var erro = overlay.querySelector('#ja-erro');
    if (erro) {
      erro.textContent = mensagem;
      erro.style.color = neutra ? COR_SECUNDARIA : '';
    }
    if (campo) campo.focus();
  }

  function configuracaoDoFormulario() {
    return [estado.exigirVerificacao, estado.exigirSenhaForte, estado.captchaProvedor, estado.captchaSiteKey, estado.exigirConvite].join('|');
  }

  function renderizarCadastro() {
    telaAtual = 'formulario';
    formularioMontadoCom = configuracaoDoFormulario();
    pararTimer();
    limparOverlay();
    var sub = estado.exigirVerificacao
      ? 'Preencha seus dados. Enviaremos um código de verificação para o seu e-mail.'
      : 'Preencha seus dados para criar sua conta.';
    var botao = estado.exigirVerificacao ? 'Enviar código' : 'Criar conta';
    var dicaSenha = estado.exigirSenhaForte ? 'Senha (mínimo 8 caracteres, com letras e números)' : 'Senha (mínimo 8 caracteres)';
    overlay.appendChild(montarCartao(
      '<h1>Criar conta</h1>' +
      '<p class="ja-sub">' + sub + '</p>' +
      (estado.exigirConvite
        ? '<div class="ja-campo"><label for="ja-convite">Código de convite</label>' +
          '<input id="ja-convite" type="text" autocomplete="off" autocapitalize="characters" spellcheck="false" maxlength="40" placeholder="XXXX-XXXX-XXXX"></div>'
        : '') +
      '<div class="ja-campo"><label for="ja-username">Nome de usuário</label>' +
      '<input id="ja-username" type="text" autocomplete="username" maxlength="255" autofocus></div>' +
      '<div class="ja-campo"><label for="ja-email">E-mail</label>' +
      '<input id="ja-email" type="email" autocomplete="email" maxlength="200"></div>' +
      '<div class="ja-campo"><label for="ja-senha">' + dicaSenha + '</label>' +
      '<input id="ja-senha" type="password" autocomplete="new-password"></div>' +
      '<div class="ja-campo"><label for="ja-senha2">Confirmar senha</label>' +
      '<input id="ja-senha2" type="password" autocomplete="new-password"></div>' +
      '<div class="ja-captcha" id="ja-captcha"></div>' +
      '<div class="ja-erro" id="ja-erro"></div>' +
      '<button class="ja-botao" id="ja-enviar" type="button">' + botao + '</button>' +
      '<div style="text-align:center"><button class="ja-voltar" type="button" id="ja-voltar">Voltar para o login</button></div>'
    ));

    var campoConvite = overlay.querySelector('#ja-convite');
    if (campoConvite) campoConvite.value = lerSessao(CHAVE_CONVITE);
    overlay.querySelector('#ja-voltar').addEventListener('click', function () { location.hash = ROTA_LOGIN; });
    overlay.querySelector('#ja-enviar').addEventListener('click', enviarSolicitacao);
    overlay.querySelector('#ja-senha2').addEventListener('keydown', function (e) {
      if (e.key === 'Enter') enviarSolicitacao();
    });

    montarCaptcha();
  }

  function lerFormulario() {
    var username = overlay.querySelector('#ja-username').value.trim();
    var email = overlay.querySelector('#ja-email').value.trim();
    var senha = overlay.querySelector('#ja-senha').value;
    var senha2 = overlay.querySelector('#ja-senha2').value;
    var campoConvite = overlay.querySelector('#ja-convite');
    var convite = campoConvite ? campoConvite.value.trim().toUpperCase() : '';
    return { username: username, email: email, senha: senha, senha2: senha2, convite: convite };
  }

  function validarFormulario(dados) {
    if (estado.exigirConvite && !dados.convite) return 'Informe o código do convite.';
    if (!dados.username) return 'Informe um nome de usuário.';
    if (!/^(?!\s)[\w\ \-'._@+]+(?<!\s)$/.test(dados.username) || dados.username === '.' || dados.username === '..') {
      return 'Nome de usuário inválido. Use letras, números, hífen (-), sublinhado (_), apóstrofo (\'), ponto (.) ou arroba (@).';
    }
    if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(dados.email)) return 'Informe um e-mail válido.';
    if (dados.senha.length < 8) return 'A senha deve ter pelo menos 8 caracteres.';
    if (estado.exigirSenhaForte && (!/[a-zA-Z]/.test(dados.senha) || !/[0-9]/.test(dados.senha))) return 'A senha deve conter letras e números.';
    if (dados.senha !== dados.senha2) return 'As senhas não conferem.';
    return null;
  }

  function enviarSolicitacao() {
    var dados = lerFormulario();
    var erro = validarFormulario(dados);
    if (erro) { erroNoCampo(null, erro); return; }

    var tokenCaptcha = '';
    if (captchaLigado()) {
      tokenCaptcha = obterTokenCaptcha();
      if (!tokenCaptcha) { erroNoCampo(null, 'Confirme que você não é um robô.'); return; }
    }

    var botaoEnviar = overlay.querySelector('#ja-enviar');
    botaoEnviar.disabled = true;
    erroNoCampo(null, '');

    fetch(BASE + '/JellyAuth/Request', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ Username: dados.username, Email: dados.email, Password: dados.senha, Convite: dados.convite || null, CaptchaToken: tokenCaptcha })
    })
      .then(lerResposta)
      .catch(function () { return { ok: false, status: 0, corpo: { Mensagem: 'Falha de rede.' } }; })
      .then(function (res) {
        botaoEnviar.disabled = false;
        if (res.ok) {
          dadosFormulario = dados;
          if (res.corpo && res.corpo.criado === true) {
            renderizarSucesso();
          } else {
            renderizarVerificacao(dados.email);
          }
        } else {
          resetarCaptcha();
          erroNoCampo(null, (res.corpo && res.corpo.Mensagem) || 'Não foi possível concluir o cadastro.');
        }
      });
  }

  function renderizarVerificacao(email) {
    telaAtual = 'verificacao';
    limparOverlay();
    overlay.appendChild(montarCartao(
      '<h1>Verifique seu e-mail</h1>' +
      '<p class="ja-sub">Enviamos um código de 6 dígitos para <strong>' + escaparHtml(email) + '</strong>. Digite-o abaixo para concluir o cadastro.</p>' +
      '<div class="ja-campo"><label for="ja-codigo">Código de verificação</label>' +
      '<input id="ja-codigo" type="text" inputmode="numeric" autocomplete="one-time-code" maxlength="6" class="ja-codigo" autofocus></div>' +
      '<div class="ja-erro" id="ja-erro"></div>' +
      '<button class="ja-botao" id="ja-confirmar" type="button">Confirmar cadastro</button>' +
      '<div class="ja-timer" id="ja-timer"></div>' +
      '<button class="ja-botao ja-botao-secundario" id="ja-reenviar" type="button" style="margin-bottom:10px">Reenviar código</button>' +
      '<div style="text-align:center"><button class="ja-voltar" type="button" id="ja-voltar">Voltar para o login</button></div>'
    ));

    var campoCodigo = overlay.querySelector('#ja-codigo');
    campoCodigo.addEventListener('input', function () { campoCodigo.value = campoCodigo.value.replace(/\D/g, '').slice(0, 6); });

    overlay.querySelector('#ja-confirmar').addEventListener('click', enviarVerificacao);
    campoCodigo.addEventListener('keydown', function (e) { if (e.key === 'Enter') enviarVerificacao(); });
    overlay.querySelector('#ja-voltar').addEventListener('click', function () { location.hash = ROTA_LOGIN; });
    overlay.querySelector('#ja-reenviar').addEventListener('click', reenviarCodigo);

    iniciarTimerReenvio(estado.cooldownReenvio);
  }

  function enviarVerificacao() {
    var codigo = overlay.querySelector('#ja-codigo').value.trim();
    if (codigo.length !== 6) { erroNoCampo(null, 'Digite o código de 6 dígitos.'); return; }

    var botaoConfirmar = overlay.querySelector('#ja-confirmar');
    botaoConfirmar.disabled = true;
    erroNoCampo(null, '');

    fetch(BASE + '/JellyAuth/Verify', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ Email: dadosFormulario.email, Code: codigo })
    })
      .then(lerResposta)
      .catch(function () { return { ok: false, status: 0, corpo: { Mensagem: 'Falha de rede.' } }; })
      .then(function (res) {
        botaoConfirmar.disabled = false;
        if (res.ok) {
          renderizarSucesso();
        } else {
          erroNoCampo(null, (res.corpo && res.corpo.Mensagem) || 'Código inválido.');
        }
      });
  }

  function renderizarSucesso() {
    // Conta criada: o convite já foi usado.
    if (dadosFormulario && dadosFormulario.convite) gravarSessao(CHAVE_CONVITE_USADO, normalizarConvite(dadosFormulario.convite));
    gravarSessao(CHAVE_CONVITE, '');
    telaAtual = 'sucesso';
    limparOverlay();
    pararTimer();
    overlay.appendChild(montarCartao(
      '<h1>Conta criada!</h1>' +
      '<p class="ja-sub">Seu cadastro foi confirmado. Agora você pode entrar com seu usuário e senha.</p>' +
      '<div class="ja-sucesso">Tudo pronto!</div>' +
      '<button class="ja-botao" id="ja-ir-login" type="button">Ir para o login</button>'
    ));
    overlay.querySelector('#ja-ir-login').addEventListener('click', function () {
      location.hash = ROTA_LOGIN;
      location.reload();
    });
  }

  function reenviarCodigo() {
    var botaoReenviar = overlay.querySelector('#ja-reenviar');
    botaoReenviar.disabled = true;
    erroNoCampo(null, '');

    fetch(BASE + '/JellyAuth/Resend', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ Email: dadosFormulario.email })
    })
      .then(lerResposta)
      .catch(function () { return { ok: false, status: 0, corpo: { Mensagem: 'Falha de rede.' } }; })
      .then(function (res) {
        if (res.ok) {
          // O servidor responde igual haja ou não cadastro aguardando (não revela se o e-mail existe).
          erroNoCampo(null, 'Se o cadastro ainda estiver aguardando a confirmação, enviamos um código novo.', true);
          iniciarTimerReenvio(estado.cooldownReenvio);
        } else {
          erroNoCampo(null, (res.corpo && res.corpo.Mensagem) || 'Não foi possível reenviar.');
          botaoReenviar.disabled = false;
        }
      });
  }

  function iniciarTimerReenvio(segundos) {
    pararTimer();
    var restante = segundos;
    var botaoReenviar = overlay.querySelector('#ja-reenviar');
    var timerEl = overlay.querySelector('#ja-timer');
    if (botaoReenviar) botaoReenviar.disabled = true;

    function tique() {
      if (!timerEl || !botaoReenviar) { pararTimer(); return; }
      if (restante <= 0) {
        timerEl.textContent = '';
        botaoReenviar.disabled = false;
        pararTimer();
        return;
      }
      timerEl.textContent = 'Reenviar disponível em ' + restante + 's';
      restante--;
    }

    tique();
    temporizadorReenvio = setInterval(tique, 1000);
  }

  function pararTimer() {
    if (temporizadorReenvio) { clearInterval(temporizadorReenvio); temporizadorReenvio = null; }
  }

  /** Lê a resposta mesmo quando o corpo não é JSON (erro do servidor ou de um proxy), sem confundir com falha de rede. */
  function lerResposta(r) {
    return r.text().then(function (texto) {
      var corpo = null;
      try { corpo = texto ? JSON.parse(texto) : null; } catch (e) { corpo = null; }
      if (!corpo || typeof corpo !== 'object') {
        corpo = r.ok ? {} : { Mensagem: 'O servidor não conseguiu atender agora (erro ' + r.status + '). Tente de novo em instantes.' };
      }
      return { ok: r.ok, status: r.status, corpo: corpo };
    });
  }

  function montarCartao(htmlInterno) {
    var cartao = document.createElement('div');
    cartao.className = 'ja-cartao';
    cartao.innerHTML = htmlInterno;
    return cartao;
  }

  function escaparHtml(texto) {
    return String(texto).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  // Garante que o botão não fique órfão se o body ainda não existir no momento da injeção.
  function iniciar() {
    if (document.body) {
      consultarStatus();
    } else {
      document.addEventListener('DOMContentLoaded', consultarStatus);
    }
  }

  iniciar();
})();

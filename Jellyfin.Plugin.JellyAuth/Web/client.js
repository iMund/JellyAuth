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

  // Cores/visual do tema escuro do Jellyfin (veja "04 - Frontend & UI/Componentes e CSS Variables.md").
  var COR_ACCENT = '#00a4dc';
  var COR_FUNDO = '#101010';
  var COR_CARTAO = '#1c2126';
  var COR_TEXTO = '#eef2f5';
  var COR_SECUNDARIA = '#aab6c0';
  var COR_ERRO = '#f2555a';
  var COR_SUCESSO = '#3ecf8e';

  var estado = { habilitado: false, exigirVerificacao: true, cooldownReenvio: 60, exigirSenhaForte: true, consultado: false, logado: false };
  var overlay = null;
  var temporizadorReenvio = null;
  var dadosFormulario = null;

  function naRotaRegistro() {
    var h = location.hash || '';
    return h === ROTA_REGISTRO || h === ROTA_REGISTRO + '/';
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
        estado.consultado = true;
        atualizarInterface();
      });
  }

  function atualizarInterface() {
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
      '#jellyauth-overlay .ja-timer{text-align:center;color:' + COR_SECUNDARIA + ';font-size:13px;margin:8px 0}';
    document.head.appendChild(estilo);
  }

  function injetarBotaoLogin() {
    if (document.getElementById('jellyauth-criar-conta')) return; // já injetado

    var referencia = document.querySelector('.btnForgotPassword');
    if (!referencia) return; // a tela de login ainda não renderizou

    var botao = document.createElement('button', { is: 'emby-button' });
    botao.id = 'jellyauth-criar-conta';
    botao.type = 'button';
    botao.className = 'raised cancel block';
    var rotulo = document.createElement('span');
    rotulo.textContent = 'Criar conta';
    botao.appendChild(rotulo);
    botao.addEventListener('click', function () { location.hash = ROTA_REGISTRO; });

    referencia.parentNode.insertBefore(botao, referencia.nextSibling);
  }

  function mostrarOverlay() {
    garantirEstilo();
    if (overlay) {
      overlay.style.display = '';
      return;
    }
    overlay = document.createElement('div');
    overlay.id = 'jellyauth-overlay';
    document.body.appendChild(overlay);
    renderizarCadastro();
  }

  function esconderOverlay() {
    if (overlay) overlay.style.display = 'none';
  }

  function limparOverlay() { overlay.innerHTML = ''; }

  function erroNoCampo(campo, mensagem) {
    var erro = overlay.querySelector('#ja-erro');
    if (erro) erro.textContent = mensagem;
    if (campo) campo.focus();
  }

  function renderizarCadastro() {
    limparOverlay();
    var sub = estado.exigirVerificacao
      ? 'Preencha seus dados. Enviaremos um código de verificação para o seu e-mail.'
      : 'Preencha seus dados para criar sua conta.';
    var botao = estado.exigirVerificacao ? 'Enviar código' : 'Criar conta';
    var dicaSenha = estado.exigirSenhaForte ? 'Senha (mínimo 8 caracteres, com letras e números)' : 'Senha (mínimo 8 caracteres)';
    overlay.appendChild(montarCartao(
      '<h1>Criar conta</h1>' +
      '<p class="ja-sub">' + sub + '</p>' +
      '<div class="ja-campo"><label for="ja-username">Nome de usuário</label>' +
      '<input id="ja-username" type="text" autocomplete="username" maxlength="255" autofocus></div>' +
      '<div class="ja-campo"><label for="ja-email">E-mail</label>' +
      '<input id="ja-email" type="email" autocomplete="email" maxlength="200"></div>' +
      '<div class="ja-campo"><label for="ja-senha">' + dicaSenha + '</label>' +
      '<input id="ja-senha" type="password" autocomplete="new-password"></div>' +
      '<div class="ja-campo"><label for="ja-senha2">Confirmar senha</label>' +
      '<input id="ja-senha2" type="password" autocomplete="new-password"></div>' +
      '<div class="ja-erro" id="ja-erro"></div>' +
      '<button class="ja-botao" id="ja-enviar" type="button">' + botao + '</button>' +
      '<div style="text-align:center"><button class="ja-voltar" type="button" id="ja-voltar">Voltar para o login</button></div>'
    ));

    overlay.querySelector('#ja-voltar').addEventListener('click', function () { location.hash = ROTA_LOGIN; });
    overlay.querySelector('#ja-enviar').addEventListener('click', enviarSolicitacao);
    overlay.querySelector('#ja-senha2').addEventListener('keydown', function (e) {
      if (e.key === 'Enter') enviarSolicitacao();
    });
  }

  function lerFormulario() {
    var username = overlay.querySelector('#ja-username').value.trim();
    var email = overlay.querySelector('#ja-email').value.trim();
    var senha = overlay.querySelector('#ja-senha').value;
    var senha2 = overlay.querySelector('#ja-senha2').value;
    return { username: username, email: email, senha: senha, senha2: senha2 };
  }

  function validarFormulario(dados) {
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

    var botaoEnviar = overlay.querySelector('#ja-enviar');
    botaoEnviar.disabled = true;
    erroNoCampo(null, '');

    fetch(BASE + '/JellyAuth/Request', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ Username: dados.username, Email: dados.email, Password: dados.senha })
    })
      .then(function (r) { return r.json().then(function (corpo) { return { ok: r.ok, status: r.status, corpo: corpo }; }); })
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
          erroNoCampo(null, (res.corpo && res.corpo.Mensagem) || 'Não foi possível concluir o cadastro.');
        }
      });
  }

  function renderizarVerificacao(email) {
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
      .then(function (r) { return r.json().then(function (corpo) { return { ok: r.ok, status: r.status, corpo: corpo }; }); })
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
      .then(function (r) { return r.json().then(function (corpo) { return { ok: r.ok, status: r.status, corpo: corpo }; }); })
      .catch(function () { return { ok: false, status: 0, corpo: { Mensagem: 'Falha de rede.' } }; })
      .then(function (res) {
        if (res.ok) {
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

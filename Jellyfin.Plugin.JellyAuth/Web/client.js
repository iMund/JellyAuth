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

  // O visual vem do tema do Jellyfin (padrão ou um tema instalado pelo admin, como o ElegantFin): o formulário usa as
  // classes nativas (emby-input, button-submit, cancel, sectionTitle) e copia o fundo e o cartão da tela de login.
  // Só as cores de erro e de sucesso são nossas.
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
  // Quando a tela do código foi aberta: depois do prazo do código ela não é retomada (num aparelho compartilhado, a
  // próxima pessoa não vê o e-mail de quem começou o cadastro antes).
  var verificacaoAbertaEm = 0;
  var captchaMontadoCom = '';
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
      .then(function (r) { return r.ok ? r.json() : null; })
      .catch(function () { return null; })
      .then(function (s) {
        if (!s) {
          // Falhou (rede, servidor reiniciando): numa releitura fica tudo como estava; só na primeira leitura o cadastro
          // fica desligado, porque não há o que mostrar sem a configuração.
          if (estado.consultado) return;
          s = { Habilitado: false };
        }
        estado.habilitado = !!(s && s.Habilitado);
        estado.exigirVerificacao = !(s && s.ExigirVerificacaoEmail === false);
        estado.cooldownReenvio = (s && s.MinimoSegundosReenvio > 0) ? s.MinimoSegundosReenvio : 60;
        estado.exigirSenhaForte = !(s && s.ExigirSenhaForte === false);
        estado.captchaProvedor = (s && s.CaptchaProvedor) || 'Nenhum';
        estado.captchaSiteKey = (s && s.CaptchaSiteKey) || '';
        estado.exigirConvite = !!(s && s.ExigirConvite);
        estado.minutosCodigo = (s && s.MinutosExpiracaoCodigo > 0) ? s.MinutosExpiracaoCodigo : 15;
        estado.consultado = true;
        // Formulário na tela montado com outra configuração (o admin mudou algo): monta de novo, antes de a pessoa digitar.
        if (overlay && overlay.style.display !== 'none' && telaAtual === 'formulario' && formularioMontadoCom !== configuracaoDoFormulario()) {
          remontarFormularioMantendoDados();
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
    // Só estrutura: cores, fontes, campos e botões vêm do tema (classes nativas) e de aplicarVisualDoLogin().
    estilo.textContent =
      '#jellyauth-overlay{position:fixed;inset:0;z-index:10000;display:flex;align-items:center;justify-content:center;' +
      'padding:4em 1em;box-sizing:border-box;overflow:auto;background-position:center;background-size:cover;background-repeat:no-repeat}' +
      '#jellyauth-overlay .ja-cartao{width:100%;max-width:26em;box-sizing:border-box;margin:auto}' +
      '#jellyauth-overlay .ja-cartao .sectionTitle{margin:0 0 .3em;text-align:center}' +
      '#jellyauth-overlay .ja-sub{margin:0 0 1.5em;opacity:.8;line-height:1.5;text-align:center}' +
      '#jellyauth-overlay .inputContainer{margin-bottom:1.2em}' +
      '#jellyauth-overlay .ja-erro{color:' + COR_ERRO + ';margin:.5em 0;min-height:1em;line-height:1.4}' +
      '#jellyauth-overlay .ja-sucesso{color:' + COR_SUCESSO + ';margin:.8em 0;text-align:center}' +
      '#jellyauth-overlay .ja-codigo{font-size:1.8em;letter-spacing:.5em;text-align:center}' +
      '#jellyauth-overlay .ja-timer{text-align:center;opacity:.75;margin:.5em 0}' +
      '#jellyauth-overlay .ja-captcha{display:flex;justify-content:center;margin:0 0 1em}' +
      '#jellyauth-overlay .ja-cartao .emby-button{margin:.5em 0 0}';
    document.head.appendChild(estilo);
  }

  // Tela de abertura do Jellyfin (colagem de pôsteres) ligada no painel: vai por baixo do cadastro, como no login.
  var telaDeAberturaLigada = false;
  function consultarTelaDeAbertura() {
    return fetch(BASE + '/Branding/Configuration')
      .then(function (r) { return r.ok ? r.json() : null; })
      .catch(function () { return null; })
      .then(function (b) {
        telaDeAberturaLigada = !!(b && b.SplashscreenEnabled);
        if (overlay && overlay.style.display !== 'none') aplicarVisualDoLogin();
      });
  }

  /** Aplica a opacidade do elemento ao alfa da cor (rgba(r, g, b, a) → rgba(r, g, b, a × opacidade)). */
  function comOpacidade(cor, opacidade) {
    var partes = /rgba?\(([^)]+)\)/.exec(cor || '');
    if (!partes) return cor;
    var v = partes[1].split(',').map(function (x) { return parseFloat(x); });
    var alfa = (v.length > 3 ? v[3] : 1) * (isNaN(opacidade) ? 1 : opacidade);
    return 'rgba(' + v[0] + ', ' + v[1] + ', ' + v[2] + ', ' + Math.round(alfa * 1000) / 1000 + ')';
  }

  function corVisivel(cor) { return cor && cor !== 'transparent' && !/rgba\(\s*0\s*,\s*0\s*,\s*0\s*,\s*0\s*\)/.test(cor); }

  /**
   * Mede o visual da tela de login do tema atual com uma cópia vazia e invisível da estrutura dela (temas estilizam
   * pelo #loginPage) e aplica ao cadastro: fundo da página por cima da tela de abertura e o cartão em volta do
   * formulário. A cópia fica na página só durante a medição (nenhum outro script roda nesse meio-tempo).
   */
  function aplicarVisualDoLogin() {
    if (!overlay) return;
    var sonda = document.createElement('div');
    sonda.innerHTML = '<div id="loginPage" class="page standalonePage backdropPage" aria-hidden="true">' +
      '<div class="padded-left padded-right padded-bottom-page margin-auto-y"></div></div>';
    var pagina = sonda.firstChild;
    pagina.style.cssText = 'position:fixed;left:-10000px;top:0;visibility:hidden;pointer-events:none';
    document.body.appendChild(pagina);
    var estiloPagina = getComputedStyle(pagina);
    var estiloCartao = getComputedStyle(pagina.firstChild);
    var fundoPagina = estiloPagina.backgroundImage;
    var corPagina = estiloPagina.backgroundColor;
    var cartao = {
      backgroundColor: estiloCartao.backgroundColor,
      backdropFilter: estiloCartao.backdropFilter,
      webkitBackdropFilter: estiloCartao.webkitBackdropFilter,
      borderRadius: estiloCartao.borderRadius,
      padding: estiloCartao.padding,
      boxShadow: estiloCartao.boxShadow,
      border: estiloCartao.border
    };
    pagina.parentNode.removeChild(pagina);

    // Escurecimento que o Jellyfin (ou o tema) põe sobre a tela de abertura no login. A cópia entra logo antes da camada
    // real (.backgroundContainer), porque temas a estilizam pela posição na página; a opacidade entra na conta.
    var veu = document.createElement('div');
    veu.className = 'backgroundContainer withBackdrop';
    veu.style.cssText = 'left:-10000px;top:0;width:1px;height:1px;visibility:hidden;pointer-events:none';
    var camadaReal = document.querySelector('.backgroundContainer');
    if (camadaReal && camadaReal.parentNode) camadaReal.parentNode.insertBefore(veu, camadaReal);
    else document.body.appendChild(veu);
    var estiloVeu = getComputedStyle(veu);
    var corVeu = comOpacidade(estiloVeu.backgroundColor, parseFloat(estiloVeu.opacity));
    veu.parentNode.removeChild(veu);

    // Cor de base: a da página de login do tema ou a do corpo da página (o Jellyfin padrão é escuro).
    var base = corVisivel(corPagina) ? corPagina
      : corVisivel(getComputedStyle(document.body).backgroundColor) ? getComputedStyle(document.body).backgroundColor
      : corVisivel(getComputedStyle(document.documentElement).backgroundColor) ? getComputedStyle(document.documentElement).backgroundColor
      : '#101010';
    var camadas = [];
    var temFundoDoTema = fundoPagina && fundoPagina !== 'none';
    if (temFundoDoTema) camadas.push(fundoPagina.replace(/,\s*url\(""\)/g, ''));
    if (telaDeAberturaLigada) {
      // Como no login: colagem por baixo, o escurecimento do Jellyfin/tema sobre ela (se houver) e o fundo do tema por cima.
      if (corVisivel(corVeu)) camadas.push('linear-gradient(' + corVeu + ', ' + corVeu + ')');
      camadas.push('url("' + BASE + '/Branding/Splashscreen")');
    }
    overlay.style.backgroundColor = base;
    overlay.style.backgroundImage = camadas.join(', ');

    var cartaoEl = overlay.querySelector('.ja-cartao');
    if (cartaoEl) {
      Object.keys(cartao).forEach(function (propriedade) { cartaoEl.style[propriedade] = cartao[propriedade]; });
    }
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
    // O Jellyfin guarda telas antigas escondidas (voltar do cadastro cria outra tela de login): o botão vai na tela
    // visível, ao lado do "Esqueci a senha" dela, e a presença é conferida só ali.
    var referencia = Array.prototype.filter.call(
      document.querySelectorAll('.btnForgotPassword, .btnSelectServer'),
      function (e) { return e.offsetParent !== null; })[0];
    if (!referencia) return; // a tela de login ainda não renderizou
    if (referencia.parentNode.querySelector('.jellyauth-criar-conta')) return; // já injetado nesta tela

    // Usa HTML (não createElement) para o emby-button ser registrado/upgradado como na própria tela de login.
    referencia.insertAdjacentHTML('afterend',
      '<button is="emby-button" type="button" class="raised cancel block jellyauth-criar-conta"><span>Criar conta</span></button>');

    referencia.parentNode.querySelector('.jellyauth-criar-conta').addEventListener('click', function () { location.hash = ROTA_REGISTRO; });
  }

  function mostrarOverlay() {
    garantirEstilo();
    if (document.title !== TITULO_CADASTRO) document.title = TITULO_CADASTRO;
    if (overlay) {
      if (overlay.style.display === 'none') {
        // Voltou ao cadastro: depois de uma conta criada começa do formulário; na tela do código, retoma (quem quiser
        // outro cadastro usa "Usar outro e-mail"). Relê o status, que o admin pode ter mudado com a página aberta.
        overlay.style.display = '';
        aplicarVisualDoLogin();
        var codigoVencido = telaAtual === 'verificacao' && Date.now() - verificacaoAbertaEm > (estado.minutosCodigo || 15) * 60000;
        if (telaAtual === 'sucesso' || codigoVencido) renderizarCadastro();
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
    consultarTelaDeAbertura();
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
      erro.style.color = neutra ? 'inherit' : '';
      erro.style.opacity = neutra ? '.8' : '';
    }
    if (campo) campo.focus();
  }

  /**
   * A configuração mudou com o formulário na tela. Se o captcha é o mesmo, ajusta só o que mudou (campo de convite,
   * textos), sem recriar o widget — um captcha já resolvido continua valendo. Se o captcha mudou, monta tudo de novo
   * devolvendo aos campos o que a pessoa já tinha digitado.
   */
  function remontarFormularioMantendoDados() {
    if (captchaMontadoCom === estado.captchaProvedor + '|' + estado.captchaSiteKey) {
      ajustarFormulario();
      return;
    }

    var digitado = {};
    Array.prototype.forEach.call(overlay.querySelectorAll('input[id^="ja-"]'), function (campo) { digitado[campo.id] = campo.value; });
    var focado = document.activeElement && document.activeElement.id;
    renderizarCadastro();
    Object.keys(digitado).forEach(function (id) {
      var campo = overlay.querySelector('#' + id);
      if (campo && digitado[id]) campo.value = digitado[id];
    });
    var campoFocado = focado && overlay.querySelector('#' + focado);
    if (campoFocado) campoFocado.focus();
  }

  function subtituloFormulario() {
    return estado.exigirVerificacao
      ? 'Preencha seus dados. Enviaremos um código de verificação para o seu e-mail.'
      : 'Preencha seus dados para criar sua conta.';
  }

  function textoBotaoFormulario() { return estado.exigirVerificacao ? 'Enviar código' : 'Criar conta'; }

  function dicaSenhaFormulario() {
    return estado.exigirSenhaForte ? 'Senha (mínimo 8 caracteres, com letras e números)' : 'Senha (mínimo 8 caracteres)';
  }

  function htmlCampo(id, rotulo, atributos) {
    return '<div class="inputContainer" id="ja-bloco-' + id + '"><label class="inputLabel" for="ja-' + id + '">' + rotulo + '</label>' +
      '<input class="emby-input" id="ja-' + id + '" ' + atributos + '></div>';
  }

  function htmlBotao(id, texto, principal) {
    return '<button class="raised ' + (principal ? 'button-submit' : 'cancel') + ' block emby-button" id="' + id + '" type="button"><span>' + texto + '</span></button>';
  }

  var HTML_CAMPO_CONVITE = htmlCampo('convite', 'Código de convite',
    'type="text" autocomplete="off" autocapitalize="characters" spellcheck="false" maxlength="40" placeholder="XXXX-XXXX-XXXX"');

  /** Ajusta o formulário já montado à configuração atual, sem tocar no captcha nem no que foi digitado. */
  function ajustarFormulario() {
    overlay.querySelector('.ja-sub').textContent = subtituloFormulario();
    overlay.querySelector('#ja-enviar span').textContent = textoBotaoFormulario();
    overlay.querySelector('label[for="ja-senha"]').textContent = dicaSenhaFormulario();
    var blocoConvite = overlay.querySelector('#ja-bloco-convite');
    if (estado.exigirConvite && !blocoConvite) {
      overlay.querySelector('#ja-username').parentNode.insertAdjacentHTML('beforebegin', HTML_CAMPO_CONVITE);
      overlay.querySelector('#ja-convite').value = lerSessao(CHAVE_CONVITE);
    } else if (!estado.exigirConvite && blocoConvite) {
      blocoConvite.parentNode.removeChild(blocoConvite);
    }
    formularioMontadoCom = configuracaoDoFormulario();
  }

  function configuracaoDoFormulario() {
    return [estado.exigirVerificacao, estado.exigirSenhaForte, estado.captchaProvedor, estado.captchaSiteKey, estado.exigirConvite].join('|');
  }

  function renderizarCadastro() {
    telaAtual = 'formulario';
    formularioMontadoCom = configuracaoDoFormulario();
    captchaMontadoCom = estado.captchaProvedor + '|' + estado.captchaSiteKey;
    pararTimer();
    limparOverlay();
    overlay.appendChild(montarCartao(
      '<h1 class="sectionTitle">Criar conta</h1>' +
      '<p class="ja-sub">' + subtituloFormulario() + '</p>' +
      (estado.exigirConvite ? HTML_CAMPO_CONVITE : '') +
      htmlCampo('username', 'Nome de usuário', 'type="text" autocomplete="username" maxlength="255" autofocus') +
      htmlCampo('email', 'E-mail', 'type="email" autocomplete="email" maxlength="200"') +
      htmlCampo('senha', dicaSenhaFormulario(), 'type="password" autocomplete="new-password"') +
      htmlCampo('senha2', 'Confirmar senha', 'type="password" autocomplete="new-password"') +
      '<div class="ja-captcha" id="ja-captcha"></div>' +
      '<div class="ja-erro" id="ja-erro"></div>' +
      htmlBotao('ja-enviar', textoBotaoFormulario(), true) +
      htmlBotao('ja-voltar', 'Voltar para o login', false)
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
    verificacaoAbertaEm = Date.now();
    limparOverlay();
    overlay.appendChild(montarCartao(
      '<h1 class="sectionTitle">Verifique seu e-mail</h1>' +
      '<p class="ja-sub">Enviamos um código de 6 dígitos para <strong>' + escaparHtml(email) + '</strong>. Digite-o abaixo para concluir o cadastro.</p>' +
      '<div class="inputContainer"><label class="inputLabel" for="ja-codigo">Código de verificação</label>' +
      '<input class="emby-input ja-codigo" id="ja-codigo" type="text" inputmode="numeric" autocomplete="one-time-code" maxlength="6" autofocus></div>' +
      '<div class="ja-erro" id="ja-erro"></div>' +
      htmlBotao('ja-confirmar', 'Confirmar cadastro', true) +
      '<div class="ja-timer" id="ja-timer"></div>' +
      htmlBotao('ja-reenviar', 'Reenviar código', false) +
      htmlBotao('ja-recomecar', 'Usar outro e-mail', false) +
      htmlBotao('ja-voltar', 'Voltar para o login', false)
    ));

    var campoCodigo = overlay.querySelector('#ja-codigo');
    campoCodigo.addEventListener('input', function () { campoCodigo.value = campoCodigo.value.replace(/\D/g, '').slice(0, 6); });

    overlay.querySelector('#ja-confirmar').addEventListener('click', enviarVerificacao);
    campoCodigo.addEventListener('keydown', function (e) { if (e.key === 'Enter') enviarVerificacao(); });
    overlay.querySelector('#ja-voltar').addEventListener('click', function () { location.hash = ROTA_LOGIN; });
    overlay.querySelector('#ja-reenviar').addEventListener('click', reenviarCodigo);
    overlay.querySelector('#ja-recomecar').addEventListener('click', renderizarCadastro);

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
      '<h1 class="sectionTitle">Conta criada!</h1>' +
      '<p class="ja-sub">Seu cadastro foi confirmado. Agora você pode entrar com seu usuário e senha.</p>' +
      '<div class="ja-sucesso">Tudo pronto!</div>' +
      htmlBotao('ja-ir-login', 'Ir para o login', true)
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
          // O servidor responde igual haja ou não cadastro aguardando, e antes de enviar (não revela se o e-mail existe).
          erroNoCampo(null, 'Se o cadastro ainda estiver aguardando a confirmação, um código novo chega em instantes. Se não chegar, espere o tempo de reenvio e tente de novo.', true);
          // O tempo de espera pode ter mudado no painel: relê antes de contar.
          consultarStatus().then(function () { iniciarTimerReenvio(estado.cooldownReenvio); });
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
    // Aplica o visual medido depois que o cartão entra na página (o chamador faz o appendChild em seguida).
    setTimeout(aplicarVisualDoLogin, 0);
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

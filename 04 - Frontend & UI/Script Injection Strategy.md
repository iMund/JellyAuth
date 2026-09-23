---
tags:
  - frontend
  - injection
  - javascript
  - jellyauth
created: 2026-09-23
---

# Script Injection Strategy

Como o `client.js` entra na SPA do Jellyfin e implementa a tela de cadastro e a verificação.

## 1. Entrega do script

Ver [[ADR-001 - Injeção de JS no Jellyfin Web]] para a decisão arquitetural. Resumo:

1. `InjetorScriptWeb` (middleware) injeta no `index.html`:
   ```html
   <script defer src="../JellyAuth/client.js?v=<hash12></script><!-- jellyauth -->
   ```
2. `GET /JellyAuth/client.js` serve o conteúdo a partir de recurso embutido na DLL (`ScriptWeb.Conteudo`), com `Cache-Control: no-cache`.

O `<hash>` é o SHA-256 do script (12 hex), substituído no marcador `__VERSAO_SCRIPT__` em tempo de carga (`ScriptWeb.cs`).

## 2. Inicialização no navegador

O `client.js` (IIFE, ES5, `'use strict'`, guarda `window.__jellyAuth`):

1. Calcula a `BASE` a partir do `src` do próprio `<script>` (funciona com base path customizada).
2. Chama `GET /JellyAuth/Status` → `{ Habilitado: true|false }`.
3. Se habilitado, observa a rota e o login para decidir o que exibir.

## 3. Botão "Criar conta" na tela de login

- Um botão flutuante `#jellyauth-botao` (fixo, centralizado embaixo) é mostrado **quando não há sessão ativa** e o usuário não está em `#/register`.
- A detecção de login usa `window.ApiClient.accessToken()` (com fallback para `localStorage.jellyfin_credentials`).
- Clicar leva a `location.hash = '#/register'`.
- A lógica roda num `setInterval` (1s) + listener de `hashchange`, então reaparece/some conforme o usuário entra/sai.

> Estratégia de "botão flutuante" escolhida por robustez: a tela de login é React/MUI e muda entre versões; ancorar em IDs internos seria frágil.

## 4. Rota `#/register` (overlay)

- O listener de `hashchange` detecta `#/register` e monta um **overlay full-screen** `#jellyauth-overlay`.
- O overlay contém o cartão `ja-cartao` com o formulário, e é removido quando a rota muda.
- Estados internos do overlay:
  1. **Formulário** (`renderizarCadastro`): username, e-mail, senha, confirmação.
  2. **Verificação** (`renderizarVerificacao`): campo de código de 6 dígitos + botão reenviar + timer regressivo.
  3. **Sucesso** (`renderizarSucesso`): mensagem + botão "Ir para o login".

## 5. Fluxo de chamadas

```text
Formulário ──POST /JellyAuth/Request──▶ ok ──▶ (verificação ligada? renderizarVerificacao : renderizarSucesso)
Verificação ──POST /JellyAuth/Verify──▶ ok ──▶ renderizarSucesso ──▶ #/login + reload
              └─POST /JellyAuth/Resend──▶ novo código + timer
```

- O `GET /JellyAuth/Status` retorna `{ Habilitado, ExigirVerificacaoEmail }`.
- Quando `ExigirVerificacaoEmail = false`, o formulário usa o botão "Criar conta" e, após `Request` (que devolve `criado: true`), vai direto à tela de sucesso — sem etapa de código.
- A validação **client-side** espelha a do servidor (regex do username, e-mail, senha ≥ 8, confirmação igual).
- O campo de código aceita só dígitos (`inputmode="numeric"`, filtro `\D`), máx. 6.
- O timer de reenvio começa em 60s; ao zerar, habilita o botão "Reenviar código".

## 6. Robustez

| Cuidado | Implementação |
|---|---|
| Ordem de carga (script antes do `body`) | `iniciar()` usa `DOMContentLoaded` se `document.body` ainda não existe |
| SPA que re-renderiza a tela de login | overlay/botão próprios, independentes do DOM interno |
| Navegadores/cache | `?v=<hash>` + `no-cache` |
| Apps nativos que embutem a web | IIFE pura, sem dependência de framework |
| XSS no e-mail exibido | `escaparHtml()` antes de injetar no HTML |

> Arquivos: `Web/client.js` (script), `Web/ScriptWeb.cs` (carga + versionamento), `Servicos/InjetorScriptWeb.cs` (middleware).

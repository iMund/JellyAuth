---
tags:
  - frontend
  - css
  - tema
  - jellyauth
created: 2026-09-23
---

# Componentes e CSS Variables

Tokens visuais e estrutura dos componentes usados no `client.js` e no `painel.html`.

## Tema do Jellyfin e por que usamos cores fixas

O Jellyfin Web (10.9+) aplica o tema via **MUI em tempo de execução** — as cores do tema escuro não estão disponíveis como CSS custom properties estáticas nos arquivos `.css` servidos. Por isso, o plugin adota **tokens fixos** que casam com o tema escuro padrão (mesma abordagem do JellyPix).

## Paleta (tema escuro)

| Token (JS) | Valor | Uso |
|---|---|---|
| `COR_FUNDO` | `#101010` | fundo do overlay (tela inteira) |
| `COR_CARTAO` | `#1c2126` | cartão do formulário |
| `COR_TEXTO` | `#eef2f5` | texto principal |
| `COR_SECUNDARIA` | `#aab6c0` | rótulos, dicas, textos de apoio |
| `COR_ACCENT` | `#00a4dc` | botões primários, foco (azul Jellyfin) |
| `COR_ERRO` | `#f2555a` | mensagens de erro |
| `COR_SUCESSO` | `#3ecf8e` | mensagem de sucesso |

No `painel.html`, os valores são aplicados direto nos estilos (o painel já herda o CSS do dashboard do Jellyfin, então só os elementos próprios usam os tokens).

## Componentes do `client.js`

| ID | Descrição |
|---|---|
| `#jellyauth-botao` | botão flutuante "Criar conta" (visível sem sessão) |
| `#jellyauth-overlay` | container full-screen do cadastro |
| `.ja-cartao` | cartão central (max-width 420px, borda arredondada) |
| `.ja-campo` | grupo rótulo + input |
| `.ja-botao` / `.ja-botao-secundario` | botões primário/secundário |
| `.ja-erro` / `.ja-sucesso` | área de mensagens |
| `.ja-codigo` | input do código (letter-spacing 10px) |
| `.ja-timer` | contador do reenvio |

## Estrutura dos estados (overlay)

```text
#jellyauth-overlay
└── .ja-cartao
    ├── h1 (título)
    ├── .ja-sub (subtítulo)
    ├── .ja-campo (username)   → #ja-username
    ├── .ja-campo (email)      → #ja-email
    ├── .ja-campo (senha)      → #ja-senha
    ├── .ja-campo (confirma)   → #ja-senha2
    ├── .ja-erro
    ├── .ja-botao              → #ja-enviar
    └── .ja-voltar             → #ja-voltar
```

Estado de verificação troca o conteúdo do cartão para `#ja-codigo`, `#ja-confirmar`, `#ja-reenviar`, `#ja-timer`.

## Acessibilidade e consistência

- Fontes `system-ui, sans-serif` (mesma família usada pelo Jellyfin e pelo JellyPix).
- `input:focus` com borda `COR_ACCENT` (consistente com os campos do dashboard).
- `autocomplete` correto (`username`, `email`, `new-password`, `one-time-code`) para gerenciadores de senha.
- `inputmode="numeric"` no campo de código para teclado numérico em mobile.
- Overlay usa `inset:0` + `overflow:auto`, cobrindo a SPA inteira e permitindo rolagem em telas baixas.

## Painel admin (`painel.html`)

O painel herda o CSS do dashboard (classes `page`, `pluginConfigurationPage`, `checkboxContainer`, `emby-input`, `emby-button`, `emby-checkbox`). Estilos próprios usam o prefixo `ja-` para evitar colisão.

| Classe | Uso |
|---|---|
| `.ja-abas` / `.ja-aba` | abas (Geral, SMTP, Regras, Segurança) |
| `.ja-bloco` / `.ja-grade` | seções e grade responsiva |
| `.ja-dica` | caixa de dica com borda accent |
| `.ja-barra-salvar` | barra de salvar fixa no rodapé (fundo `var(--ja-fundo)` detectado do tema) |

> A barra de salvar detecta a cor de fundo real do tema no `pageshow` (`aplicarCorDeFundo`) para não ficar transparente sobre conteúdo — mesmo truque do JellyPix.

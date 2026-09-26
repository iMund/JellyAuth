# JellyAuth

Auto-cadastro para o Jellyfin. A pessoa cria a própria conta na tela de login, confirma o e-mail e já entra — sem você ter que criar usuário na mão e sem abrir o registro pra qualquer um.

## Como funciona

Na tela de login aparece um "Criar conta". A pessoa informa usuário, e-mail e senha e recebe um código por e-mail. A conta só é criada depois que ela digita esse código, então ninguém consegue se cadastrar com um e-mail que não é dela.

Dá pra manter isso ligado num servidor público sem virar problema: o código expira, tem um tempo de espera antes de reenviar e tem rate limit por e-mail e por IP.

Se preferir que só entre quem você chamar, liga o **cadastro só com convite**: você gera o convite no painel (quantas contas ele cria e por quantos dias vale) e manda o link ou o código pra pessoa. Ela mesma escolhe usuário, e-mail e senha.

## Instalação

Pega o pacote que combina com o teu Jellyfin na página de [releases](https://github.com/iMund/JellyAuth/releases):

- **10.11.x** → `JellyAuth_1.1.1.0_jellyfin-10.11.11.zip`
- **12.x** → `JellyAuth_1.1.1.0_jellyfin-12.1.zip`

Descompacta numa pasta dentro de `plugins/` (tipo `JellyAuth_1.1.1.0/`) e reinicia o Jellyfin.

## Configuração

Tudo pelo painel do Jellyfin, em **Plugins → JellyAuth**:

- **SMTP** — servidor, porta, usuário, senha e remetente dos e-mails (no Gmail, com senha de app).
- **Captcha** — Cloudflare Turnstile pra segurar bot. É opcional, mas vale deixar ligado.
- **Bibliotecas** — as que o usuário novo já pode ver.
- **Convites** — gerar, revogar e excluir convites, e ver quem se cadastrou com cada um.
- **Limites** — validade do código, cooldown de reenvio e rate limit.

## Compatibilidade

Compila pro Jellyfin 10.11 (net9.0). Com o SDK do .NET 10 instalado, também sai o pacote pro 12 (net10.0).

## Licença

GPL-3.0. O texto completo está no arquivo [LICENSE](LICENSE).

Copyright (C) 2026 Tavares.

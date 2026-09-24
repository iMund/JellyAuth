# JellyAuth

Auto-cadastro para o Jellyfin.

O usuário vai na tela de login, clica em "Criar conta", preenche usuário, e-mail e senha e recebe um código por e-mail. Quando confirma o código, a conta é criada com as permissões que você definir.

Uso o Cloudflare Turnstile pra segurar bot e a configuração é toda pelo painel do Jellyfin: SMTP, captcha, as bibliotecas que o usuário novo pode ver e os limites (validade do código, cooldown de reenvio e rate limit).

Compila pro Jellyfin 10.11 (net9.0). Se tiver o SDK do .NET 10 instalado, também gera o pacote pro 12 (net10.0).

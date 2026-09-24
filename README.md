# JellyAuth

Plugin de auto-cadastro para o Jellyfin. O usuário cria a conta pela tela de login, recebe um código por e-mail e confirma. Tem Cloudflare Turnstile para barrar bots.

Feito para o Jellyfin 10.11 (net9.0). O mesmo código também compila para o Jellyfin 12 (net10.0) quando o SDK do .NET 10 está instalado.

## Como funciona

- Injeta "Criar conta" na tela de login e a tela de cadastro em `#/register` (sem alterar arquivos da pasta web).
- `POST /JellyAuth/Request`: valida os dados, checa duplicidade e envia o código por SMTP.
- `POST /JellyAuth/Verify`: confere o código e cria o usuário via `IUserManager`.
- `POST /JellyAuth/Resend`: reenvia o código, com cooldown.

Toda a configuração fica no painel do Jellyfin, em Plugins → JellyAuth: SMTP, captcha, regras de usuário (download e bibliotecas visíveis) e segurança (rate limit, expiração do código).

## Requisitos

- .NET SDK 9.0 ou superior
- Jellyfin 10.11.x

## Build e deploy

Ajuste os caminhos do servidor no começo do `build-and-deploy.sh` e rode:

```bash
./build-and-deploy.sh
```

Ele compila, gera o `meta.json` e copia a DLL para a pasta de plugins. Depois reinicie o Jellyfin.

## Testes

```bash
cd Jellyfin.Plugin.JellyAuth.Testes
dotnet test
```

## Autor

Higor Tavares

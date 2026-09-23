---
tags:
  - guia
  - smtp
  - email
  - teste
created: 2026-09-23
---

# Como Configurar SMTP e Testar

## 1. Configurar no painel

Painel → Plugins → **JellyAuth** → aba **SMTP**.

| Campo | Exemplo (Gmail) | Observação |
|---|---|---|
| Host | `smtp.gmail.com` | |
| Porta | `587` | 587 = STARTTLS · 465 = SSL implícito |
| Usuário | `voce@gmail.com` | vazio = sem autenticação |
| Senha | *(senha de app)* | **Guardada em arquivo separado** (não aparece na config); preencha só para definir/trocar |
| Habilitar SSL/TLS | ✅ | |
| E-mail do remetente | `voce@gmail.com` | |
| Nome exibido | `Jellyfin` | |
| Assunto | `Seu código de verificação` | |

> **Senha de app do Gmail:** Conta Google → Segurança → Verificação em duas etapas → Senhas de app. Gere uma para "E-mail".

## 2. Ativar o cadastro

Aba **Geral** → marque **"Permitir que novos usuários se cadastrem"** (padrão ligado) e ajuste **"Exigir verificação por e-mail"**.

Aba **Regras de Usuário**:
- **Permitir download de mídia** — desligado por padrão. O acesso às bibliotecas é gerenciado pelo **JellyPix** (novos usuários começam sem bibliotecas).

Aba **Segurança** (padrões):
- **Exigir senha forte** (letras e números) — ligado.
- **Confiar em proxy reverso (X-Forwarded-For)** — ligue se estiver atrás de Nginx/Caddy.
- Expiração do código: `15` min.
- Cooldown de reenvio: `60` s.
- Rate limit: `5` solicitações por e-mail e `30` por IP, em `15` min.

## 3. Testar o envio pelo painel

Na aba SMTP, preencha **"E-mail de teste"** e clique **"Enviar teste"**. O botão chama `POST /JellyAuth/Admin/TestarEmail` e mostra o resultado inline.

## 4. Testar via API (sem navegador)

```bash
# 1. status
curl -s http://localhost:8096/JellyAuth/Status

# 2. solicitar cadastro (gera e envia o código)
curl -s -X POST http://localhost:8096/JellyAuth/Request \
  -H 'Content-Type: application/json' \
  -d '{"Username":"maria","Email":"maria@exemplo.com","Password":"senhaSegura123"}'

# 3. confirmar com o código recebido por e-mail
curl -s -X POST http://localhost:8096/JellyAuth/Verify \
  -H 'Content-Type: application/json' \
  -d '{"Email":"maria@exemplo.com","Code":"123456"}'

# 4. reenviar (se o código expirou)
curl -s -X POST http://localhost:8096/JellyAuth/Resend \
  -H 'Content-Type: application/json' \
  -d '{"Email":"maria@exemplo.com"}'
```

## 5. Testar pela interface web

1. Acesse `http://localhost:8096/web/#/login`.
2. Clique em **"Criar conta"**.
3. Preencha o formulário e aguarde o e-mail.
4. Digite o código de 6 dígitos e confirme.
5. A conta é criada e você é levado ao login.

## Problemas comuns

| Sintoma | Causa provável | Correção |
|---|---|---|
| `503` "SMTP não configurado" | Host/remetente vazios | preencha `SmtpHost` e `RemetenteEmail` |
| `502` ao enviar | credenciais/porta/SSL errados | confira senha de app, porta e `SmtpSsl` |
| `429` "Aguarde Ns antes de reenviar" | cooldown | aguarde o tempo indicado |
| Código "inválido ou expirado" | passou do prazo ou >5 tentativas | use "Reenviar código" |
| Botão não aparece | cadastro desligado | confira aba Geral e `/JellyAuth/Status` |
| E-mail cai no spam | SPF/DKIM do domínio | configure DNS do remetente |

## Nota sobre SSL implícito (porta 465)

`System.Net.Mail.SmtpClient` (usado pelo plugin) negocia STARTTLS nativamente na 587. Na porta 465 (SSL implícito) o suporte pode variar; **prefira 587** com `SmtpSsl` ligado.

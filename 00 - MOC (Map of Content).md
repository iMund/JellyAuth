---
tags:
  - moc
  - jellyauth
  - jellyfin
  - plugin
created: 2026-09-23
status: ativo
---

# 00 — MOC (Map of Content)

> **Segundo Cérebro** do plugin **JellyAuth** para o Jellyfin — auto-cadastro de usuários com verificação por e-mail.

## O que é

Plugin nativo (C#/.NET) para o Jellyfin Server que adiciona um fluxo de **auto-cadastro**:
o visitante preenche usuário/e-mail/senha na interface web, recebe um **código de 6 dígitos** por e-mail (SMTP) e, ao confirmá-lo, o plugin cria o usuário diretamente no banco nativo do Jellyfin (`IUserManager`).

## Mapa de navegação

### 🏗 01 — Arquitetura & ADRs
- [[Arquitetura do Plugin]] — visão geral dos componentes C# e do fluxo.
- [[ADR-001 - Injeção de JS no Jellyfin Web]] — por que injetamos o script no `index.html` em vez de escrever no disco.
- [[ADR-002 - Armazenamento de Códigos Temporários de Verificação]] — por que os códigos ficam em memória e não no disco.

### 🔌 02 — Endpoints & API
- [[API-Request]] — `POST /JellyAuth/Request`
- [[API-Verify]] — `POST /JellyAuth/Verify`
- [[API-Resend]] — `POST /JellyAuth/Resend`

### 🗄 03 — Database & Models
- [[UserVerificationModel]] — modelo `CodigoVerificacao` e o mapeamento e-mail→usuário.

### 🎨 04 — Frontend & UI
- [[Script Injection Strategy]] — como o `client.js` entra na página e intercepta `#/register`.
- [[Componentes e CSS Variables]] — tokens visuais usados no formulário/overlay.

### 🧭 05 — Guides & Setup
- [[Setup-Flatpak-Test-Environment]] — layout das pastas do Jellyfin em Flatpak.
- [[Como Compilar e Empacotar]] — build, deploy e o `build-and-deploy.sh`.
- [[Como Configurar SMTP e Testar]] — passo a passo para configurar e validar o e-mail.

## Fatos rápidos

| Item | Valor |
|---|---|
| Namespace / Assembly | `Jellyfin.Plugin.JellyAuth` |
| GUID do plugin | `6591c9c1-2d2d-463b-b4e0-560fc466024b` |
| Versão | `1.0.0.0` |
| Target frameworks | `net9.0` (Jellyfin 10.11.x) · `net10.0` (Jellyfin 12.x) |
| Rota pública da API | `/JellyAuth/*` |
| Rota do script web | `/JellyAuth/client.js` |
| Rota de cadastro no SPA | `/web/#/register` |

## Decisões-chave (resumo)

1. **Injeção de JS via middleware** (sem escrever na pasta web) — ver [[ADR-001 - Injeção de JS no Jellyfin Web]].
2. **Códigos em memória** (`ConcurrentDictionary`), nunca em disco — ver [[ADR-002 - Armazenamento de Códigos Temporários de Verificação]].
3. **E-mail persistido em JSON próprio** (o `User` nativo do Jellyfin não tem campo de e-mail).
4. **Senha guardada só em memória** até a confirmação do código; depois passa ao `IUserManager.ChangePassword`.
5. **Verificação por e-mail é opcional** (`ExigirVerificacaoEmail`): desligada, a conta é criada na hora, sem SMTP.
6. **Novos usuários nascem sem bibliotecas** (`EnableAllFolders = false`) e **sem download** (`PermitirDownload = false`): o acesso às bibliotecas é gerenciado pelo **JellyPix**, não pelo JellyAuth.
7. **Senha SMTP em arquivo separado** (`ArmazenamentoSegredos`, modo 0600), nunca na configuração XML nem na API de config.
8. **Rate limit por e-mail e por IP** + **poda periódica** (`RotinaLimpeza`) contra abuso e DoS de memória.
9. **Senha forte opcional** (`ExigirSenhaForte`) e **confiança em proxy reverso** (`ConfiarProxy`) para rate limit por IP correto atrás de Nginx/Caddy.

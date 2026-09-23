---
tags:
  - api
  - endpoint
  - jellyauth
created: 2026-09-23
---

# API — `POST /JellyAuth/Request`

Solicita o cadastro. Com **verificação por e-mail** ligada, gera o código de 6 dígitos e envia por SMTP; com **verificação desligada**, cria a conta na hora.

- **Método:** `POST`
- **Rota:** `/JellyAuth/Request`
- **Auth:** anônimo (`[AllowAnonymous]`)
- **Controller:** `ControladorCadastro.Solicitar`

## Corpo da requisição (`PedidoRegistro`)

```json
{
  "Username": "maria",
  "Email": "maria@exemplo.com",
  "Password": "senhaSegura123"
}
```

| Campo | Tipo | Obrigatório | Observações |
|---|---|---|---|
| `Username` | string | sim | Validação estrita do Jellyfin (ver abaixo), máx. 255 |
| `Email` | string | sim | Formato de e-mail válido, máx. 200 (usado para impedir duplicidade) |
| `Password` | string | sim | Mínimo 8 caracteres |

## Regras de validação (ordem)

1. `HabilitarCadastro` desligado → **403**.
2. Username deve casar com o regex do Jellyfin `^(?!\s)[\w\ \-'._@+]+(?<!\s)$` e não pode ser `.`/`..` (máx. 255) → **400**.
3. E-mail válido (`EmailAddressAttribute`) → **400**.
4. Senha com ≥ 8 caracteres; se `ExigirSenhaForte`, também com letras e números → **400**.
5. Usuário ou e-mail já cadastrados (`IUserManager.GetUserByName` / `ArmazenamentoCadastros`) → **400** com mensagem única (anti-enumeração).
6. Rate limit por e-mail **e por IP** (`MaximoTentativasPorEmail` / `MaximoTentativasPorIp`) → **429**.

### Se `ExigirVerificacaoEmail = true`

7. SMTP não configurado → **503**.
8. Gera código (`RandomNumberGenerator`), guarda em RAM e envia via SMTP. Falha de SMTP → **502**.

### Se `ExigirVerificacaoEmail = false`

7. Cria o usuário imediatamente (mesmo caminho do [[API-Verify]]) e retorna `criado: true`.

## Respostas

### 200 OK — aguardando código
```json
{ "sucesso": true, "criado": false }
```

### 200 OK — conta já criada (verificação desligada)
```json
{ "sucesso": true, "criado": true }
```

### 400 / 403 / 429 / 502 / 503 — `RespostaErro`
```json
{ "Mensagem": "Este nome de usuário já está em uso." }
```

| Status | Significado |
|---|---|
| `400` | Dado inválido ou já em uso |
| `403` | Cadastro desativado |
| `429` | Rate limit atingido |
| `502` | Falha ao enviar e-mail |
| `503` | SMTP não configurado |

## Fluxo interno

```
Solicitar → validar → [verificação ligada?]
   ├─ sim → rate limit → CriarCodigo (RAM) → EnviarCodigoAsync (SMTP) → criado=false
   └─ não → CriarUsuarioAsync (IUserManager) → criado=true
```

O campo `criado` diz ao frontend se deve mostrar a etapa do código ou ir direto ao sucesso. O código fica em `ArmazenamentoCodigos` (ver [[ADR-002 - Armazenamento de Códigos Temporários de Verificação]]); o próximo passo com verificação é [[API-Verify]].

> Para reenviar o código, ver [[API-Resend]]. O estado de `ExigirVerificacaoEmail` é exposto em `GET /JellyAuth/Status`.

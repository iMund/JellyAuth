---
tags:
  - api
  - endpoint
  - jellyauth
created: 2026-09-23
---

# API — `POST /JellyAuth/Verify`

Confirma o código recebido por e-mail e **cria o usuário definitivamente** no Jellyfin.

- **Método:** `POST`
- **Rota:** `/JellyAuth/Verify`
- **Auth:** anônimo (`[AllowAnonymous]`)
- **Controller:** `ControladorCadastro.Verificar`

## Corpo da requisição (`PedidoVerificacao`)

```json
{
  "Email": "maria@exemplo.com",
  "Code": "123456"
}
```

| Campo | Tipo | Obrigatório |
|---|---|---|
| `Email` | string | sim |
| `Code` | string | sim (6 dígitos) |

## Comportamento

1. `ArmazenamentoCodigos.Confirmar(email, code)`:
   - compara o código (exato, `StringComparison.Ordinal`);
   - confere a validade temporal (`ExpiraEm` vs `TimeProvider`);
   - limita a 5 tentativas erradas por código (após isso, descarta o pendente);
   - em caso de sucesso, **consome** (remove) o pendente.
2. Se inválido/expirado → **400** com mensagem genérica (não revela se o e-mail existe).
3. Se válido, cria o usuário:
   - `IUserManager.CreateUserAsync(username)`;
   - `IUserManager.ChangePassword(...)` (10.11: `(User, string)`; 12: `(Guid, string)` — ver [[Arquitetura do Plugin]]);
   - aplica as **regras de usuário** da configuração (ver abaixo);
   - `IUserManager.UpdateUserAsync(user)`;
4. Registra o e-mail em `ArmazenamentoCadastros` (JSON).
5. Retorna **200**.

## Regras de usuário aplicadas (`AplicarRegrasDeUsuario`)

| Configuração | Efeito |
|---|---|
| `PermitirDownload` (padrão **desligado**) | `PermissionKind.EnableContentDownloading` |
| — (bibliotecas) | `EnableAllFolders = false` **sempre** — o usuário começa sem nenhuma biblioteca |

O acesso às bibliotecas **não é gerenciado aqui**: outro plugin (JellyPix) libera as bibliotecas conforme a situação de cada usuário. Por isso os novos cadastros já nascem sem bibliotecas (`EnableAllFolders = false`, sem `EnabledFolders`).

## Respostas

### 200 OK — `RespostaCadastro`
```json
{ "Sucesso": true, "Username": "maria@exemplo.com" }
```

### 400 — `RespostaErro`
```json
{ "Mensagem": "Código inválido ou expirado. Peça um novo código." }
```

## Observações de segurança

- Mensagem de erro **única** para código inválido/expirado/inexistente.
- A senha em claro só existia em RAM; após `ChangePassword`, o hash fica no banco nativo do Jellyfin.
- O usuário é criado como **não-admin** (padrões do `CreateUserAsync`).

> O passo anterior é [[API-Request]]; se o código expirou, ver [[API-Resend]].

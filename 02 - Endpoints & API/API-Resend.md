---
tags:
  - api
  - endpoint
  - jellyauth
created: 2026-09-23
---

# API — `POST /JellyAuth/Resend`

Reenvia um novo código para o e-mail, respeitando o **cooldown** configurado.

- **Método:** `POST`
- **Rota:** `/JellyAuth/Resend`
- **Auth:** anônimo (`[AllowAnonymous]`)
- **Controller:** `ControladorCadastro.Reenviar`

## Corpo da requisição (`PedidoVerificacao`)

```json
{
  "Email": "maria@exemplo.com"
}
```

| Campo | Tipo | Obrigatório |
|---|---|---|
| `Email` | string | sim |

> O campo `Code` (presente no `PedidoVerificacao`) é ignorado neste endpoint.

## Comportamento

1. `ArmazenamentoCodigos.Reenviar(email, out aguardar)`:
   - localiza o pendente; se não existir → **400** genérico;
   - calcula `agora - UltimoReenvioEm`:
     - menor que `MinimoSegundosReenvio` → retorna o tempo restante (`aguardar`);
     - caso contrário, gera novo código e renova o prazo de expiração.
2. Se houver cooldown pendente → **429** com o tempo de espera.
3. Envia o novo código via SMTP. Falha de SMTP → **502**.

## Respostas

### 200 OK
```json
{ "sucesso": true }
```

### 429 — cooldown ativo
```json
{ "Mensagem": "Aguarde 42s antes de reenviar." }
```

### 400 — sem cadastro pendente
```json
{ "Mensagem": "Nenhum cadastro pendente. Inicie o cadastro novamente." }
```

## Parâmetros de configuração envolvidos

| Configuração | Default | Descrição |
|---|---|---|
| `MinimoSegundosReenvio` | 60 | Cooldown mínimo entre reenvios |
| `MinutosExpiracaoCodigo` | 15 | Renovado a cada reenvio |

## Fluxo interno

```
Reenviar → checa cooldown → gera novo código → renova expiração → EnviarCodigoAsync
```

> Relacionado: [[API-Request]] (envio inicial) e [[API-Verify]] (confirmação). O frontend usa este endpoint no botão "Reenviar código" com timer regressivo (ver [[Script Injection Strategy]]).

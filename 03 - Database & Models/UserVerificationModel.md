---
tags:
  - modelo
  - dados
  - jellyauth
created: 2026-09-23
---

# UserVerificationModel

Modelos de dados do fluxo de verificação por e-mail.

## `CodigoVerificacao`

`Jellyfin.Plugin.JellyAuth/Dados/CodigoVerificacao.cs`

Cadastro pendente guardado **somente em memória** (ver [[ADR-002 - Armazenamento de Códigos Temporários de Verificação]]).

```csharp
public sealed class CodigoVerificacao
{
    public required string Email { get; init; }       // chave do dicionário (OrdinalIgnoreCase)
    public required string Username { get; init; }
    public required string Password { get; init; }    // em claro, apenas em RAM
    public required string Codigo { get; set; }        // 6 dígitos numéricos
    public DateTime CriadoEm { get; set; }
    public DateTime ExpiraEm { get; set; }             // CriadoEm + MinutosExpiracaoCodigo
    public int TentativasVerificacao { get; set; }     // máx. 5; excedeu → descartado
    public DateTime UltimoReenvioEm { get; set; }      // cooldown de reenvio (JsonIgnore)
}
```

| Campo | Tipo | Mutável? | Notas |
|---|---|---|---|
| `Email` | string | não (`init`) | chave, comparação case-insensitive |
| `Username` | string | não | validado antes de criar |
| `Password` | string | não | em claro só em RAM, consumida no `ChangePassword` |
| `Codigo` | string | sim | renovado a cada reenvio |
| `CriadoEm` / `ExpiraEm` | DateTime | sim | expiração renovada no reenvio |
| `TentativasVerificacao` | int | sim | proteção contra força bruta |
| `UltimoReenvioEm` | DateTime | sim | marcado `[JsonIgnore]` por ser volátil |

## `CadastroConcluido`

`Jellyfin.Plugin.JellyAuth/Dados/ArmazenamentoCadastros.cs`

Mapeamento persistido **e-mail → usuário**, gravado em `plugins/configurations/JellyAuth.cadastros.json` (o `User` nativo do Jellyfin **não tem campo de e-mail**).

```csharp
public sealed class CadastroConcluido
{
    public string Email { get; set; }
    public string Username { get; set; }
    public Guid IdUsuario { get; set; }
    public DateTime DataCadastro { get; set; }
}
```

### Por que persistir o e-mail separadamente?

O Jellyfin não armazena e-mail no usuário. Para impedir que o mesmo e-mail crie contas duplicadas, o plugin mantém o próprio índice. É gravado de forma **atômica** (arquivo `.tmp` + `File.Move`), seguindo o padrão do `ArmazenamentoDados` do JellyPix.

## Onde os modelos são usados

```text
ArmazenamentoCodigos  ── usa ──▶ CodigoVerificacao (RAM, ConcurrentDictionary)
ArmazenamentoCadastros ── usa ──▶ CadastroConcluido (JSON, disco)
ServicoCadastro       ── orquestra ambos ──▶ IUserManager (SQLite nativo do Jellyfin)
```

> O usuário final é criado no banco **SQLite nativo** do Jellyfin via `IUserManager` — o plugin não escreve SQL direto.

---
tags:
  - arquitetura
  - jellyauth
  - jellyfin
  - plugin
created: 2026-09-23
---

# Arquitetura do Plugin

> Visão geral dos componentes C# e do fluxo de auto-cadastro do **JellyAuth**.

## Diagrama de componentes

```text
┌─────────────────────────────────────────────────────────────────────────┐
│ Jellyfin Server (DI container)                                          │
│                                                                         │
│  Plugin.cs  (BasePlugin<ConfiguracaoPlugin> + IHasWebPages)             │
│  RegistroServicos.cs  (IPluginServiceRegistrator)                       │
│                                                                         │
│  ┌─────────────── API ────────────────┐   ┌────────── Serviços ───────┐ │
│  │ ControladorCadastro (público)      │──▶│ ServicoCadastro (regras)  │ │
│  │   /JellyAuth/Status             │   │ ServicoEmail (SMTP)       │ │
│  │   /JellyAuth/client.js          │   │ InjetorScriptWeb (middle) │ │
│  │   /JellyAuth/Request|Verify     │   └───────────────────────────┘ │
│  │   /JellyAuth/Resend             │              │                  │
│  │ ControladorAdmin (RequiresElevation)│             │                  │
│  │   /JellyAuth/Admin/TestarEmail  │   ┌────────── Dados ──────────┐ │
│  └────────────────────────────────────┘   │ ArmazenamentoCodigos (RAM)│ │
│                                           │ ArmazenamentoCadastros    │ │
│  Seguranca/InicioCadastro (IStartupFilter)│   (JSON em plugins/config)│ │
│  └──▶ UseMiddleware<InjetorScriptWeb>     └───────────────────────────┘ │
│                                                                         │
│  APIs nativas usadas: IUserManager, (ILibraryManager via painel)        │
└─────────────────────────────────────────────────────────────────────────┘
```

## Camadas

### 1. `Plugin.cs`
- Herda `BasePlugin<ConfiguracaoPlugin>` — persistência automática da configuração (XML em `plugins/configurations/Jellyfin.Plugin.JellyAuth.xml`).
- Implementa `IHasWebPages` para registrar o **painel admin** (`Configuracao/painel.html` como recurso embutido).
- `Instancia` estática: permite que os serviços leiam a configuração atual via `Func<ConfiguracaoPlugin>`.

### 2. `RegistroServicos.cs`
- Implementa `IPluginServiceRegistrator`, registrando:
  - `Func<ConfiguracaoPlugin>` → leitura sempre atual da config (sem reiniciar).
  - `TimeProvider.System` (clock injetável, testável).
  - `ArmazenamentoCodigos`, `ArmazenamentoCadastros`, `ServicoEmail`, `ServicoCadastro`.
  - `IStartupFilter` → `InicioCadastro` (middleware de injeção do script).

### 3. API
| Controller | Rota | Auth |
|---|---|---|
| `ControladorCadastro` | `/JellyAuth/*` | `[AllowAnonymous]` |
| `ControladorAdmin` | `/JellyAuth/Admin/*` | `RequiresElevation` (admin) |

Detalhes em [[API-Request]], [[API-Verify]], [[API-Resend]].

### 4. Serviços
- **`ServicoCadastro`** — validações (usuário/e-mail/senha), rate limit, criação do usuário, aplicação de permissões (download; bibliotecas ficam desligadas, gerenciadas pelo JellyPix).
- **`ServicoEmail`** — envia o código via `System.Net.Mail.SmtpClient` (sem dependência externa). Usado só quando `ExigirVerificacaoEmail` está ligado.
- **`InjetorScriptWeb`** — injeta `<script>` no `index.html` em tempo de execução (ver [[ADR-001 - Injeção de JS no Jellyfin Web]]).

### 5. Dados
- **`ArmazenamentoCodigos`** — códigos e rate limit em memória (`ConcurrentDictionary`) (ver [[ADR-002 - Armazenamento de Códigos Temporários de Verificação]]).
- **`ArmazenamentoCadastros`** — JSON (`plugins/configurations/JellyAuth.cadastros.json`) com o mapeamento e-mail→usuário (o `User` nativo não tem campo de e-mail).
- **`ArmazenamentoSegredos`** — senha SMTP em arquivo próprio (`JellyAuth.smtp-senha.txt`, modo 0600), fora da configuração XML (não vaza pela API de config).

### 6. Rotina em background
- **`RotinaLimpeza`** (`BackgroundService`) — poda a cada 5 min códigos e contadores de rate limit expirados, além do teto de tamanho (50k), mitigando DoS de memória.

## Fluxo de ponta a ponta

```mermaid
sequenceDiagram
    participant N as Navegador (web/#/register)
    participant A as ControladorCadastro
    participant S as ServicoCadastro
    participant C as ArmazenamentoCodigos
    participant E as ServicoEmail (SMTP)
    participant J as IUserManager

    N->>A: POST /JellyAuth/Request {username,email,password}
    A->>S: SolicitarAsync
    S->>S: valida usuário/e-mail/senha + rate limit
    S->>C: CriarCodigo (gera 6 dígitos, em RAM)
    S->>E: EnviarCodigoAsync
    E-->>N: e-mail com código
    N->>A: POST /JellyAuth/Verify {email, code}
    A->>S: VerificarAsync
    S->>C: Confirmar (compara e consome)
    S->>J: CreateUserAsync + ChangePassword + UpdateUserAsync
    A-->>N: 200 { sucesso }
    N->>N: redireciona para #/login
```

## Compatibilidade entre versões (10.11 ↔ 12)

O `IUserManager` mudou de assinatura **dentro da própria série 10.11** (e de novo no 12). O pacote NuGet `Jellyfin.Controller 10.11.0` expõe `ChangePassword(User, string)`, mas o servidor **10.11.11** já usa `ChangePassword(Guid, string)` — o que quebrava o plugin com `MissingMethodException` em tempo de execução.

| API | 10.11.0 (pacote) | 10.11.11 (runtime) | 12 |
|---|---|---|---|
| `ChangePassword` | `(User, string)` | `(Guid, string)` | `(Guid, string)` |
| `Users` | propriedade | método `GetUsers()` | método `GetUsers()` |
| `CreateUserAsync(string)` | idêntico | idêntico | idêntico |
| `UpdateUserAsync(User)` | idêntico | idêntico | idêntico |
| `DeleteUserAsync(Guid)` | idêntico | idêntico | idêntico |

**Solução:** a chamada a `ChangePassword` é feita por **reflexão** (`MetodoChangePassword`, resolvido uma vez em tempo de execução), despachando para a assinatura correta conforme o tipo do primeiro parâmetro (`Guid` ou `User`). Um único build funciona em qualquer versão da série. As extensões `SetPermission`/`SetPreference` (`Jellyfin.Data`) e os enums `PermissionKind`/`PreferenceKind` são estáveis.

> Referência cruzada: o plugin **JellyPix** (mesmo repositório de origem) documentou essa volatilidade da API e a resolveu com reflexão — mesma estratégia adotada aqui.

# JellyAuth — Guia para agentes de IA

## Visão geral

Plugin do Jellyfin que permite **auto-cadastro de usuários com verificação por e-mail**: na tela de login aparece um botão "Criar conta"; o visitante informa usuário, e-mail e senha, recebe um código de 6 dígitos por e-mail e só vira usuário do Jellyfin depois de confirmar o código. Inclui captcha (Cloudflare Turnstile, opcional), rate limit por e-mail e por IP, cooldown de reenvio, expiração de código e **cadastro restrito a convites** gerados pelo admin (opcional).

- Linguagem: **C#** (nomes de classes, métodos, variáveis e comentários em **português brasileiro**; mensagens ao usuário também em português).
- Frameworks alvo: **net9.0** (Jellyfin 10.11.x) e, se o SDK do .NET 10 estiver instalado, também **net10.0** (Jellyfin 12.x). A seleção é condicional no `.csproj`.
- O plugin inteiro é **uma única DLL**: o frontend (`Web/client.js` e `Configuracao/painel.html`) vai embutido como **recurso incorporado** e é servido em tempo de execução.
- Licença: GPL-3.0. Versão atual: 1.2.2.0 (a mesma no `<Version>` do `.csproj`, no `VERSAO` do `build-and-deploy.sh` e no `meta.json` — manter sincronizadas). GUID do plugin: `6591c9c1-2d2d-463b-b4e0-560fc466024b`.

## Estrutura do repositório

- `Jellyfin.Plugin.JellyAuth/` — código do plugin.
  - `Plugin.cs` — classe do plugin (`BasePlugin<ConfiguracaoPlugin>`, `IHasWebPages`), instância estática `Plugin.Instancia`.
  - `RegistroServicos.cs` — `IPluginServiceRegistrator`: registra todos os serviços no DI do Jellyfin (singletons) mais o `HostedService` de limpeza e o `IStartupFilter` do middleware. Configuração é lida sempre via `Func<ConfiguracaoPlugin>` (reflete mudanças do painel sem reiniciar).
  - `Api/` — controladores ASP.NET Core:
    - `ControladorCadastro.cs` — endpoints públicos `[AllowAnonymous]` em `JellyAuth/`: `Status`, `client.js`, `Request`, `Verify`, `Resend`. Também contém a resolução do IP do visitante (ver "Considerações de segurança").
    - `ControladorAdmin.cs` — endpoints admin (`[Authorize(Policy = "RequiresElevation")]`) em `JellyAuth/Admin/`: listar/criar/revogar/excluir convites, salvar senha SMTP, salvar secret do captcha, testar e-mail.
    - `Api/Contratos/` — DTOs de pedido/resposta (records): `PedidoRegistro`, `PedidoVerificacao`, `PedidoConvite`, `PedidoSenha`, `PedidoTesteEmail`, `RespostaCadastro`, `RespostaConvite`, `RespostaErro`, `RespostaMensagem` etc.
  - `Configuracao/` — `ConfiguracaoPlugin.cs` (configuração editada no painel; os nomes das propriedades são os IDs dos campos do `painel.html`, as seções são as abas: Geral, SMTP, Regras de Usuário, Segurança, Captcha), `painel.html` (página de configuração do admin, recurso incorporado), `TipoCaptcha.cs`.
  - `Dados/` — persistência:
    - `ArmazenamentoCodigos.cs` — códigos de verificação, rate limit e **reservas de uso de convite** dos cadastros pendentes **em memória** (nunca em disco — ver ADR-002), com tetos de tamanho e poda periódica.
    - `ArmazenamentoCadastros.cs` — mapeamento e-mail → usuário em `JellyAuth.cadastros.json` (gravação atômica, lock, trabalha em cópia).
    - `ArmazenamentoConvites.cs` + `Convite.cs` — convites de cadastro em `JellyAuth.convites.json`, mesmo padrão dos cadastros (gravação atômica, lock, alterações numa cópia). Código de 12 caracteres sem ambíguos (sem 0/O, 1/I/L); **só o hash SHA-256 e o prefixo ficam gravados** — o código em claro aparece uma única vez, na resposta de criação. Arquivo corrompido sem cópia de segurança faz o serviço falhar na hora (cadastro responde 503) até o admin corrigir, para não apagar a única cópia.
    - `ArmazenamentoSegredos.cs` — senha SMTP e secret do captcha em arquivos próprios (`JellyAuth.smtp-senha.txt`, `JellyAuth.captcha-secret.txt`) na pasta de configuração de plugins, fora do XML de configuração, com permissão 0600 no Unix.
    - `CodigoVerificacao.cs` — modelo do cadastro pendente.
    - `ArquivoProtegido.cs` — escrita de arquivo que já nasce com permissão 0600 no Unix (segredos e arquivos JSON com PII).
  - `Seguranca/` — `InicioCadastro.cs` (`IStartupFilter` que coloca o middleware do plugin na frente), `TextoParaLog.cs` (mascaramento de e-mail e sanitização de texto para log).
  - `Servicos/` — regras de negócio:
    - `ServicoCadastro.cs` — fluxo principal (validação, rate limit, captcha, convite, criação do usuário no Jellyfin). Também define a exceção `ErroCadastro`.
    - `ServicoEmail.cs` — envio SMTP com `SmtpClient` do próprio .NET (sem dependência externa). Porta 465 (SSL implícito) não é suportada; use 587 com STARTTLS.
    - `ServicoCaptcha.cs` — validação do token Turnstile.
    - `InjetorScriptWeb.cs` — middleware que injeta a tag `<script>` **na resposta HTTP** do `index.html` (não escreve em arquivo; funciona em Docker e sobrevive a atualizações — ver ADR-001).
    - `RotinaLimpeza.cs` — `HostedService` que poda códigos/rate limit expirados.
  - `Web/` — `client.js` (interface do auto-cadastro na tela de login, recurso incorporado) e `ScriptWeb.cs` (carrega o recurso e carimba a versão por hash SHA-256, substituindo o marcador `__VERSAO_SCRIPT__`).
- `Jellyfin.Plugin.JellyAuth.Testes/` — testes xUnit (net9.0). **Fora do repositório público** (ver `.gitignore`).
- `build-and-deploy.sh` — build + deploy local no servidor de testes (também fora do repositório público).
- `empacotar.sh` — gera os zips da release em `dist/`, um por série do Jellyfin (exige SDK do .NET 10; confere se a versão do `.csproj` bate com a do `meta.json`).
- `dist/` — pacotes gerados (`JellyAuth_<versão>_jellyfin-10.11.11.zip`, `JellyAuth_<versão>_jellyfin-12.1.zip`).
- `meta.json`, `icon.png`, `icon.svg`, `LICENSE` — metadados/ícone/licença do plugin.

## Build e teste

Requer o SDK do .NET 9 (com o do .NET 10 instalado, o build de net10.0/Jellyfin 12 é habilitado automaticamente pelo `.csproj` e pelos scripts).

```bash
# Compilar (Release, net9.0)
dotnet build Jellyfin.Plugin.JellyAuth -c Release -f net9.0

# Rodar os testes (112 testes, net9.0)
dotnet test Jellyfin.Plugin.JellyAuth.Testes

# Build completo + deploy no servidor de testes local (Jellyfin Flatpak)
./build-and-deploy.sh                # compila e instala em ~/.var/app/org.jellyfin.JellyfinServer
./build-and-deploy.sh --sem-deploy   # só compila (gera dist/)
./build-and-deploy.sh --restart      # tenta reiniciar o Jellyfin após instalar

# Gerar os zips da release (um por série: jellyfin-10.11.11 e jellyfin-12.1)
./empacotar.sh
```

Detalhes de build importantes (ver comentários no `.csproj`):

- Compila-se contra a **primeira versão de cada série** do Jellyfin (`JellyfinVersion` = 10.11.0 para net9.0, 12.0.0 para net10.0), para a DLL carregar em qualquer atualização da série. `Jellyfin.Controller` e `Jellyfin.Model` são referenciados com `ExcludeAssets=runtime`.
- O projeto usa `TreatWarningsAsErrors=true`, `Nullable=enable`, `ImplicitUsings=enable`.
- O projeto de testes acessa internals do plugin via `InternalsVisibleTo` e referencia o Jellyfin 10.11.0 **com** runtime (para carregar `ConfiguracaoPlugin : BasePluginConfiguration`).
- O script de deploy gera o `meta.json` exigido pelo Jellyfin a partir do do repositório (category, guid, version, status Active), só ajustando o `targetAbi` da série.

## Convenções de código

- **Tudo em português brasileiro**: identificadores, comentários XML, mensagens de erro ao usuário e logs. Manter esse padrão em código novo.
- Comentários XML (`<summary>`) explicando a intenção/segurança de cada classe e membro público; comentários inline explicam decisões não óbvias (ex.: por que a senha é definida depois de `UpdateUserAsync`).
- Concorrência com `System.Threading.Lock` (novo tipo do .NET 9) + `ConcurrentDictionary`; seções críticas documentadas com comentários "Sempre chamado sob _travaX".
- Serviços registrados como singletons no DI; configuração sempre obtida via `Func<ConfiguracaoPlugin>` injetado (nunca capturar `ConfiguracaoPlugin` diretamente).
- Compatibilidade interna de ABI por reflexão quando necessário (ex.: `ServicoCadastro` resolve a assinatura de `IUserManager.ChangePassword` em runtime, pois mudou dentro da série 10.11 — `User` → `Guid`).
- Erros de negócio usam a exceção `ErroCadastro` (mensagem amigável + status HTTP, definida em `ServicoCadastro.cs`); os controladores convertem em `RespostaErro`.
- Mensagens de erro ao usuário são propositalmente genéricas quando revelar detalhe vazaria informação (ex.: "Este nome de usuário ou e-mail já está em uso", "Código inválido ou expirado", e uma mensagem única para convite inexistente/revogado/esgotado/reservado).

## Testes

- xUnit + `Microsoft.NET.Test.Sdk` + coverlet, com `FakeTimeProvider` próprio para controlar o tempo (os serviços recebem `TimeProvider` injetado).
- Testes cobrem armazenamento de códigos (geração, expiração, cooldown, rate limit, consumo), armazenamento de cadastros e de convites (persistência JSON, revogação, expiração, esgotamento, arquivo corrompido), o fluxo de cadastro com convites (`ServicoCadastroConvitesTests`), injeção do script web, configuração (clamp de valores) e sanitização de logs.
- Nomenclatura dos testes: `Metodo_Cenario_ResultadoEsperado` em português.
- Rodar com `dotnet test Jellyfin.Plugin.JellyAuth.Testes` (net9.0).

## Considerações de segurança

Este plugin abre um endpoint **público** de criação de contas; a superfície de ataque é tratada com cuidado. Ao alterar o código, preserve estas garantias:

- Códigos de verificação: 6 dígitos via `RandomNumberGenerator`, comparação em **tempo constante** (`CryptographicOperations.FixedTimeEquals`), remoção após tentativas erradas, expiração configurável, nunca gravados em disco.
- Convites: código de 12 caracteres (60 bits) gerado por RNG criptográfico, **só o hash SHA-256 é persistido**, busca com comparação em tempo constante. O rate limit do pedido de cadastro vem **antes** da conferência do convite, bloqueando adivinhação. O uso só é gasto quando a conta é criada: cadastros pendentes **reservam** usos em memória (`ArmazenamentoCodigos`), e um pedido que nunca confirma o e-mail não queima o convite. Dois cadastros simultâneos com o mesmo convite de uso único: só um consome (conferência de novo sob lock em `Consumir`).
- Rate limit por e-mail e por IP, com tetos rígidos de tamanho dos dicionários e poda limitada em frequência (mitigação de DoS de memória/CPU). No teto, só **chaves novas** são barradas: e-mails/IPs já conhecidos seguem pelo próprio limite, para uma enxurrada de chaves novas não derrubar cadastros em andamento (nem virar DoS de serviço). Resolução de IP do cliente (`ControladorCadastro.IpDoVisitante`): com `ConfiarProxy` ligado, só aceita cabeçalhos se a conexão vier de endereço local/interno ou de um IP da lista `ProxiesConfiaveis` (IPs ou faixas CIDR); percorre o `X-Forwarded-For` **de trás para a frente** até o primeiro IP que não consta na lista `ProxiesConfiaveis` (os da frente são controlados pelo cliente), e só usa `CF-Connecting-IP` quando não há `X-Forwarded-For`. IPs de rede interna no cabeçalho **não** são pulados por conta própria — podem ser o próprio visitante (LAN/Tailscale), e pulá-los deixaria o cabeçalho escolher o IP; para pular um proxy interno em cadeia, o admin o lista. Conexão direta da internet nunca escolhe o próprio IP pelo cabeçalho.
- Não revelar contas existentes: pedido de cadastro com e-mail já cadastrado recebe a mesma resposta de um cadastro novo ("enviamos um código"); só o dono do e-mail recebe o aviso de que já tem conta. O reenvio também responde igual, com envio em segundo plano, para não vazar pela resposta se há cadastro pendente.
- Segredos (senha SMTP, secret do captcha) **nunca** na configuração XML nem na API de configuração: ficam em arquivos próprios com permissão 0600 (`ArmazenamentoSegredos`). Arquivos com segredos ou PII já **nascem** com 0600 (`ArquivoProtegido.EscreverTexto`), sem a janela de criar com a permissão padrão e restringir depois.
- Logs: e-mails sempre mascarados (`TextoParaLog.MascararEmail`); exceções sanitizadas (`TextoParaLog.Limpar`); nunca logar senhas, códigos ou segredos.
- Entradas limitadas em tamanho (500 caracteres geral, 4096 para token de captcha); validação de formato antes de qualquer consulta ao banco; rate limit antes de chamadas externas (captcha).
- Criação de usuário é transacional na prática: qualquer falha após `CreateUserAsync` desfaz o usuário (sem senha/órfão) e o registro de e-mail; usuário novo começa sem acesso a bibliotecas (só as marcadas pelo admin), sem download por padrão e com lockout após 3 tentativas de login.

## Deploy e documentação externa

- O deploy de desenvolvimento é local, num Jellyfin instalado via **Flatpak** (`org.jellyfin.JellyfinServer`); caminho configurável pela variável `JELLYFIN_FLATPAK_ROOT`.
- Pacotes de release (`./empacotar.sh`): um ZIP por série do Jellyfin (`dist/JellyAuth_<versão>_jellyfin-10.11.11.zip`, `dist/JellyAuth_<versão>_jellyfin-12.1.zip`), cada um com a DLL, `meta.json` (com o `targetAbi` da série), `icon.png` e `LICENSE`. A instalação é descompactar numa pasta dentro de `plugins/` e reiniciar o Jellyfin.
- Releases públicas no GitHub: https://github.com/iMund/JellyAuth/releases.
- Há decisões de arquitetura (ADR-001: injeção de script na resposta HTTP; ADR-002: códigos só em memória) documentadas num vault Obsidian externo ao repositório (`~/Documentos/Obsidian Vault/JellyAuth`).

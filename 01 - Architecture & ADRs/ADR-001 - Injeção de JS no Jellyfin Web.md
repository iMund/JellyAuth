---
tags:
  - adr
  - frontend
  - injection
  - jellyauth
created: 2026-09-23
status: aceita
---

# ADR-001 — Injeção de JS no Jellyfin Web

- **Data:** 2026-09-23
- **Status:** Aceita

## Contexto

O plugin precisa adicionar um botão "Criar conta" à tela de login do Jellyfin (`/web/#/login`) e renderizar uma nova tela de cadastro em `/web/#/register`. O frontend oficial do Jellyfin é uma SPA (React/MUI) servida como arquivos estáticos na pasta `jellyfin-web`, sem API pública para estender páginas.

O ambiente de teste é o **Flatpak**, onde a pasta web fica dentro do sandbox (somente leitura para o processo), e o Jellyfin também roda em **Docker** em produção, muitas vezes sem permissão de escrita na pasta web.

## Decisão

**Injetar o `<script>` na resposta HTTP do `index.html` via middleware ASP.NET**, em vez de escrever arquivos na pasta web.

O plugin:
1. Registra um `IStartupFilter` (`InicioCadastro`) que adiciona `UseMiddleware<InjetorScriptWeb>()` na frente do pipeline.
2. O middleware intercepta `GET` de `/web/index.html`, `/web/` e `/web` e devolve o HTML com a tag:

   ```html
   <script defer src="../JellyAuth/client.js?v=<hash>"></script>
   ```

3. O `client.js` é servido por uma rota pública do próprio plugin (`GET /JellyAuth/client.js`), carregado de **recurso embutido** na DLL.

## Consequências

### Positivas
- **Sem escrita em disco**: funciona no Flatpak e no Docker sem permissão de escrita, e sobrevive a atualizações do Jellyfin (que sobrescreveriam arquivos alterados).
- **Uma única DLL**: o `client.js` e o `painel.html` vão como *embedded resources*, então o deploy é só copiar a DLL.
- **Cache controlado**: a tag carrega `?v=<hash SHA-256>` do script; quando o script muda, a versão muda e o navegador busca de novo.

### Negativas / riscos
- O middleware lê e reescreve o `index.html` inteiro a cada mudança (cacheado por `LastWriteTimeUtc` + `Length` + versão do script).
- Depende de o `index.html` conter `</body>`; se ausente, a tag é anexada ao final (comportamento degradado, mas ainda funcional).
- Qualquer *plugin* que faça o mesmo precisa de cuidado com marcadores; usamos o comentário `<!-- jellyauth -->` para idempotência.

## Alternativas rejeitadas

| Alternativa | Motivo da rejeição |
|---|---|
| Escrever `index.html`/`client.js` na pasta web | Quebra no Flatpak/Docker (sem escrita), some a cada update do Jellyfin. |
| Patch no `main.jellyfin.bundle.js` | Frágil, minificado, insustentável entre versões. |
| Proxy reverso injetando o script | Exige configuração externa; não funciona sem o proxy. |
| Extensão de navegador | Não atende apps nativos que embutem a web (Android/iOS/TV). |

> Mesma estratégia do plugin **JellyPix** (`InjetorScriptWeb`), revalidada aqui.

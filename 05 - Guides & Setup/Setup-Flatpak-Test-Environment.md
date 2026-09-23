---
tags:
  - guia
  - setup
  - flatpak
  - ambiente
created: 2026-09-23
---

# Setup — Ambiente de Testes Flatpak

Layout real do Jellyfin instalado via **Flatpak** nesta máquina (Linux Mint).

## Caminhos principais

| O que | Caminho |
|---|---|
| Raiz do app (dados) | `/home/tavares/.var/app/org.jellyfin.JellyfinServer` |
| Banco SQLite nativo | `.../data/jellyfin/data/jellyfin.db` |
| Configuração do servidor | `.../config/jellyfin/system.xml` |
| **Plugins** | `.../data/jellyfin/plugins/` |
| Config dos plugins | `.../data/jellyfin/plugins/configurations/` |
| Logs | `.../data/jellyfin/log/` |
| Web estática (somente leitura) | `/var/lib/flatpak/app/org.jellyfin.JellyfinServer/x86_64/stable/<commit>/files/bin/jellyfin-web` |

> O caminho exato da `jellyfin-web` contém um hash do commit do Flatpak e muda a cada atualização. Para localizá-lo:
> ```bash
> find /var/lib/flatpak/app/org.jellyfin.JellyfinServer -maxdepth 6 -type d -iname 'jellyfin-web'
> ```

## Versão do servidor

- **targetAbi:** `10.11.11.0` (série 10.11.x)
- Config lida em `system.xml` (ex.: `EnableLegacyAuthorization`, `UICulture`).

## Como o plugin usa cada pasta

| Pasta | Uso pelo plugin |
|---|---|
| `plugins/` | a DLL `Jellyfin.Plugin.JellyAuth.dll` + `meta.json` em `plugins/JellyAuth_1.0.0.0/` |
| `plugins/configurations/` | config XML (`Jellyfin.Plugin.JellyAuth.xml`, gerada pelo `BasePlugin`) e o JSON de cadastros (`JellyAuth.cadastros.json`) |
| `jellyfin-web` | **não é escrita** — o script é injetado em tempo de execução (ver [[ADR-001 - Injeção de JS no Jellyfin Web]]) |

## Iniciar / parar / reiniciar

```bash
# iniciar
flatpak run org.jellyfin.JellyfinServer

# parar
flatpak kill org.jellyfin.JellyfinServer

# ver se está rodando
flatpak ps
```

## Verificação rápida de saúde

```bash
# a API responde?
curl -s http://localhost:8096/System/Info/Public | head

# status do cadastro (endpoint público do plugin)
curl -s http://localhost:8096/JellyAuth/Status
```

> A porta padrão é `8096` (confira `network.xml` em `config/jellyfin/`).

## Observações deste ambiente

- O SDK do .NET disponível é o **9.0.304** (via Tizen tooling): compila `net9.0` (Jellyfin 10.11), mas **não** `net10.0` (Jellyfin 12). O `build-and-deploy.sh` detecta isso e pula o build do 12 (ver [[Como Compilar e Empacotar]]).
- Os pacotes NuGet do Jellyfin (`Jellyfin.Controller`, `Jellyfin.Model`, …) nas versões `10.11.0` e `12.0.0` já estão no cache local (`~/.nuget/packages`).
- Plugin de referência no mesmo servidor: **JellyPix** (padrões de DI, injeção de script e dados JSON).

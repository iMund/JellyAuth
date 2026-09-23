---
tags:
  - guia
  - build
  - deploy
  - jellyauth
created: 2026-09-23
---

# Como Compilar e Empacotar

## Pré-requisitos

- SDK .NET **9.0** (para Jellyfin 10.11.x) — opcionalmente .NET **10** (para Jellyfin 12.x).
- Acesso de escrita à pasta de plugins do Flatpak (já é o caso para o usuário dono dos dados).

## Build + Deploy em um passo

```bash
cd JellyAuth
./build-and-deploy.sh
```

O que ele faz:

1. Compila `net9.0` (Release) → `Jellyfin.Plugin.JellyAuth/bin/Release/net9.0/Jellyfin.Plugin.JellyAuth.dll`.
2. Se houver SDK .NET 10, publica `net10.0` para `dist/` (pacote do Jellyfin 12).
3. Copia a DLL para `plugins/JellyAuth_1.0.0.0/`.
4. Gera o `meta.json` exigido pelo Jellyfin.

Flags:

```bash
./build-and-deploy.sh --sem-deploy   # só compila, não instala
./build-and-deploy.sh --restart      # tenta reiniciar o Jellyfin após instalar
```

## Compilação manual

```bash
cd Jellyfin.Plugin.JellyAuth

# só net9.0 (funciona com o SDK 9 disponível)
dotnet build -c Release -p:TargetFrameworks=net9.0

# ambos os TFMs (exige SDK .NET 10 também)
dotnet build -c Release
```

> Por que `-p:TargetFrameworks=net9.0`? O `.csproj` declara `net9.0;net10.0`. Sem o SDK 10 instalado, o restore falha ao avaliar `net10.0`. O override restringe o build ao que o SDK suporta.

## Estrutura do `.csproj`

```xml
<TargetFrameworks>net9.0;net10.0</TargetFrameworks>   <!-- 10.11.x | 12.x -->
```

| TFM | Versão Jellyfin | PackageReference |
|---|---|---|
| `net9.0` | `10.11.0` | `Jellyfin.Controller` / `Jellyfin.Model` (com `ExcludeAssets=runtime`) |
| `net10.0` | `12.0.0` | idem |

## O que compõe o pacote

| Arquivo | Origem | Observação |
|---|---|---|
| `Jellyfin.Plugin.JellyAuth.dll` | build | contém os recursos embutidos `painel.html` e `client.js` |
| `meta.json` | gerado pelo script | `guid`, `name`, `version`, `targetAbi` |

**Não há** arquivos de frontend para copiar na pasta web: `client.js` e `painel.html` vão embutidos na DLL (ver [[ADR-001 - Injeção de JS no Jellyfin Web]]).

## Sobre o `targetAbi`

O Jellyfin carrega o plugin se `versão do servidor >= targetAbi`. Usamos:

- `10.11.0.0` para a série 10.11.x;
- `12.0.0.0` para a série 12.x.

## Empacotamento para distribuição

Para gerar um `.zip` publicável (como o JellyPix faz com `empacotar.ps1`), o conteúdo é a DLL + `meta.json`:

```bash
mkdir -p dist
cp Jellyfin.Plugin.JellyAuth/bin/Release/net9.0/Jellyfin.Plugin.JellyAuth.dll dist/
cd dist && zip JellyAuth_1.0.0.0_jellyfin-10.11.zip Jellyfin.Plugin.JellyAuth.dll meta.json
```

## Verificação pós-deploy

1. Reinicie o Jellyfin (`flatpak kill org.jellyfin.JellyfinServer && flatpak run org.jellyfin.JellyfinServer`).
2. Painel → Plugins → "JellyAuth" deve aparecer.
3. Teste a API:
   ```bash
   curl -s http://localhost:8096/JellyAuth/Status
   ```

#!/usr/bin/env bash
#
# build-and-deploy.sh — compila o plugin JellyAuth e instala no Jellyfin (Flatpak).
#
# Uso:
#   ./build-and-deploy.sh                # compila net9.0 e instala no servidor de testes
#   ./build-and-deploy.sh --sem-deploy   # só compila (gera dist/)
#   ./build-and-deploy.sh --restart      # tenta reiniciar o Jellyfin após instalar
#
# O frontend (client.js e painel.html) vai embutido na DLL como recursos incorporados e é
# injetado/servido em tempo de execução pelo plugin — não há arquivos de web para copiar.
# Ver "04 - Frontend & UI/Script Injection Strategy.md" e ADR-001.
#
set -euo pipefail

# ---------------------------------------------------------------------------
# Configuração
# ---------------------------------------------------------------------------
NOME_PLUGIN="JellyAuth"
NOME_DLL="Jellyfin.Plugin.JellyAuth"
GUID_PLUGIN="6591c9c1-2d2d-463b-b4e0-560fc466024b"
VERSAO="1.0.0.0"
DESCRICAO="Auto-cadastro de usuários com verificação por e-mail."
# ABI alvo por framework (compilamos contra a primeira versão de cada série).
ABI_NET9="10.11.0.0"
ABI_NET10="12.0.0.0"

# Caminhos do servidor de testes (Flatpak).
SERVIDOR_RAIZ="${JELLYFIN_FLATPAK_ROOT:-/home/tavares/.var/app/org.jellyfin.JellyfinServer}"
PASTA_PLUGINS="$SERVIDOR_RAIZ/data/jellyfin/plugins"
APP_FLATPAK="org.jellyfin.JellyfinServer"

# ---------------------------------------------------------------------------
# Resolve diretórios e o dotnet
# ---------------------------------------------------------------------------
DIR_PROJETO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIR_PLUGIN="$DIR_PROJETO/Jellyfin.Plugin.JellyAuth"
DIR_DIST="$DIR_PROJETO/dist"

DOTNET="$(command -v dotnet || true)"
if [ -z "$DOTNET" ]; then
    echo "ERRO: dotnet não encontrado no PATH." >&2
    exit 1
fi

SEM_DEPLOY=false
REINICIAR=false
for arg in "$@"; do
    case "$arg" in
        --sem-deploy) SEM_DEPLOY=true ;;
        --restart)    REINICIAR=true ;;
        *) echo "Argumento desconhecido: $arg" >&2; exit 2 ;;
    esac
done

# ---------------------------------------------------------------------------
# Compilação
# ---------------------------------------------------------------------------
echo "==> Compilando $NOME_PLUGIN v$VERSAO (net9.0 / Jellyfin 10.11.x)…"
"$DOTNET" build "$DIR_PLUGIN" -c Release -f net9.0 -p:TargetFrameworks=net9.0 -p:Version="$VERSAO" -nologo

DLL_NET9="$DIR_PLUGIN/bin/Release/net9.0/$NOME_DLL.dll"
if [ ! -f "$DLL_NET9" ]; then
    echo "ERRO: DLL não gerada em $DLL_NET9" >&2
    exit 1
fi

# Suporte opcional ao Jellyfin 12 (net10.0): só se o SDK do .NET 10 estiver disponível.
TEM_SDK_NET10=false
if "$DOTNET" --list-sdks 2>/dev/null | grep -qE '^1[0-9]\.'; then
    TEM_SDK_NET10=true
fi

if $TEM_SDK_NET10; then
    echo "==> Compilando net10.0 / Jellyfin 12.x (pacote dist/)…"
    "$DOTNET" publish "$DIR_PLUGIN" -c Release -f net10.0 -o "$DIR_DIST/publish/net10.0" \
        -p:Version="$VERSAO" -nologo
    mkdir -p "$DIR_DIST"
    cp "$DIR_DIST/publish/net10.0/Jellyfin.Plugin.JellyAuth.dll" \
       "$DIR_DIST/Jellyfin.Plugin.JellyAuth_${VERSAO}_jellyfin-12.dll" 2>/dev/null || true
else
    echo "==> (SDK do .NET 10 não encontrado; build do Jellyfin 12 ignorado.)"
fi

# ---------------------------------------------------------------------------
# Instalação no servidor de testes
# ---------------------------------------------------------------------------
if $SEM_DEPLOY; then
    echo "==> Build concluído (sem deploy)."
    exit 0
fi

if [ ! -d "$PASTA_PLUGINS" ]; then
    echo "ERRO: pasta de plugins não encontrada em $PASTA_PLUGINS" >&2
    echo "O Jellyfin (Flatpak) está instalado? Ajuste SERVIDOR_RAIZ no script." >&2
    exit 1
fi

PASTA_ALVO="$PASTA_PLUGINS/${NOME_PLUGIN}_${VERSAO}"
mkdir -p "$PASTA_ALVO"

echo "==> Copiando DLL para $PASTA_ALVO"
cp "$DLL_NET9" "$PASTA_ALVO/Jellyfin.Plugin.JellyAuth.dll"

# meta.json exigido pelo Jellyfin para reconhecer o plugin.
META="$PASTA_ALVO/meta.json"
cat > "$META" <<EOF
{
  "category": "General",
  "changelog": "",
  "description": "$DESCRICAO",
  "guid": "$GUID_PLUGIN",
  "name": "$NOME_PLUGIN",
  "overview": "$DESCRICAO",
  "owner": "jellyauth",
  "targetAbi": "$ABI_NET9",
  "timestamp": "0001-01-01T00:00:00.0000000Z",
  "version": "$VERSAO",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
EOF

echo "==> meta.json gravado:"
cat "$META"
echo ""

if $REINICIAR; then
    echo "==> Reiniciando o Jellyfin…"
    flatpak kill "$APP_FLATPAK" 2>/dev/null || true
    echo "    Inicie novamente com: flatpak run $APP_FLATPAK"
else
    echo "==> Pronto. Reinicie o Jellyfin para carregar o plugin:"
    echo "    flatpak kill $APP_FLATPAK && flatpak run $APP_FLATPAK"
fi

echo ""
echo "Próximos passos:"
echo "  1. No painel (Painel → Plugins → JellyAuth), configure o SMTP."
echo "  2. Ative 'Permitir que novos usuários se cadastrem' se necessário."
echo "  3. Acesse /web/#/login e use o botão 'Criar conta'."

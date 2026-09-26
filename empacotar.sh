#!/usr/bin/env bash
#
# empacotar.sh — gera os zips da release em dist/, um por série do Jellyfin:
#   dist/JellyAuth_<versão>_jellyfin-10.11.11.zip  (net9.0,  targetAbi 10.11.0.0)
#   dist/JellyAuth_<versão>_jellyfin-12.1.zip      (net10.0, targetAbi 12.0.0.0)
# Cada zip leva a DLL, o meta.json (o do repositório, com o targetAbi da série), o icon.png e a LICENSE.
# A versão vem do <Version> do .csproj e precisa ser a mesma do meta.json.
#
# Uso: ./empacotar.sh   (DOTNET=/caminho/do/dotnet escolhe o SDK, que precisa ser o .NET 10; DIST=/pasta muda a saída)
#
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET="${DOTNET:-dotnet}"
PROJETO="$DIR/Jellyfin.Plugin.JellyAuth"
DIST="${DIST:-$DIR/dist}"
mkdir -p "$DIST"
DIST="$(cd "$DIST" && pwd)" # absoluto: o zip é criado de dentro da pasta do pacote

VERSAO="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJETO/Jellyfin.Plugin.JellyAuth.csproj" | head -1)"
VERSAO_META="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$DIR/meta.json")"
if [ -z "$VERSAO" ] || [ "$VERSAO" != "$VERSAO_META" ]; then
    echo "ERRO: versão do .csproj ($VERSAO) diferente da do meta.json ($VERSAO_META)." >&2
    exit 1
fi

for alvo in "net9.0 10.11.0.0 jellyfin-10.11.11" "net10.0 12.0.0.0 jellyfin-12.1"; do
    read -r tfm abi serie <<< "$alvo"
    echo "==> $serie ($tfm, targetAbi $abi)"
    "$DOTNET" publish "$PROJETO" -c Release -f "$tfm" -o "$DIST/publish/$tfm" -p:Version="$VERSAO" -nologo -v q

    pasta="$DIST/$serie"
    rm -rf "$pasta"
    mkdir -p "$pasta"
    cp "$DIST/publish/$tfm/Jellyfin.Plugin.JellyAuth.dll" "$DIR/icon.png" "$DIR/LICENSE" "$pasta/"
    python3 - "$DIR/meta.json" "$pasta/meta.json" "$abi" <<'PY'
import json, sys
meta = json.load(open(sys.argv[1], encoding="utf-8"))
meta["targetAbi"] = sys.argv[3]
json.dump(meta, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY

    zip="$DIST/JellyAuth_${VERSAO}_${serie}.zip"
    rm -f "$zip"
    (cd "$pasta" && python3 -m zipfile -c "$zip" Jellyfin.Plugin.JellyAuth.dll meta.json icon.png LICENSE)
    echo "    $zip"
done

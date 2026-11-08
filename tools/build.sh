#!/usr/bin/env bash
# 编译两层 DLL，组装成 out/SteamPresence/（直接放进 Thor 的 desktop/Mods 即可）和 zip。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/out/SteamPresence"
dotnet build "$ROOT/src/SteamPresence.Core/SteamPresence.Core.csproj" -c Release -nologo -v q
dotnet build "$ROOT/src/SteamPresence/SteamPresence.csproj" -c Release -nologo -v q
rm -rf "$OUT"; mkdir -p "$OUT"
cp "$ROOT/src/SteamPresence/bin/Release/SteamPresence.dll" "$OUT/"
cp "$ROOT/src/SteamPresence.Core/bin/Release/SteamPresence.Core.dll" "$OUT/"
cp "$ROOT/src/SteamPresence/manifest.json" "$OUT/"
VER=$(grep -oE '"Version": *"[^"]+"' "$ROOT/src/SteamPresence/manifest.json" | grep -oE '[0-9][^"]*')
(cd "$ROOT/out" && rm -f "SteamPresence-$VER.zip" && powershell -NoProfile -Command "Compress-Archive -Path SteamPresence -DestinationPath SteamPresence-$VER.zip" )
ls -la "$OUT" "$ROOT/out"

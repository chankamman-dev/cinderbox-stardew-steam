#!/usr/bin/env bash
# 编译 LoadCache（主层 + Core 层），组装 out/LoadCache/ 和 zip。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/out/LoadCache"
dotnet build "$ROOT/src/LoadCache.Core/LoadCache.Core.csproj" -c Release -nologo -v q
dotnet build "$ROOT/src/LoadCache/LoadCache.csproj" -c Release -nologo -v q
rm -rf "${OUT:?}"; mkdir -p "$OUT"
cp "$ROOT/src/LoadCache/bin/Release/LoadCache.dll" "$OUT/"
cp "$ROOT/src/LoadCache.Core/bin/Release/LoadCache.Core.dll" "$OUT/"
cp "$ROOT/src/LoadCache/manifest.json" "$OUT/"
VER=$(grep -oE '"Version": *"[^"]+"' "$ROOT/src/LoadCache/manifest.json" | grep -oE '[0-9][^"]*')
(cd "$ROOT/out" && rm -f "LoadCache-$VER.zip" && powershell -NoProfile -Command "Compress-Archive -Path LoadCache -DestinationPath LoadCache-$VER.zip")
ls -la "$OUT"

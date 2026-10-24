#!/usr/bin/env bash
# Nino's generated TypeScript against JSON, in Node and in headless Chromium, on data written by Nino in .NET.
#
# usage: runtimes/typescript/bench/run.sh [scenario]   (e.g. lookup; default: all)
# Requires the .NET 10 SDK, Node 22.18+ (it runs TypeScript directly), tsc 5.7+ and Playwright with Chromium.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../../.." && pwd)
OUT=$HERE/out

rm -rf "$OUT" "$HERE/gen"
dotnet build -c Release "$ROOT/src/Nino.Codegen" > /dev/null
dotnet build -c Release "$HERE" > /dev/null
dotnet "$HERE/bin/Release/net10.0/Bench.dll" "$OUT/data"
dotnet "$ROOT/src/Nino.Codegen/bin/Release/net8.0/nino-gen.dll" --assembly "$HERE/bin/Release/net10.0/Bench.dll" \
  --lang ts --out "$HERE/gen" --import-extension .ts
tsc --target es2022 --module es2022 --moduleResolution bundler --strict --skipLibCheck \
  --rewriteRelativeImportExtensions --outDir "$OUT/js" "$HERE/bench.ts"

echo "## Node $(node --version)"
node --no-warnings "$HERE/node.ts" "$OUT/data" "${1:-}"
echo
echo "## Chromium"
export NODE_PATH=${NODE_PATH:-$(npm root -g)}
node "$HERE/browser.mjs" "$OUT/js" "$OUT/data" "${1:-}"

#!/usr/bin/env bash
# Nino's generated TypeScript against Protocol Buffers (protobuf-es), MessagePack (msgpackr) and JSON, timed by
# tinybench, in Node and in headless Chromium, on data written by Nino in .NET: 64 variants of each scenario, one a
# pass in turn (bench.ts says why).
#
# usage: runtimes/typescript/bench/run.sh [scenario]   (e.g. lookup; default: all)
# NINO_BENCH_TIME: tinybench's time per task in milliseconds (default 1000).
# Requires the .NET 10 SDK, Node 22.18+ (it runs TypeScript directly) and, for the Chromium run, Playwright with
# Chromium (global: NODE_PATH); the rest comes from package-lock.json.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../../.." && pwd)
OUT=$HERE/out

rm -rf "$OUT" "$HERE/gen"
(cd "$HERE" && npm ci --no-audit --no-fund > /dev/null)
dotnet build -c Release "$ROOT/src/Nino.Codegen" > /dev/null
dotnet build -c Release "$HERE" > /dev/null
dotnet "$HERE/bin/Release/net10.0/Bench.dll" "$OUT/data"
dotnet "$ROOT/src/Nino.Codegen/bin/Release/net8.0/nino-gen.dll" --assembly "$HERE/bin/Release/net10.0/Bench.dll" \
  --lang ts --out "$HERE/gen" --import-extension .ts
(cd "$HERE" && npx tsc -p . && npx esbuild bench.ts --bundle --format=esm --platform=browser --log-level=warning \
  --outfile="$OUT/js/bench.js")

echo "## Node $(node --version)"
node --no-warnings "$HERE/node.ts" "$OUT/data" "${1:-}"
echo
echo "## Chromium"
export NODE_PATH=${NODE_PATH:-$(npm root -g)}
node "$HERE/browser.mjs" "$OUT/js" "$OUT/data" "${1:-}"

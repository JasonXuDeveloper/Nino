#!/usr/bin/env bash
# The .NET suite's cases in Nino's generated TypeScript against Protocol Buffers (protobuf-es), MessagePack (msgpackr)
# and JSON, timed by tinybench, in Node and in headless Chromium (bench.ts says how).
#
# usage: runtimes/typescript/bench/run.sh [cases]   (a regular expression of case names, e.g. '^Simple'; default: all)
# NINO_BENCH_TIME: tinybench's time per task in milliseconds (default 1000); NINO_BENCH_QUICK=1: the quick cases at
# 100, with 25 ms of warm-up (the trend check a pull request labeled `benchmark` runs).
# Requires the .NET SDK, Node 22.18+ (it runs TypeScript directly) and, for the Chromium run, Playwright with Chromium
# (global: NODE_PATH); the rest comes from package-lock.json.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=$HERE/out

(cd "$HERE" && npm ci --no-audit --no-fund > /dev/null)
"$HERE/generate.sh"
(cd "$HERE" && npx tsc -p . && npx esbuild bench.ts --bundle --format=esm --platform=browser --log-level=warning \
  --outfile="$OUT/js/bench.js")

echo "## Node $(node --version)"
node --no-warnings "$HERE/node.ts" "$OUT/data" "${1:-}"
echo
echo "## Chromium"
export NODE_PATH=${NODE_PATH:-$(npm root -g)}
node "$HERE/browser.mjs" "$OUT/js" "$OUT/data" "${1:-}"

#!/usr/bin/env bash
# Runs the published runtime benchmark: Nino v3 3.3.13 (from NuGet), then the full v4 suite, on the same machine
# with the same settings. MemoryPack runs in both, so its two results show how much the machine moved.
#
# usage: benchmarks/run.sh [output directory]   (default: benchmarks/results)
#   QUICK=1     development run: shorter iterations that stop at 2% error (NINO_BENCH_QUICK, see
#               src/Nino.Benchmark/Program.cs). For trends and regressions, not for publishing.
#   FILTER=...  the v4 benchmarks to run, as BenchmarkDotNet's --filter (default '*', e.g. '*PooledTest*').
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=${1:-$ROOT/benchmarks/results}
V3=$ROOT/benchmarks/v3
V4=$ROOT/src/Nino.Benchmark
FILTER=${FILTER:-*}
if [[ "${QUICK:-0}" == 1 ]]; then
  export NINO_BENCH_QUICK=1
  ARGS=()
else
  ARGS=(--launchCount 1 --warmupCount 5 --iterationCount 15)
fi

mkdir -p "$OUT"
dotnet build -c Release "$V3" > "$OUT/build_v3.log"
dotnet build -c Release "$V4" > "$OUT/build_v4.log"
git -C "$ROOT" rev-parse HEAD > "$OUT/v4-commit.txt"
dotnet "$V3/bin/Release/net10.0/Nino.Benchmark.dll" --verify | tee "$OUT/verify_v3.txt"

echo "v3 $(date)"
(cd "$V3" && dotnet bin/Release/net10.0/Nino.Benchmark.dll --filter '*SimpleTest*' ${ARGS[@]+"${ARGS[@]}"} \
  --artifacts "$OUT/v3" > "$OUT/v3.log")
echo "v4 $(date)"
(cd "$V4" && dotnet bin/Release/net10.0/Nino.Benchmark.dll --filter "$FILTER" ${ARGS[@]+"${ARGS[@]}"} \
  --artifacts "$OUT/v4" > "$OUT/v4.log")
echo "done $(date)"

V4_REPORT=$(ls "$OUT"/v4/results/*-report-github.md | head -n1)
echo "== v3 vs v4"
python3 "$V3/compare.py" "$OUT/v3/results/Nino.Benchmark.SimpleTest-report-github.md" "$V4_REPORT"

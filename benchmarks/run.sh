#!/usr/bin/env bash
# Runs the published runtime benchmark: Nino v3 3.3.13 (from NuGet), then the full v4 suite, on the same machine
# with the same settings. MemoryPack runs in both, so its two results show how much the machine moved.
#
# usage: benchmarks/run.sh [output directory]   (default: benchmarks/results)
#   QUICK=1     development run: shorter iterations that stop at 2% error (NINO_BENCH_QUICK, see
#               benchmarks/Nino.Benchmark/NinoJobAttribute.cs). For trends and regressions, not for publishing.
#   FILTER=...  the v4 benchmarks to run, as BenchmarkDotNet's --filter patterns, separated by spaces (default '*',
#               e.g. '*PooledTest*' or '*Vectors* *FixedStruct*').
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=${1:-$ROOT/benchmarks/results}
# One project for both runs: NINO_BENCH_V3=true builds it against Nino 3.3.13 into bin/v3 (see Nino.Benchmark.csproj).
BENCH=$ROOT/benchmarks/Nino.Benchmark
V3_DLL=bin/v3/Release/net10.0/Nino.Benchmark.dll
V4_DLL=bin/Release/net10.0/Nino.Benchmark.dll
FILTER=${FILTER:-*}
read -r -a FILTERS <<< "$FILTER"
# The counts of each mode are in benchmarks/Nino.Benchmark/NinoJobAttribute.cs.
if [[ "${QUICK:-0}" == 1 ]]; then
  export NINO_BENCH_QUICK=1
fi

mkdir -p "$OUT"
NINO_BENCH_V3=true dotnet build -c Release "$BENCH" > "$OUT/build_v3.log"
dotnet build -c Release "$BENCH" > "$OUT/build_v4.log"
git -C "$ROOT" rev-parse HEAD > "$OUT/v4-commit.txt"
(cd "$BENCH" && NINO_BENCH_V3=true dotnet "$V3_DLL" --verify) | tee "$OUT/verify_v3.txt"
(cd "$BENCH" && dotnet "$V4_DLL" --verify) | tee "$OUT/verify_v4.txt"

echo "v3 $(date)"
(cd "$BENCH" && NINO_BENCH_V3=true dotnet "$V3_DLL" --filter '*SimpleTest.Nino*' '*SimpleTest.MemoryPack*' \
  --artifacts "$OUT/v3" > "$OUT/v3.log")
echo "v4 $(date)"
(cd "$BENCH" && dotnet "$V4_DLL" --filter "${FILTERS[@]}" --artifacts "$OUT/v4" > "$OUT/v4.log")
echo "done $(date)"

V4_REPORT=$(ls "$OUT"/v4/results/*-report-github.md | head -n1)
echo "== v3 vs v4"
python3 "$ROOT/benchmarks/compare_v3.py" "$OUT/v3/results/Nino.Benchmark.SimpleTest-report-github.md" "$V4_REPORT"

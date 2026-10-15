#!/usr/bin/env bash
# Runs the published runtime benchmark: Nino v3 3.3.13 (from NuGet), then the full v4 suite, then v3 again, on the
# same machine with the same settings. v3 runs before and after v4 so that drift of the machine during the v4 run
# averages out, and the two v3 runs show how much the machine moved.
#
# usage: benchmarks/run.sh [output directory]   (default: benchmarks/results)
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=${1:-$ROOT/benchmarks/results}
V3=$ROOT/benchmarks/v3
V4=$ROOT/src/Nino.Benchmark
ARGS=(--launchCount 1 --warmupCount 5 --iterationCount 15)

mkdir -p "$OUT"
dotnet build -c Release "$V3" > "$OUT/build_v3.log"
dotnet build -c Release "$V4" > "$OUT/build_v4.log"
git -C "$ROOT" rev-parse HEAD > "$OUT/v4-commit.txt"
dotnet "$V3/bin/Release/net10.0/Nino.Benchmark.dll" --verify | tee "$OUT/verify_v3.txt"

echo "v3 (before) $(date)"
(cd "$V3" && dotnet bin/Release/net10.0/Nino.Benchmark.dll --filter '*SimpleTest*' "${ARGS[@]}" --artifacts "$OUT/v3a" > "$OUT/v3a.log")
echo "v4 $(date)"
(cd "$V4" && dotnet bin/Release/net10.0/Nino.Benchmark.dll --filter '*' "${ARGS[@]}" --artifacts "$OUT/v4" > "$OUT/v4.log")
echo "v3 (after) $(date)"
(cd "$V3" && dotnet bin/Release/net10.0/Nino.Benchmark.dll --filter '*SimpleTest*' "${ARGS[@]}" --artifacts "$OUT/v3b" > "$OUT/v3b.log")
echo "done $(date)"

V4_REPORT=$(ls "$OUT"/v4/results/*-report-github.md | head -n1)
for run in v3a v3b; do
  echo "== $run vs v4"
  python3 "$V3/compare.py" "$OUT/$run/results/Nino.Benchmark.SimpleTest-report-github.md" "$V4_REPORT"
done

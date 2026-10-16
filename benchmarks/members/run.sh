#!/usr/bin/env bash
# Member-by-member comparison of Nino 3.3.13 (NuGet) and v4: v3, v4, v3 again, in-process BenchmarkDotNet
# (3 warm-up, 8 measured iterations; a few minutes). Locates a regression; for published numbers use ../run.sh.
#   benchmarks/members/run.sh               all cases
#   FILTER='*DMap*' benchmarks/members/run.sh
#   FILTER='*DStr*' DISASM=1 benchmarks/members/run.sh   also writes the JIT's code of each case (results/asm)
set -euo pipefail
cd "$(dirname "$0")"
D=$(pwd)
OUT="$D/results"
rm -rf "$OUT"; mkdir -p "$OUT"
dotnet build -c Release v3/P3.csproj > "$OUT/build_v3.log"
dotnet build -c Release v4/P4.csproj > "$OUT/build_v4.log"
ARGS=(--inProcess --warmupCount 3 --iterationCount 8)
# The disassembler needs the benchmark in its own process.
[ -n "${DISASM:-}" ] && ARGS=(--warmupCount 3 --iterationCount 8 --disasm --disasmDepth 4 --disasmFilter '*')
n=0
for v in v3 v4 v3; do
  n=$((n + 1))
  echo "$v start $(date)"
  (cd "$v" && dotnet "bin/Release/net10.0/P${v#v}.dll" --filter "${FILTER:-*}" "${ARGS[@]}" \
    --artifacts "$OUT/out-$n-$v" > "$OUT/log-$n.txt" 2>&1)
done
python3 "$D/table.py" "$OUT"
if [ -n "${DISASM:-}" ]; then
  mkdir -p "$OUT/asm"
  for n in 1 2; do
    for f in "$OUT"/out-$n-*/results/*-asm.md; do cp "$f" "$OUT/asm/$(basename "$(dirname "$(dirname "$f")")")-$(basename "$f")"; done
  done
  echo "JIT code: $OUT/asm"
fi

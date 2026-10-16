#!/usr/bin/env bash
# Member-by-member comparison of Nino 3.3.13 (NuGet) and v4: v3, v4, v3 again, in-process BenchmarkDotNet
# (3 warm-up, 8 measured iterations; a few minutes). Locates a regression; for published numbers use ../run.sh.
#   benchmarks/members/run.sh               all cases
#   FILTER='*DMap*' benchmarks/members/run.sh
#   FILTER='*DStr*' DISASM=1 benchmarks/members/run.sh   also writes the JIT's code for x64 AND arm64, whatever the
#                                                        machine (results/asm-<run>-<version>-<arch>.txt; timings
#                                                        of such a run are meaningless, see ../cross-jit.sh)
set -euo pipefail
cd "$(dirname "$0")"
D=$(pwd)
OUT="$D/results"
rm -rf "$OUT"; mkdir -p "$OUT"
[ -n "${DISASM:-}" ] && CROSS=$("$D/../cross-jit.sh")
dotnet build -c Release v3/P3.csproj > "$OUT/build_v3.log"
dotnet build -c Release v4/P4.csproj > "$OUT/build_v4.log"
n=0
for v in v3 v4 v3; do
  n=$((n + 1))
  echo "$v start $(date)"
  (
    cd "$v"
    # DISASM: the JIT prints the code it compiles for the cases and Nino's methods (every tier; read the Tier1 ones).
    # The cross JIT compiles every method for the other architecture too; both print to stdout (the log).
    if [ -n "${DISASM:-}" ] && [ $n -le 2 ]; then
      export DOTNET_JitDisasm="${DISASM_METHODS:-Parts.B:D* Parts.B:S* Nino* P3*}"
      export DOTNET_AltJit='*' DOTNET_AltJitName="$CROSS"
    fi
    dotnet "bin/Release/net10.0/P${v#v}.dll" --filter "${FILTER:-*}" --inProcess --warmupCount 3 --iterationCount 8 \
      --artifacts "$OUT/out-$n-$v" > "$OUT/log-$n.txt" 2>&1
  )
done
python3 "$D/table.py" "$OUT"
if [ -n "${DISASM:-}" ]; then
  python3 "$D/split-asm.py" "$OUT/log-1.txt" "$OUT/asm-1-v3"
  python3 "$D/split-asm.py" "$OUT/log-2.txt" "$OUT/asm-2-v4"
  echo "JIT code: $OUT/asm-1-v3-{x64,arm64}.txt, $OUT/asm-2-v4-{x64,arm64}.txt"
fi
true

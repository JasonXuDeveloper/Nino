#!/usr/bin/env bash
# Coverage-guided fuzzing of each root type with libFuzzer and SharpFuzz (README.md, "libFuzzer").
#
#   tests/Nino.Fuzz/libfuzzer/run.sh [seconds per target] [target ...]
#
# Builds the driver (clang with libFuzzer: on Ubuntu, the clang and libclang-rt-<version>-dev packages), builds
# Nino.Fuzz into $NINO_LIBFUZZER_WORK (default tests/Nino.Fuzz/bin/libfuzzer), instruments Nino.Core,
# Nino.Immutable and the generated formatters with SharpFuzz, and runs each target for the given time (default 60 s),
# starting from its seed inputs (through Launcher/, which explains why). Inputs that crash are saved to
# $NINO_LIBFUZZER_WORK/findings/<target>/crash-*; reproduce one with
#   dotnet run -c Release -f net10.0 --project tests/Nino.Fuzz -- repro <target> <file> --placement end   (start for an odd length)
# Exit code: 0 when no target found anything.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
project=$(cd "$here/.." && pwd)
work=${NINO_LIBFUZZER_WORK:-$project/bin/libfuzzer}
seconds=${1:-60}
shift || true
mkdir -p "$work"
work=$(cd "$work" && pwd)

echo "== driver"
clang++ -O1 -fsanitize=fuzzer "$here/driver.cc" -o "$work/nino-libfuzzer"

echo "== SharpFuzz"
sharpfuzz=$(command -v sharpfuzz || true)
if [ -z "$sharpfuzz" ]; then
  [ -x "$work/tools/sharpfuzz" ] || dotnet tool install SharpFuzz.CommandLine --version 2.3.0 --tool-path "$work/tools" >/dev/null
  sharpfuzz="$work/tools/sharpfuzz"
fi

echo "== build"
rm -rf "$work/out"
dotnet build "$project/Nino.Fuzz.csproj" -c Release -f net10.0 -o "$work/out" -nologo -v quiet
dotnet build "$here/Launcher/Nino.Fuzz.Launcher.csproj" -c Release -o "$work/out" -nologo -v quiet

echo "== instrument"
# Nino.Core and Nino.Immutable whole; of the fuzzer's own assemblies, only the generated formatters (namespace
# NinoGen), not the harness.
"$sharpfuzz" "$work/out/Nino.Core.dll"
"$sharpfuzz" "$work/out/Nino.Immutable.dll"
"$sharpfuzz" "$work/out/Nino.Fuzz.Golden.dll" NinoGen
"$sharpfuzz" "$work/out/Nino.Fuzz.dll" NinoGen

if [ $# -eq 0 ]; then
  mapfile -t targets < <(dotnet "$work/out/Nino.Fuzz.Launcher.dll" targets)
else
  targets=("$@")
fi
dotnet "$work/out/Nino.Fuzz.Launcher.dll" seeds "$work/seeds" >/dev/null

failed=()
for t in "${targets[@]}"; do
  echo "== $t ($seconds s)"
  mkdir -p "$work/corpus/$t" "$work/findings/$t"
  cp -n "$work/seeds/$t"/* "$work/corpus/$t/" 2>/dev/null || true
  if ! "$work/nino-libfuzzer" --target_path=dotnet --target_arg="$work/out/Nino.Fuzz.Launcher.dll libfuzzer $t" \
      -max_total_time="$seconds" -timeout=10 -rss_limit_mb=4096 -max_len=65536 -print_final_stats=1 \
      -artifact_prefix="$work/findings/$t/" "$work/corpus/$t" 2>"$work/findings/$t/log.txt"; then
    failed+=("$t")
    tail -n 60 "$work/findings/$t/log.txt"
  else
    grep -E "^(#[0-9]+.*DONE|stat::number_of_executed_units|stat::peak_rss_mb)" "$work/findings/$t/log.txt" || tail -n 3 "$work/findings/$t/log.txt"
  fi
done

if [ ${#failed[@]} -gt 0 ]; then
  echo "libFuzzer found crashes in: ${failed[*]} (inputs in $work/findings)"
  exit 1
fi
echo "libFuzzer: no findings."

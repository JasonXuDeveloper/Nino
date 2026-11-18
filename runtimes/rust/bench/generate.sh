#!/usr/bin/env bash
# The Rust benchmark's inputs, from the .NET suite (runtimes/csharp/benchmarks):
#   data/          each case's bytes (Nino.Benchmark --write-data): <Case>.nino.bin, <Case>.protobuf.bin, cases.json
#                  (each case's .NET type) and roots.txt (nino-gen's --roots: the case types that are not [NinoType]s)
#   src/model.rs   the cases' types and codecs (nino-gen --lang rust --serde on Nino.Benchmark.Schema, with roots.txt),
#                  which the benchmark looks up by each case's .NET type (model::with_nino_serde_type): a new C# case
#                  needs no Rust edit
# The Protocol Buffers messages need no step here: build.rs compiles the suite's .proto files.
# With --data, only data/ (CI: the committed src/model.rs is current, ci.yml checks it). NINO_BENCH_DATA moves data/.
# Needs the .NET SDK (runtimes/csharp/global.json).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
csharp=$(cd "$here/../../csharp" && pwd)
data=${NINO_BENCH_DATA:-$here/data}

dotnet build -c Release "$csharp/benchmarks/Nino.Benchmark" > /dev/null
rm -rf "$data"
dotnet "$csharp/benchmarks/Nino.Benchmark/bin/Release/net11.0/Nino.Benchmark.dll" --write-data "$data"
[[ ${1:-} == --data ]] && exit 0

dotnet build -c Release "$csharp/src/Nino.Codegen.Tool" > /dev/null
dotnet build -c Release "$csharp/benchmarks/Nino.Benchmark.Schema" > /dev/null
dotnet "$csharp/src/Nino.Codegen.Tool/bin/Release/net8.0/nino-gen.dll" \
  --assembly "$csharp/benchmarks/Nino.Benchmark.Schema/bin/Release/net11.0/Nino.Benchmark.Schema.dll" \
  --lang rust --serde --name model.rs --out "$here/src" --roots "$(cat "$data/roots.txt")"

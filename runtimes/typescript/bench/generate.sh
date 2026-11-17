#!/usr/bin/env bash
# The TypeScript benchmark's inputs, from the .NET suite (runtimes/csharp/benchmarks), all ignored:
#   out/data/     each case's bytes (Nino.Benchmark --write-data): <Case>.nino.bin, <Case>.protobuf.bin, cases.json (each
#                 case's .NET type) and roots.txt (nino-gen's --roots: the case types that are not [NinoType]s)
#   gen/model.ts  the cases' types and codecs (nino-gen --lang ts on Nino.Benchmark.Schema, with roots.txt), which the
#                 benchmark looks up by each case's .NET type (ninoCodecOf): a new C# case needs no TypeScript edit;
#                 gen/nino.ts is the runtime beside it
#   gen/pb/       the Protocol Buffers messages of the suite's .proto files (protobuf-es, buf.gen.yaml), whose bytes
#                 protobuf-net writes
# NINO_BENCH_DATA moves out/data. Needs the .NET SDK (runtimes/csharp/global.json) and `npm ci` here first (buf).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
csharp=$(cd "$here/../../csharp" && pwd)
data=${NINO_BENCH_DATA:-$here/out/data}

dotnet build -c Release "$csharp/benchmarks/Nino.Benchmark" > /dev/null
rm -rf "$data"
dotnet "$csharp/benchmarks/Nino.Benchmark/bin/Release/net10.0/Nino.Benchmark.dll" --write-data "$data"

dotnet build -c Release "$csharp/src/Nino.Codegen.Tool" > /dev/null
dotnet build -c Release "$csharp/benchmarks/Nino.Benchmark.Schema" > /dev/null
rm -rf "$here/gen"
dotnet "$csharp/src/Nino.Codegen.Tool/bin/Release/net8.0/nino-gen.dll" \
  --assembly "$csharp/benchmarks/Nino.Benchmark.Schema/bin/Release/net11.0/Nino.Benchmark.Schema.dll" \
  --lang ts --name model.ts --import-extension .ts --out "$here/gen" --roots "$(cat "$data/roots.txt")"

protos=$(mktemp -d)
trap 'rm -rf "$protos"' EXIT
mkdir -p "$protos/cases" "$protos/workloads"
cp "$csharp/benchmarks/Nino.Benchmark/Fixtures/cases.proto" "$protos/cases/"
cp -r "$csharp/benchmarks/Nino.Benchmark/Fixtures/protobuf-net" "$protos/"
cp "$csharp/benchmarks/Nino.Benchmark/Workloads/workloads.proto" "$protos/workloads/"
(cd "$here" && npx buf generate "$protos" --template buf.gen.yaml)

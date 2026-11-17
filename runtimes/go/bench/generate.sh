#!/usr/bin/env bash
# The Go benchmark's inputs, from the .NET suite (runtimes/csharp/benchmarks):
#   data/        each case's bytes (Nino.Benchmark --write-data): <Case>.nino.bin, <Case>.protobuf.bin, cases.json (each
#                case's .NET type) and roots.txt (nino-gen's --roots: the case types that are not [NinoType]s)
#   model/       the cases' types and codecs (nino-gen --lang go on Nino.Benchmark.Schema, with roots.txt), which the
#                benchmark looks up by each case's .NET type (model.NinoCodecOf): a new C# case needs no Go edit
#   benchpb/     with --proto, only these: the Protocol Buffers messages of the .NET suite's .proto files (cases.proto,
#                workloads.proto), whose bytes protobuf-net writes (buf.gen.yaml; protoc-gen-go and buf on PATH)
# With --data, only data/ (CI: the committed model/ is current, ci.yml checks it). NINO_BENCH_DATA moves data/.
# Needs the .NET SDK (runtimes/csharp/global.json).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
csharp=$(cd "$here/../../csharp" && pwd)
data=${NINO_BENCH_DATA:-$here/data}

if [[ ${1:-} == --proto ]]; then
  protos=$(mktemp -d)
  trap 'rm -rf "$protos"' EXIT
  mkdir -p "$protos/cases" "$protos/workloads"
  cp "$csharp/benchmarks/Nino.Benchmark/Fixtures/cases.proto" "$protos/cases/"
  cp -r "$csharp/benchmarks/Nino.Benchmark/Fixtures/protobuf-net" "$protos/"
  cp "$csharp/benchmarks/Nino.Benchmark/Workloads/workloads.proto" "$protos/workloads/"
  rm -rf "$here/benchpb"
  (cd "$here" && ${BUF:-npx -y @bufbuild/buf} generate "$protos" --template buf.gen.yaml)
  exit 0
fi

dotnet build -c Release "$csharp/benchmarks/Nino.Benchmark" > /dev/null
rm -rf "$data"
dotnet "$csharp/benchmarks/Nino.Benchmark/bin/Release/net10.0/Nino.Benchmark.dll" --write-data "$data"
[[ ${1:-} == --data ]] && exit 0

dotnet build -c Release "$csharp/src/Nino.Codegen.Tool" > /dev/null
dotnet build -c Release "$csharp/benchmarks/Nino.Benchmark.Schema" > /dev/null
roots=$(cat "$data/roots.txt")
rm -rf "$here/model"
dotnet "$csharp/src/Nino.Codegen.Tool/bin/Release/net8.0/nino-gen.dll" \
  --assembly "$csharp/benchmarks/Nino.Benchmark.Schema/bin/Release/net11.0/Nino.Benchmark.Schema.dll" \
  --lang go --package model --tags json --out "$here/model" --roots "$roots"

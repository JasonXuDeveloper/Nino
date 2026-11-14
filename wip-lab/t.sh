#!/bin/bash
# usage: t.sh [filter]   builds net10.0 Release once and runs the filter under each ISA configuration (one lock for all)
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
cd /tmp/claude-0/-workspace/91aad9ce-3bb6-50d1-a5d8-163511cc7c13/scratchpad/simd-clone
F=${1:-'FullyQualifiedName~Nullable|FullyQualifiedName~GoldenBytes|FullyQualifiedName~Layout|FullyQualifiedName~Padding'}
exec 9>/tmp/nino-heavy.lock; flock 9
dotnet build tests/Nino.UnitTests -c Release -f net10.0 9>&- 2>&1 | grep -E " error |rror\(s\)" | sort -u | head -20
run() { name=$1; shift; r=$(/usr/bin/env "$@" dotnet test tests/Nino.UnitTests -c Release -f net10.0 --no-build --filter "$F" 9>&- 2>&1 | grep -E "^(Passed!|Failed!)|^\s+Failed |Assert|error" | head -${LINES_MAX:-12}); echo "[$name] $r"; }
run default X=1
run noavx512 DOTNET_EnableAVX512=0
run noavx512-nogfni DOTNET_EnableAVX512=0 DOTNET_EnableGFNI=0
run noavx2 DOTNET_EnableAVX2=0
run noavx2-nogfni DOTNET_EnableAVX2=0 DOTNET_EnableGFNI=0
run nohw DOTNET_EnableHWIntrinsic=0

#!/bin/bash
# list.sh <side> <config> <case filter> <methods> <out file>: Tier1 listings of the methods while the case runs
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
L=/tmp/claude-0/-workspace/91aad9ce-3bb6-50d1-a5d8-163511cc7c13/scratchpad/lab
case $2 in
  default) E="X=1";; noavx512) E="DOTNET_EnableAVX512=0";; noavx512-nogfni) E="DOTNET_EnableAVX512=0 DOTNET_EnableGFNI=0";;
  noavx2) E="DOTNET_EnableAVX2=0";; noavx2-nogfni) E="DOTNET_EnableAVX2=0 DOTNET_EnableGFNI=0";;
esac
rm -f "$5"
exec 9>/tmp/nino-heavy.lock; flock 9
/usr/bin/env $E DOTNET_JitDisasm="$4" DOTNET_JitStdOutFile="$5" dotnet $L/e2e-$1/bin/Release/net10.0/e2e.dll "$3" 2 9>&- | tail -3
grep -n "^; Assembly listing\|^; Tier\|; Total bytes" "$5" | paste - - - | cut -c1-230

#!/bin/bash
# build-e2e.sh head|base [arm]: builds the end-to-end timing program against the clone (head) or the base worktree,
# from a copy of the project per side
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
S=/tmp/claude-0/-workspace/91aad9ce-3bb6-50d1-a5d8-163511cc7c13/scratchpad
side=$1; root=$S/simd-clone; [ "$side" = base ] && root=$S/simd-base
mkdir -p $S/lab/e2e-$side && cp $S/lab/e2e/Program.cs $S/lab/e2e/e2e.csproj $S/lab/e2e-$side/ && cd $S/lab/e2e-$side
sed -i "s|<NinoRoot Condition=\"'\$(NinoRoot)' == ''\">.*</NinoRoot>|<NinoRoot>$root</NinoRoot>|" e2e.csproj
exec 9>/tmp/nino-heavy.lock; flock 9
if [ "$2" = arm ]; then
  dotnet publish -c Release -r linux-arm64 --self-contained -o out-arm 9>&- 2>&1 | grep -E " error |rror\(s\)|Elapsed" | sort -u | head
else
  dotnet build -c Release 9>&- 2>&1 | grep -E " error |rror\(s\)|Elapsed" | sort -u | head
fi

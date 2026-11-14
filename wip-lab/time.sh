#!/bin/bash
# time.sh <config> [filter] [rounds]: base and head alternated twice under one lock
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
L=/tmp/claude-0/-workspace/91aad9ce-3bb6-50d1-a5d8-163511cc7c13/scratchpad/lab
case $1 in
  default) E="X=1";; noavx512) E="DOTNET_EnableAVX512=0";; noavx512-nogfni) E="DOTNET_EnableAVX512=0 DOTNET_EnableGFNI=0";;
  noavx2) E="DOTNET_EnableAVX2=0";; noavx2-nogfni) E="DOTNET_EnableAVX2=0 DOTNET_EnableGFNI=0";; nohw) E="DOTNET_EnableHWIntrinsic=0";;
esac
exec 9>/tmp/nino-heavy.lock; flock 9
for r in 1 2; do for side in ${SIDES:-base head}; do
  /usr/bin/env $E dotnet $L/e2e-$side/bin/Release/net10.0/e2e.dll "${2:--}" "${3:-10}" 9>&- > $L/time-$1-$side-$r.txt 2>&1
done; done
python3 - "$1" <<'PY'
import sys,re,glob
L='/tmp/claude-0/-workspace/91aad9ce-3bb6-50d1-a5d8-163511cc7c13/scratchpad/lab'
cfg=sys.argv[1]; data={}
for side in ('base','head'):
    for f in sorted(glob.glob(f'{L}/time-{cfg}-{side}-*.txt')):
        for line in open(f):
            m=re.match(r'(.{28}) best\s+([\d.]+) ns',line)
            if m: data.setdefault(m.group(1).strip(),{}).setdefault(side,[]).append(float(m.group(2)))
            elif side=='head' and line.startswith('avx'): head=line.strip()
print(cfg, head)
for k,v in data.items():
    b=min(v.get('base',[0])); h=min(v.get('head',[0]))
    print(f'{k:24} base {b:8.1f}  head {h:8.1f}  {h/b if b else 0:5.2f}')
PY

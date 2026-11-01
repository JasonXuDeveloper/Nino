#!/usr/bin/env bash
# Exits 0 when this Linux machine's CPU has the AVX-512 subsets .NET needs for 512-bit vectors (Vector512 and
# DOTNET_MaxVectorTBitWidth=512): F, BW, CD, DQ and VL. GitHub's x64 runners are of both kinds.
flags=$(grep -m1 '^flags' /proc/cpuinfo)
for f in avx512f avx512bw avx512cd avx512dq avx512vl; do
  grep -qw "$f" <<< "$flags" || exit 1
done

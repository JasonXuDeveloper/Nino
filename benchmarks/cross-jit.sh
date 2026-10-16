#!/usr/bin/env bash
# Installs the JIT that compiles for the OTHER 64-bit architecture (arm64 on an x64 machine, x64 on an arm64 one)
# into the .NET 10 runtime the benchmarks run on, and prints its file name. With
#   DOTNET_AltJit='*' DOTNET_AltJitName=<that name> DOTNET_JitDisasm='<methods>'
# every method is compiled twice: by it (printed, never run) and by the real JIT (printed and run), so a run on one
# machine shows the code of both architectures. Leave DOTNET_JitStdOutFile unset: both JITs would write the same file.
# The cross JIT comes from Microsoft's crossgen2 package for this runtime version and must sit next to libclrjit
# (a path in DOTNET_AltJitName is not loaded). Its code is the generic Unix ABI; on Apple silicon the only
# differences are in calls with stack arguments.
set -euo pipefail
dotnet=$(command -v dotnet)
root=$(dirname "$(readlink -f "$dotnet")")
version=$(dotnet --list-runtimes | awk '/^Microsoft.NETCore.App 10\./ { v = $2 } END { print v }')
[ -n "$version" ] || { echo "no .NET 10 runtime" >&2; exit 1; }
runtime="$root/shared/Microsoft.NETCore.App/$version"
case "$(uname -m)" in
  x86_64|amd64) host=x64; target=arm64 ;;
  arm64|aarch64) host=arm64; target=x64 ;;
  *) echo "unsupported machine $(uname -m)" >&2; exit 1 ;;
esac
case "$(uname -s)" in
  Darwin) os=osx; ext=dylib ;;
  Linux) os=linux; ext=so ;;
  *) echo "unsupported OS $(uname -s)" >&2; exit 1 ;;
esac

existing=$(ls "$runtime" | grep -E "^libclrjit_(universal|unix)_${target}_${host}\.$ext$" | head -1 || true)
if [ -n "$existing" ]; then
  echo "$existing"
  exit 0
fi

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
package="microsoft.netcore.app.crossgen2.$os-$host"
curl -fsSL -o "$tmp/p.zip" "https://api.nuget.org/v3-flatcontainer/$package/$version/$package.$version.nupkg"
name=$(unzip -Z1 "$tmp/p.zip" | grep -E "^tools/libclrjit_(universal|unix)_${target}_${host}\.$ext$" | head -1 | sed 's#^tools/##' || true)
[ -n "$name" ] || { echo "no $target JIT in $package $version" >&2; exit 1; }
unzip -q -j -o "$tmp/p.zip" "tools/$name" -d "$tmp"
cp "$tmp/$name" "$runtime/"
echo "$name"

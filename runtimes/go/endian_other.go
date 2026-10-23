//go:build !(386 || amd64 || amd64p32 || arm || arm64 || loong64 || mips64le || mipsle || ppc64le || riscv64 || wasm)

package nino

// littleEndian is false on big-endian hosts (and any architecture not known to be little-endian): blocks of
// primitives are converted element by element, with the same results.
const littleEndian = false

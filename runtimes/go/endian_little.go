//go:build 386 || amd64 || amd64p32 || arm || arm64 || loong64 || mips64le || mipsle || ppc64le || riscv64 || wasm

package nino

// littleEndian is true where the host's memory order is the wire's: blocks of primitives are copied as memory.
const littleEndian = true

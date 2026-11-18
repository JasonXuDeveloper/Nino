//go:build !arm64 || purego || race

package nino

// copyFreshBytes copies equally sized slices when dst is a fresh allocation.
// Using copy in race builds keeps Go's instrumentation of the memory accesses.
func copyFreshBytes(dst, src []byte) { copy(dst, src) }

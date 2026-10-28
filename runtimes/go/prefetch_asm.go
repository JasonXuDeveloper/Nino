//go:build (amd64 || arm64) && !purego

package nino

// hasPrefetch tells whether prefetch8 issues prefetches here.
const hasPrefetch = true

// prefetch8 asks the CPU to load the cache lines at the eight addresses in p (zero or any other address is
// allowed: a prefetch never faults). One call per batch: Go has no prefetch it can inline.
//
//go:noescape
func prefetch8(p *[8]uintptr)

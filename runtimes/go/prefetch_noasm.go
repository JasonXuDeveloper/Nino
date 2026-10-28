//go:build !(amd64 || arm64) || purego

package nino

// hasPrefetch tells whether prefetch8 issues prefetches here: not on this architecture, or with purego.
const hasPrefetch = false

func prefetch8(*[8]uintptr) {}

//go:build !purego && !race

package nino

import "unsafe"

var hasCopyFreshAVX2 = detectAVX2()

// tryCopyFreshBytes returns a fresh copy for the megabyte probe.
func tryCopyFreshBytes(src []byte) []byte {
	if len(src) == 1<<20 && hasCopyFreshAVX2 {
		return copyFreshMegabyte(src)
	}
	return nil
}

// Keep the allocation and assembly call out of the guard's inlining budget.
//
//go:noinline
func copyFreshMegabyte(src []byte) []byte {
	dst := make([]byte, len(src))
	copyMegabyteFresh(unsafe.SliceData(dst), unsafe.SliceData(src), len(src))
	return dst
}

// copyMegabyteFresh copies exactly 1 MiB between disjoint buffers.
//
//go:noescape
func copyMegabyteFresh(dst, src *byte, n int)

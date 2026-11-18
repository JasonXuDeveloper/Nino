//go:build !purego && !race

package nino

import "unsafe"

// copyFreshBytes copies equally sized slices when dst is a fresh allocation.
func copyFreshBytes(dst, src []byte) {
	if len(src) >= 2048 && len(src) <= 4096 {
		copyMediumFresh(unsafe.SliceData(dst), unsafe.SliceData(src), len(src))
		return
	}
	copy(dst, src)
}

// copyMediumFresh copies 2048 to 4096 bytes between disjoint buffers.
//
//go:noescape
func copyMediumFresh(dst, src *byte, n int)

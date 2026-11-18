//go:build ninocopyprobe && (amd64 || arm64) && !purego

package nino

import "unsafe"

// The probe only takes equally sized, disjoint byte slices. Short copies and unsupported CPUs use the builtin.
func copyProbe(dst, src []byte) {
	if !hasCopyProbe || len(src) < 256 {
		copy(dst, src)
		return
	}
	copyProbeSIMD(unsafe.SliceData(dst), unsafe.SliceData(src), len(src))
}

// copyProbeSIMD copies n >= 256 bytes between disjoint buffers.
//
//go:noescape
func copyProbeSIMD(dst, src *byte, n int)

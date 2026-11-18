//go:build ninocopyprobe && (amd64 || arm64) && !purego

package nino

import (
	"bytes"
	"fmt"
	"testing"
	"unsafe"
)

func TestBlockCopyProbe(t *testing.T) {
	checkBlockCopyProbe(t)
}

func checkBlockCopyProbe(t testing.TB) {
	t.Helper()
	// Cover every cache-line alignment on both sides and every tail in the unrolled loop.
	srcStore, dstStore := make([]byte, 512), make([]byte, 512)
	for i := range srcStore {
		srcStore[i] = byte(i*73 + i/256)
	}
	for offset := 0; offset < 64; offset++ {
		d := 64 + offset
		s := 64 + offset*17%64
		for n := 0; n <= 288; n++ {
			for i := range dstStore {
				dstStore[i] = 0xA5
			}
			dst, src := dstStore[d:d+n], srcStore[s:s+n]
			copyProbe(dst, src)
			if !bytes.Equal(dst, src) || dstStore[d-1] != 0xA5 || dstStore[d+n] != 0xA5 {
				t.Fatalf("n=%d src offset=%d dst offset=%d", n, s, d)
			}
		}
	}
}

func BenchmarkBlockCopyProbe(b *testing.B) {
	if !hasCopyProbe {
		b.Skip("SIMD tier unavailable")
	}
	checkBlockCopyProbe(b) // The A/B workflow only runs benchmarks.
	for _, size := range []int{2048, 2113, 4096, 65536, 262144, 1048576} {
		for _, dstOffset := range []int{0, 4} {
			b.Run(fmt.Sprintf("%d-offset%d", size, dstOffset), func(b *testing.B) {
				// Rotate buffers and their relative page offsets. The largest cases still fit the per-task memory budget.
				const variants = 4
				var src, dst [variants][]byte
				for i := range src {
					src[i] = probeAligned(size, 0)
					dst[i] = probeAligned(size, 128*i+dstOffset)
					for j := range src[i] {
						src[i][j] = byte(j*73 + i)
					}
				}
				b.Run("builtin", func(b *testing.B) {
					b.SetBytes(int64(size))
					b.ReportAllocs()
					for i := 0; i < b.N; i++ {
						k := i % variants
						copy(dst[k], src[k])
					}
				})
				b.Run("simd", func(b *testing.B) {
					b.SetBytes(int64(size))
					b.ReportAllocs()
					for i := 0; i < b.N; i++ {
						k := i % variants
						copyProbe(dst[k], src[k])
					}
				})
			})
		}
	}
}

func probeAligned(n, offset int) []byte {
	storage := make([]byte, n+offset+4095)
	align := int((-uintptr(unsafe.Pointer(unsafe.SliceData(storage)))) & 4095)
	return storage[align+offset : align+offset+n]
}

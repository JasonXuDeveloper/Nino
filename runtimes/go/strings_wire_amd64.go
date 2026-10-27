//go:build !purego

package nino

//go:generate go run -C internal/asm . -amd64 ../../strings_wire_amd64.s -arm64 ../../strings_wire_arm64.s

import "unsafe"

// The string paths' x64 kernels (strings_wire_amd64.s, written by internal/asm with avo), one tier per instruction set:
// the writer's SSSE3, AVX2 and AVX-512 paths and the reader's SSSE3 and AVX-512 paths, each taken where the
// processor and the system support it; without SSSE3, and with the purego build tag, the Go loops of
// strings_write.go and strings.go write and read the same bytes.

// wireStringAVX2 converts src[i:n] (n > i, the bytes before i ASCII) to Latin-1 at dst+i, returning the bytes
// written, or to UTF-16 at dst+2*i, returning ^units; for text that is not WTF-8 it returns wireInvalid. dst has
// room for 2*n+wireSlack bytes. It reads the 32 bytes from the last block's start when they lie in one page.
//
//go:noescape
func wireStringAVX2(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int

// wireStringsAVX2 writes strings from strs (count of them: strings, or pointers to them when ptrs, nil the null
// string) at dst (room bytes), each with its one-byte header, while each has room for 1+2*len+wireSlack bytes and is WTF-8
// of fewer than wireMaxText (64) bytes; it returns how many it wrote and the bytes they took.
//
//go:noescape
func wireStringsAVX2(dst unsafe.Pointer, room int, strs unsafe.Pointer, count int, ptrs bool) (done, written int)

// wireStringAVX512 and wireStringsAVX512 are wireStringAVX2 and wireStringsAVX2 with AVX-512 (BW, VL and VBMI2):
// the last block read under a mask, the chars kept compressed in a register, no tables.
//
//go:noescape
func wireStringAVX512(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int

//go:noescape
func wireStringsAVX512(dst unsafe.Pointer, room int, strs unsafe.Pointer, count int, ptrs bool) (done, written int)

// utf16UTF8AVX512 writes units UTF-16 code units at src as WTF-8 at dst (room for 3*units+wireSlack bytes) and returns the
// bytes written; latin1UTF8AVX512 writes n Latin-1 bytes at src as UTF-8 at dst (room for 2*n+wireSlack). Both read only
// the text.
//
//go:noescape
func utf16UTF8AVX512(dst, src unsafe.Pointer, units int) int

//go:noescape
func latin1UTF8AVX512(dst, src unsafe.Pointer, n int) int

func cpuid(eaxArg, ecxArg uint32) (eax, ebx, ecx, edx uint32)

func xgetbv() (eax, edx uint32)

// The tiers of the vector paths: each needs the instructions of those before it.
const (
	tierSSE    = 1 // SSSE3
	tierAVX2   = 2 // AVX2 and POPCNT
	tierAVX512 = 3 // AVX-512 F, BW, VL and VBMI2 (the reader: VBMI too)
)

// writeTiers and readTiers are the tiers of the string writer's and reader's vector paths this processor has, the
// best last; writeTier and readTier are the ones taken (0: the Go loops), set otherwise only by tests that compare
// them.
var writeTiers, readTiers = detectTiers()

var writeTier, readTier = best(writeTiers), best(readTiers)

func detectTiers() (write, read []int) {
	if _, _, ecx, _ := cpuid(1, 0); ecx&(1<<9) != 0 {
		write, read = []int{tierSSE}, []int{tierSSE}
	}
	if !detectAVX2() {
		return write, read
	}
	write = append(write, tierAVX2)
	if detectAVX512() {
		write = append(write, tierAVX512)
		if detectVBMI() {
			read = append(read, tierAVX512)
		}
	}
	return write, read
}

// tierName names a tier in tests.
func tierName(t int) string {
	return [...]string{"Go", "SSE", "AVX2", "AVX-512"}[t]
}

// hasWire and hasRead tell whether the writer and the reader have a vector path here; useWire and useRead whether
// one is taken.
var hasWire, hasRead = len(writeTiers) > 0, len(readTiers) > 0

var useWire, useRead = hasWire, hasRead

func detectAVX2() bool {
	maxID, _, _, _ := cpuid(0, 0)
	if maxID < 7 {
		return false
	}
	_, _, ecx, _ := cpuid(1, 0)
	const popcnt, osxsave, avx = 1 << 23, 1 << 27, 1 << 28
	if ecx&(popcnt|osxsave|avx) != popcnt|osxsave|avx {
		return false
	}
	// The system saves the vector registers (XMM and YMM state).
	if xcr0, _ := xgetbv(); xcr0&6 != 6 {
		return false
	}
	_, ebx, _, _ := cpuid(7, 0)
	return ebx&(1<<5) != 0
}

// detectAVX512 tells whether the processor has AVX-512 F, BW, VL and VBMI2 and the system saves the opmask and
// ZMM state (osAVX512).
func detectAVX512() bool {
	if !osAVX512() {
		return false
	}
	_, ebx, ecx, _ := cpuid(7, 0)
	const f, bw, vl, vbmi2 = 1 << 16, 1 << 30, 1 << 31, 1 << 6
	return ebx&(f|bw|vl) == f|bw|vl && ecx&vbmi2 != 0
}

// detectVBMI tells whether the processor has AVX-512 VBMI.
func detectVBMI() bool {
	_, _, ecx, _ := cpuid(7, 0)
	return ecx&(1<<1) != 0
}

// latin1UTF8SSE and utf16UTF8SSE are the reader's SSSE3 paths, as latin1UTF8NEON and utf16UTF8NEON on arm64 (16
// bytes at a time; room for 16 bytes past the text's UTF-8).
//
//go:noescape
func latin1UTF8SSE(dst, src unsafe.Pointer, n int) int

//go:noescape
func utf16UTF8SSE(dst, src unsafe.Pointer, units int) int

// utf8ScanSSE is utf8Scan of the n bytes at src (n > 0), 16 at a time: their UTF-16 code units, or -1 when they are
// not strict UTF-8. Every tier takes it (its blocks are as long as most text it checks).
//
//go:noescape
func utf8ScanSSE(src unsafe.Pointer, n int) int

func scanUTF8(src unsafe.Pointer, n int) int { return utf8ScanSSE(src, n) }

// readUTF16 and readLatin1 are the reader's kernels of the tier taken.
func readUTF16(dst, src unsafe.Pointer, units int) int {
	if readTier == tierAVX512 {
		return utf16UTF8AVX512(dst, src, units)
	}
	return utf16UTF8SSE(dst, src, units)
}

func readLatin1(dst, src unsafe.Pointer, n int) int {
	if readTier == tierAVX512 {
		return latin1UTF8AVX512(dst, src, n)
	}
	return latin1UTF8SSE(dst, src, n)
}

// wireStringSSE and wireStringsSSE are wireStringAVX2 and wireStringsAVX2 with SSSE3, 16 bytes at a time (as the
// NEON kernels of arm64).
//
//go:noescape
func wireStringSSE(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int

//go:noescape
func wireStringsSSE(dst unsafe.Pointer, room int, strs unsafe.Pointer, count int, ptrs bool) (done, written int)

// wireString is the writer's kernel of the tier taken.
func wireString(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int {
	switch writeTier {
	case tierAVX512:
		return wireStringAVX512(dst, i, src, n)
	case tierAVX2:
		return wireStringAVX2(dst, i, src, n)
	}
	return wireStringSSE(dst, i, src, n)
}

// wireStrings is the writer's list kernel of the tier taken.
func wireStrings(dst unsafe.Pointer, room int, strs unsafe.Pointer, count int, ptrs bool) (done, written int) {
	switch writeTier {
	case tierAVX512:
		return wireStringsAVX512(dst, room, strs, count, ptrs)
	case tierAVX2:
		return wireStringsAVX2(dst, room, strs, count, ptrs)
	}
	return wireStringsSSE(dst, room, strs, count, ptrs)
}

// darwinRelease tells whether the kernel release (major.minor.patch) is at least the one given, as x/sys's
// darwinKernelVersionCheck (osAVX512 on macOS).
func darwinRelease(release string, major, minor, patch int) bool {
	var mmp [3]int
	c := 0
	for i := 0; i < len(release) && release[i] != 0; i++ {
		switch b := release[i]; {
		case b >= '0' && b <= '9':
			mmp[c] = 10*mmp[c] + int(b-'0')
		case b == '.':
			c++
			if c > 2 {
				return false
			}
		default:
			return false
		}
	}
	if c != 2 {
		return false
	}
	return mmp[0] > major || mmp[0] == major && (mmp[1] > minor || mmp[1] == minor && mmp[2] >= patch)
}

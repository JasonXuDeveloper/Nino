//go:build !purego

package nino

//go:generate go run ./internal/wiregen -out strings_wire_amd64.s

import "unsafe"

// The string writer's AVX2 path (strings_wire_amd64.s), taken where the processor and the system support AVX2
// (and POPCNT); elsewhere, and with the purego build tag, the Go loops of strings_write.go write the same bytes.

// wireStringAVX2 converts src[i:n] (n > i, the bytes before i ASCII) to Latin-1 at dst+i, returning the bytes
// written, or to UTF-16 at dst+2*i, returning ^units; for text that is not WTF-8 it returns wireInvalid. dst has
// room for 2*n+64 bytes. It reads the 32 bytes from the last block's start when they lie in one page.
//
//go:noescape
func wireStringAVX2(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int

// wireStringsAVX2 writes strings from strs (count of them: strings, or pointers to them when ptrs, nil the null
// string) at dst (room bytes), each with its header, while each has room for 4+2*len+64 bytes and is WTF-8 of fewer
// than 2^30 bytes; it returns how many it wrote and the bytes they took.
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

// utf16UTF8AVX512 writes units UTF-16 code units at src as WTF-8 at dst (room for 3*units+64 bytes) and returns the
// bytes written; latin1UTF8AVX512 writes n Latin-1 bytes at src as UTF-8 at dst (room for 2*n+64). Both read only
// the text.
//
//go:noescape
func utf16UTF8AVX512(dst, src unsafe.Pointer, units int) int

//go:noescape
func latin1UTF8AVX512(dst, src unsafe.Pointer, n int) int

func cpuid(eaxArg, ecxArg uint32) (eax, ebx, ecx, edx uint32)

func xgetbv() (eax, edx uint32)

// hasWire tells whether this processor takes the AVX2 path; useWire is it, off in tests that compare the two.
var hasWire = detectAVX2()

var useWire = hasWire

// hasWire512 tells whether this processor takes the AVX-512 path; useWire512 is it, off in tests that compare it
// with the AVX2 one.
var hasWire512 = hasWire && detectAVX512()

var useWire512 = hasWire512

// hasRead tells whether the string reader takes its AVX-512 path (which needs VBMI too); useRead is it, off in tests
// that compare it with the Go loops.
var hasRead = hasWire512 && detectVBMI()

var useRead = hasRead

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
// ZMM state.
func detectAVX512() bool {
	if xcr0, _ := xgetbv(); xcr0&0xe6 != 0xe6 {
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

// readUTF16 and readLatin1 are utf16UTF8AVX512 and latin1UTF8AVX512 where they are taken.
func readUTF16(dst, src unsafe.Pointer, units int) int { return utf16UTF8AVX512(dst, src, units) }

func readLatin1(dst, src unsafe.Pointer, n int) int { return latin1UTF8AVX512(dst, src, n) }

// wireString is wireStringAVX512 or wireStringAVX2, whichever is taken.
func wireString(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int {
	if useWire512 {
		return wireStringAVX512(dst, i, src, n)
	}
	return wireStringAVX2(dst, i, src, n)
}

// wireStrings is wireStringsAVX512 or wireStringsAVX2, whichever is taken.
func wireStrings(dst unsafe.Pointer, room int, strs unsafe.Pointer, count int, ptrs bool) (done, written int) {
	if useWire512 {
		return wireStringsAVX512(dst, room, strs, count, ptrs)
	}
	return wireStringsAVX2(dst, room, strs, count, ptrs)
}

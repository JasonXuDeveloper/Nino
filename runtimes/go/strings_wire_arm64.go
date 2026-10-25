//go:build !purego

package nino

import "unsafe"

// The string paths' NEON kernels (strings_wire_arm64.s, written by internal/wiregen), taken on every arm64
// processor (NEON is part of the architecture); with the purego build tag the Go loops write and read the same
// bytes.

const tierNEON = 1

// writeTiers and readTiers are the tiers of the string writer's and reader's vector paths, the best last;
// writeTier and readTier are the ones taken (0: the Go loops), set otherwise only by tests that compare them.
var writeTiers, readTiers = []int{tierNEON}, []int{tierNEON}

var writeTier, readTier = best(writeTiers), best(readTiers)

// tierName names a tier in tests.
func tierName(t int) string { return [...]string{"Go", "NEON"}[t] }

// hasWire and hasRead tell whether the writer and the reader have a vector path here; useWire and useRead whether
// one is taken.
var hasWire, hasRead = len(writeTiers) > 0, len(readTiers) > 0

var useWire, useRead = false, false // test branch: forced

// latin1UTF8NEON writes n Latin-1 bytes at src as UTF-8 at dst (room for 2*n+16 bytes) and returns the bytes
// written; utf16UTF8NEON writes units UTF-16 code units at src as WTF-8 at dst (room for 3*units+16). Both read
// only the text.
//
//go:noescape
func latin1UTF8NEON(dst, src unsafe.Pointer, n int) int

//go:noescape
func utf16UTF8NEON(dst, src unsafe.Pointer, units int) int

func readUTF16(dst, src unsafe.Pointer, units int) int { return utf16UTF8NEON(dst, src, units) }

func readLatin1(dst, src unsafe.Pointer, n int) int { return latin1UTF8NEON(dst, src, n) }

// wireStringNEON converts src[i:n] (n > i, the bytes before i ASCII) to Latin-1 at dst+i, returning the bytes
// written, or to UTF-16 at dst+2*i, returning ^units; for text that is not WTF-8 it returns wireInvalid. dst has
// room for 2*n+64 bytes.
//
//go:noescape
func wireStringNEON(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int

// wireStringsNEON writes strings from strs (count of them: strings, or pointers to them when ptrs, nil the null
// string) at dst (room bytes), each with its header, while each has room for 4+2*len+64 bytes and is WTF-8 of fewer
// than 2^30 bytes; it returns how many it wrote and the bytes they took.
//
//go:noescape
func wireStringsNEON(dst unsafe.Pointer, room int, strs unsafe.Pointer, count int, ptrs bool) (done, written int)

func wireString(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int {
	return wireStringNEON(dst, i, src, n)
}

func wireStrings(dst unsafe.Pointer, room int, strs unsafe.Pointer, count int, ptrs bool) (done, written int) {
	return wireStringsNEON(dst, room, strs, count, ptrs)
}

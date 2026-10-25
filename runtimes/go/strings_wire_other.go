//go:build !(amd64 || arm64) || purego

package nino

import "unsafe"

// No vector paths for strings here (see strings_wire_amd64.go, strings_wire_arm64.go): the Go loops write and read
// them.

var writeTiers, readTiers []int

var writeTier, readTier int

func tierName(t int) string { return "Go" }

const hasWire, hasRead = false, false

var useWire, useRead = false, false

func wireString(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int { return wireInvalid }

func wireStrings(dst unsafe.Pointer, room int, strs unsafe.Pointer, count int, ptrs bool) (done, written int) {
	return 0, 0
}

func readUTF16(dst, src unsafe.Pointer, units int) int { return 0 }

func readLatin1(dst, src unsafe.Pointer, n int) int { return 0 }

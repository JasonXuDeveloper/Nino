//go:build !amd64 || purego

package nino

import "unsafe"

// No vector path for writing strings here (see strings_wire_amd64.go): the Go loops write them.

const hasWire = false

var useWire = false

const hasWire512 = false

var useWire512 = false

const hasRead = false

var useRead = false

func wireString(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int { return wireInvalid }

func wireStrings(dst unsafe.Pointer, room int, strs unsafe.Pointer, count int, ptrs bool) (done, written int) {
	return 0, 0
}

func readUTF16(dst, src unsafe.Pointer, units int) int { return 0 }

func readLatin1(dst, src unsafe.Pointer, n int) int { return 0 }

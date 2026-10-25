//go:build (amd64 || arm64) && !purego

package nino

// The tables of the vector paths for strings (strings_wire_amd64.s, strings_wire_arm64.s).

// wideShuf[m] moves the 16-bit lanes of 8 that the bits of m keep to the front (a byte shuffle; 0x80 clears).
var wideShuf [256][16]byte

// narrowLo[m] moves the bytes of 8 that the bits of m keep to the front; narrowHi[m] the same for the high 8 bytes of
// 16 (to the start of those).
var narrowLo, narrowHi [256][8]byte

// pairShuf[m] keeps, of eight pairs of bytes (a lead, then the last byte of a char of one or two bytes of UTF-8),
// the leads the bits of m tell and every last byte, at the front; pairLen[m] is how many: 8 and a byte a bit.
var pairShuf [256][16]byte

var pairLen [256]byte

// u3Shuf[m] keeps, of four chars of 4 bytes each (three bytes of UTF-8 and a zero), the bytes of each that two bits
// of m tell, at the front: 0 the last byte, 1 the last two, 3 all three, 2 none; u3Len[m] is how many.
var u3Shuf [256][16]byte

var u3Len [256]byte

func init() {
	if !hasWire && !hasRead {
		return
	}
	for m := 0; m < 256; m++ {
		at := 0
		for k := range pairShuf[m] {
			pairShuf[m][k] = 0x80
			u3Shuf[m][k] = 0x80
		}
		for l := 0; l < 8; l++ {
			if m&(1<<l) != 0 {
				pairShuf[m][at] = byte(2 * l)
				at++
			}
			pairShuf[m][at] = byte(2*l + 1)
			at++
		}
		pairLen[m] = byte(at)
		at = 0
		for l := 0; l < 4; l++ {
			from := [4]int{2, 1, 3, 0}[m>>(2*l)&3] // the first byte kept
			if m>>(2*l)&3 == 2 {
				continue
			}
			for k := from; k < 3; k++ {
				u3Shuf[m][at] = byte(4*l + k)
				at++
			}
		}
		u3Len[m] = byte(at)
	}
	for m := 0; m < 256; m++ {
		w, lo := 0, 0
		for k := range wideShuf[m] {
			wideShuf[m][k] = 0x80
		}
		for k := range narrowLo[m] {
			narrowLo[m][k] = 0x80
			narrowHi[m][k] = 0x80
		}
		for l := 0; l < 8; l++ {
			if m&(1<<l) == 0 {
				continue
			}
			wideShuf[m][w] = byte(2 * l)
			wideShuf[m][w+1] = byte(2*l + 1)
			w += 2
			narrowLo[m][lo] = byte(l)
			narrowHi[m][lo] = byte(8 + l)
			lo++
		}
	}
}

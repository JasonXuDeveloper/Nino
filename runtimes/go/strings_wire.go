package nino

import "unsafe"

// The tables of the vector path for writing strings (strings_wire_amd64.s).

// wireInvalid is what wireString returns for text that is not WTF-8.
const wireInvalid = int(^uint(0) >> 1)

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

func best(tiers []int) int {
	if len(tiers) == 0 {
		return 0
	}
	return tiers[len(tiers)-1]
}

// setWriteTier and setReadTier take a tier of the writer's or reader's vector paths, or the Go loops (0).
func setWriteTier(t int) {
	writeTier, useWire = t, t != 0
}

func setReadTier(t int) {
	readTier, useRead = t, t != 0
}

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

// WriteStrings writes a sequence of strings (a string[] or List<string>): nil is null. It writes the bytes
// [WriteSlice] with [Writer.String] writes, many strings in one call where the platform has a vector path.
func WriteStrings(w *Writer, v []string) {
	if v == nil {
		w.Null()
		return
	}
	w.Length(len(v))
	if !useWire {
		for _, s := range v {
			w.String(s)
		}
		return
	}
	for len(v) > 0 {
		if done := w.wireStrings(unsafe.Pointer(unsafe.SliceData(v)), v, nil); done > 0 {
			v = v[done:]
			continue
		}
		// A string the vector path leaves (not UTF-8, very long).
		w.String(v[0])
		v = v[1:]
	}
}

// WriteStringPtrs writes a sequence of nullable strings (what generated code writes a C# string[] or List<string>
// as): nil is null, and so is each nil element.
func WriteStringPtrs(w *Writer, v []*string) {
	if v == nil {
		w.Null()
		return
	}
	w.Length(len(v))
	if !useWire {
		for _, s := range v {
			w.StringPtr(s)
		}
		return
	}
	for len(v) > 0 {
		if done := w.wireStrings(unsafe.Pointer(unsafe.SliceData(v)), nil, v); done > 0 {
			v = v[done:]
			continue
		}
		w.StringPtr(v[0])
		v = v[1:]
	}
}

// wireStrings writes strings from the start of one of strs or ptrs (at p) with the vector path, first giving the
// buffer room for the next of them (up to 64), and returns how many it wrote.
func (w *Writer) wireStrings(p unsafe.Pointer, strs []string, ptrs []*string) int {
	count := len(strs) + len(ptrs)
	need := 0
	for k := 0; k < min(count, 64); k++ {
		n := 0
		if strs != nil {
			n = len(strs[k])
		} else if ptrs[k] != nil {
			n = len(*ptrs[k])
		}
		if n >= 1<<30 {
			break
		}
		need += 4 + 2*n + 64
	}
	if cap(w.buf)-len(w.buf) < need {
		w.grow(need)
	}
	at := len(w.buf)
	done, written := wireStrings(unsafe.Add(unsafe.Pointer(unsafe.SliceData(w.buf)), at), cap(w.buf)-at, p, count, ptrs != nil)
	w.buf = w.buf[:at+written]
	return done
}

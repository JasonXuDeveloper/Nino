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

func init() {
	if !hasWire {
		return
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
	for len(v) > 0 {
		if useWire {
			if done := w.wireStrings(unsafe.Pointer(unsafe.SliceData(v)), v, nil); done > 0 {
				v = v[done:]
				continue
			}
		}
		// A string the vector path leaves (not UTF-8, very long), or no vector path.
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
	for len(v) > 0 {
		if useWire {
			if done := w.wireStrings(unsafe.Pointer(unsafe.SliceData(v)), nil, v); done > 0 {
				v = v[done:]
				continue
			}
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

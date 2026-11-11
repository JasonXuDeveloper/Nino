package nino

import (
	"encoding/binary"
	"math/bits"
	"unicode/utf8"
	"unsafe"
)

// [NinoUtf8] strings: read as strict UTF-8 of the declared char count, written with each invalid byte as U+FFFD.

// UTF8 reads a [NinoUtf8] string; null reads as "".
func (r *Reader) UTF8() string {
	s, _ := r.NullableUTF8()
	return s
}

// UTF8Ptr reads a [NinoUtf8] string: nil for null.
func (r *Reader) UTF8Ptr() *string {
	s, ok := r.NullableUTF8()
	if !ok {
		return nil
	}
	return r.stringBox(s)
}

// NullableUTF8 reads a [NinoUtf8] string: ok is false for null.
func (r *Reader) NullableUTF8() (s string, ok bool) {
	// Both counts one byte (text of up to 126 code units and 127 bytes): read as a pair, and r.buf set once (each
	// store to it is a write barrier while the GC marks). Anything else, a failure included, takes the general path.
	b := r.buf
	if len(b) >= 2 && b[0] >= 2 && (b[0]|b[1]) < 0x80 {
		c, size := int(b[0])-1, int(b[1])
		if size <= len(b)-2 {
			t := b[2 : 2+size]
			if size >= c && utf8Units(t, c) {
				r.buf = b[2+size:]
				return r.textCopy(t), true
			}
		}
	}
	h := r.header()
	if h <= 1 {
		return "", h == 1 && r.err == nil
	}
	// As .NET: counts past 2^31-1 are invalid, a byte count past the data is data that ends early; fewer bytes
	// than chars, or bytes that are not strict UTF-8 or decode to another number of chars, are invalid.
	c := h - 1
	size := r.header()
	if c > 1<<31-1 || size > 1<<31-1 {
		r.Fail(CodeInvalidData, "invalid UTF-8 string")
		return "", false
	}
	b = r.buf
	if uint64(size) > uint64(len(b)) {
		r.eof()
		return "", false
	}
	r.buf = b[size:]
	b = b[:size]
	if size < c || !utf8Units(b, int(c)) {
		r.Fail(CodeInvalidData, "invalid UTF-8 string")
		return "", false
	}
	if size <= textShort {
		return r.textCopy(b), true
	}
	return string(b), true
}

// utf8Units reports whether b is strict UTF-8 of exactly units UTF-16 code units.
// isASCII and utf8Scan are written out (both above the inlining budget), as in [Writer.UTF8]: a call fewer each.
func utf8Units(b []byte, units int) bool {
	n := len(b)
	if n == units {
		// As many bytes as chars: only ASCII can be.
		switch {
		case n > 16:
			return isASCIILong(b)
		case n >= 8:
			p := unsafe.Pointer(unsafe.SliceData(b))
			return (load64(p, 0)|load64(p, n-8))&highBits == 0
		}
		return isASCII(b)
	}
	if useRead && n >= scanVector {
		m := scanUTF8(unsafe.Pointer(unsafe.SliceData(b)), n)
		return m >= 0 && m == units
	}
	m, ok := utf8ScanGo(b)
	return ok && m == units
}

// utf8Scan reports whether b is strict UTF-8 (what utf8.Valid accepts: no overlong forms, encoded surrogates or
// values past U+10FFFF, no sequence cut short) and returns its length in UTF-16 code units, in one pass. From
// scanVector bytes on, where there is a vector path (amd64 with SSSE3, arm64), its kernel checks 16 bytes at a time
// (utf8ScanSSE, utf8ScanNEON); shorter text, and every other platform, take utf8ScanGo.
func utf8Scan(b []byte) (units int, ok bool) {
	if useRead && len(b) >= scanVector {
		units = scanUTF8(unsafe.Pointer(unsafe.SliceData(b)), len(b))
		return units, units >= 0
	}
	return utf8ScanGo(b)
}

// scanVector is the length from which utf8Scan takes the vector path: below it, the few bytes the Go loop runs
// through its table cost less than a call (measured on x64; a block of 16 bytes costs about the same at any length).
const scanVector = 8

// utf8ScanGo is utf8Scan in Go: a shift-based DFA, one table load, shift and mask a byte, with no branch on the
// data. Each row of utf8DFA holds, for every state (a multiple of 6, the shift that picks it), the next state in the
// 6 bits at that shift, and in its top two bits the code units the byte starts (0 for a continuation byte, 2 for a
// 4-byte lead, else 1).
func utf8ScanGo(b []byte) (units int, ok bool) {
	units = len(b)
	var state uint64
	// Eight bytes at a time: a word of ASCII between chars is skipped; any other runs through the DFA unrolled,
	// with its code units counted as in [utf16Count]. Only the low 6 bits of state are ever used (the mask is free:
	// shifts take their count mod 64).
	for len(b) >= 8 {
		x := binary.LittleEndian.Uint64(b)
		b = b[8:]
		if x&highBits == 0 && state&63 == dfaAccept {
			continue
		}
		units += bits.OnesCount64(x&(x<<1)&(x<<2)&(x<<3)&highBits) - bits.OnesCount64(x&^(x<<1)&highBits)
		state = utf8DFA[byte(x)] >> (state & 63)
		state = utf8DFA[byte(x>>8)] >> (state & 63)
		state = utf8DFA[byte(x>>16)] >> (state & 63)
		state = utf8DFA[byte(x>>24)] >> (state & 63)
		state = utf8DFA[byte(x>>32)] >> (state & 63)
		state = utf8DFA[byte(x>>40)] >> (state & 63)
		state = utf8DFA[byte(x>>48)] >> (state & 63)
		state = utf8DFA[byte(x>>56)] >> (state & 63)
	}
	for _, c := range b {
		row := utf8DFA[c]
		units += int(row>>dfaUnitsAt) - 1
		state = row >> (state & 63)
	}
	return units, state&63 == dfaAccept
}

// The states of utf8DFA, as shifts: accept (between chars), error (stays), and the bytes still to come.
const (
	dfaAccept = 6 * iota
	dfaError
	dfaCont1   // one more 80..BF
	dfaCont2   // two more 80..BF
	dfaE0      // after E0: A0..BF (no overlong form), then one
	dfaED      // after ED: 80..9F (no surrogate), then one
	dfaF0      // after F0: 90..BF (no overlong form), then two
	dfaF1      // after F1..F3: 80..BF, then two
	dfaF4      // after F4: 80..8F (nothing past U+10FFFF), then two
	dfaStates  = iota
	dfaUnitsAt = 62
)

var utf8DFA = func() (t [256]uint64) {
	for c := range 256 {
		var row uint64
		set := func(from, to uint64) { row = row&^(63<<from) | to<<from }
		for st := uint64(0); st < dfaStates; st++ {
			set(6*st, dfaError)
		}
		in := func(lo, hi int) bool { return c >= lo && c <= hi }
		switch {
		case in(0x00, 0x7F):
			set(dfaAccept, dfaAccept)
		case in(0xC2, 0xDF):
			set(dfaAccept, dfaCont1)
		case c == 0xE0:
			set(dfaAccept, dfaE0)
		case c == 0xED:
			set(dfaAccept, dfaED)
		case in(0xE1, 0xEF):
			set(dfaAccept, dfaCont2)
		case c == 0xF0:
			set(dfaAccept, dfaF0)
		case in(0xF1, 0xF3):
			set(dfaAccept, dfaF1)
		case c == 0xF4:
			set(dfaAccept, dfaF4)
		}
		if in(0x80, 0xBF) {
			set(dfaCont1, dfaAccept)
			set(dfaCont2, dfaCont1)
			set(dfaF1, dfaCont2)
		}
		if in(0xA0, 0xBF) {
			set(dfaE0, dfaCont1)
		}
		if in(0x80, 0x9F) {
			set(dfaED, dfaCont1)
		}
		if in(0x90, 0xBF) {
			set(dfaF0, dfaCont2)
		}
		if in(0x80, 0x8F) {
			set(dfaF4, dfaCont2)
		}
		switch {
		case in(0x80, 0xBF):
		case c >= 0xF0:
			row |= 2 << dfaUnitsAt
		default:
			row |= 1 << dfaUnitsAt
		}
		t[c] = row
	}
	return t
}()

// UTF8 writes a [NinoUtf8] string. Each byte of invalid UTF-8 is written as U+FFFD.
func (w *Writer) UTF8(s string) {
	n := len(s)
	if n == 0 {
		w.U8(1)
		return
	}
	bytes := stringBytes(s)
	units := n
	// isASCII and utf8Scan written out (both above the inlining budget): 8 to 16 bytes tested here as two words, and
	// the vector kernel called directly, a call fewer each.
	var ascii bool
	switch {
	case n > 16:
		ascii = isASCIILong(bytes)
	case n >= 8:
		p := unsafe.Pointer(unsafe.StringData(s))
		ascii = (load64(p, 0)|load64(p, n-8))&highBits == 0
	default:
		ascii = isASCII(bytes)
	}
	if !ascii {
		// Valid UTF-8 (the common case) is written as it is: its byte count is len(s). Anything else (an unpaired
		// surrogate in WTF-8, a byte that is not UTF-8) goes through the char by char path.
		ok := false
		if useRead && n >= scanVector {
			units = scanUTF8(unsafe.Pointer(unsafe.StringData(s)), n)
			ok = units >= 0
		} else {
			units, ok = utf8ScanGo(bytes)
		}
		if !ok {
			w.wideUtf8(s)
			return
		}
	}
	if n > maxInt32 {
		w.Fail(CodeTooLarge, "string")
		return
	}
	// The char count plus one and the byte count, headers: a byte each up to 126 bytes (units <= n).
	if n < 0x7F {
		w.shortUTF8(s, units)
		return
	}
	if uint(n-0x80) <= 0xFFFF-0x80 {
		// Both headers in one store (0x80 <= n <= 0xFFFF: the byte count's header is three bytes, the char count's one
		// or three), its bytes past them overwritten by the text.
		cs := headerSize(uint32(units + 1))
		b := w.Extend(cs + 3 + n)
		if len(b) < 8 {
			return
		}
		count := uint64(header16) | uint64(n)<<8
		if cs == 1 {
			store32(unsafe.Pointer(&b[0]), 0, uint32(units+1)|uint32(count)<<8)
		} else {
			store64(unsafe.Pointer(&b[0]), 0, uint64(header16)|uint64(units+1)<<8|count<<24)
		}
		copy(b[cs+3:], s)
		return
	}
	w.appendHeader(uint32(units + 1))
	w.appendHeader(uint32(n))
	w.buf = append(w.buf, s...)
}

// NullableUTF8 writes a [NinoUtf8] string, or null when ok is false.
func (w *Writer) NullableUTF8(s string, ok bool) {
	if !ok {
		w.U8(0)
		return
	}
	w.UTF8(s)
}

// UTF8Ptr writes a [NinoUtf8] string: nil is null.
func (w *Writer) UTF8Ptr(s *string) {
	if s == nil {
		w.U8(0)
		return
	}
	w.UTF8(*s)
}

// wideUtf8 writes text that is not valid UTF-8, char by char.
//
//go:noinline
func (w *Writer) wideUtf8(s string) {
	// Each char that is not a scalar value (an unpaired surrogate in WTF-8, or a byte that is not UTF-8) is written
	// as U+FFFD: one code unit, 3 bytes.
	units, size := 0, 0
	for i := 0; i < len(s); {
		c, n := nextUnit(s, i)
		i += n
		units++
		switch {
		case c >= 0x10000:
			units++
			size += 4
		case c >= 0xD800 && c < 0xE000:
			size += 3
		default:
			size += utf8.RuneLen(c)
		}
	}
	if size > maxInt32 {
		w.Fail(CodeTooLarge, "string")
		return
	}
	w.appendHeader(uint32(units + 1))
	w.appendHeader(uint32(size))
	b := w.Extend(size)
	if len(b) < size {
		return
	}
	j := 0
	for i := 0; i < len(s); {
		c, n := nextUnit(s, i)
		i += n
		if c >= 0xD800 && c < 0xE000 {
			c = utf8.RuneError
		}
		if j+utf8.RuneLen(c) > len(b) {
			break
		}
		j += utf8.EncodeRune(b[j:], c)
	}
}

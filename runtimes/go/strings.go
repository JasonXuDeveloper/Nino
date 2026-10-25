package nino

import (
	"encoding/binary"
	"math/bits"
	"unicode/utf8"
	"unsafe"
)

// Strings.
//
// The default encoding: an int32 header, -1 for null and 0 for empty; ~n (-2 for one char, -3 for two...) then n
// bytes when every char is at most U+00FF (Latin-1, which covers ASCII); else n then n UTF-16 code units. .NET
// strings are UTF-16 and may hold unpaired surrogates, which the specification requires readers to keep. Go strings
// are bytes, usually UTF-8, which has no form for an unpaired surrogate. So strings are read as WTF-8: UTF-8, with
// each unpaired surrogate as the 3 bytes UTF-8 would give it (ED A0 80 to ED BF BF), which no valid UTF-8 holds:
//
//   - [Reader.String] and [Reader.NullableString] return WTF-8: valid UTF-8 unless the text holds an unpaired
//     surrogate. The text round-trips through [Writer.String] byte for byte. Where valid UTF-8 matters more,
//     strings.ToValidUTF8(s, "\uFFFD") replaces each unpaired surrogate (and any other invalid byte) with U+FFFD,
//     which is what .NET's UTF-8 encoders do.
//   - [Reader.UTF16] returns the code units themselves.
//   - [Writer.String] writes the chars of a Go string read as WTF-8: unpaired surrogates as themselves, and every
//     other byte that is not UTF-8 as U+FFFD. [Writer.UTF16] writes code units as they are.
//
// [NinoUtf8] strings: an int32 char count c (UTF-16 code units; -1 null, 0 empty, nothing after either), then an
// int32 byte count b and b bytes of UTF-8, with c <= b. The reader accepts only strict UTF-8 (no overlong forms,
// no encoded surrogates, nothing past U+10FFFF) that decodes to exactly c code units.

// String reads a string; null reads as "" (see [Reader.NullableString] to tell them apart).
func (r *Reader) String() string {
	s, _ := r.NullableString()
	return s
}

// NullableString reads a string: ok is false for null.
func (r *Reader) NullableString() (s string, ok bool) {
	b := r.buf
	if len(b) < 4 {
		r.eof()
		return "", false
	}
	h := int32(binary.LittleEndian.Uint32(b))
	b = b[4:]
	if h < NullLength {
		// One byte a char. ^h is at least 1 and at most 2^31-1.
		n := int(^h)
		if n > len(b) {
			r.eof()
			return "", false
		}
		r.buf = b[n:]
		if n <= textShort/2 {
			return r.latin1Short(b[:n]), true
		}
		return latin1String(b[:n]), true
	}
	if h > 0 {
		size := uint64(h) * 2
		if size > uint64(len(b)) {
			r.eof()
			return "", false
		}
		r.buf = b[size:]
		if h <= textShort/3 {
			return r.utf16Short(b[:size]), true
		}
		return utf16String(b[:size]), true
	}
	r.buf = b
	return "", h == 0
}

// StringPtr reads a string: nil for null.
func (r *Reader) StringPtr() *string {
	s, ok := r.NullableString()
	if !ok {
		return nil
	}
	return r.stringBox(s)
}

// UTF16 reads a string as its UTF-16 code units, unpaired surrogates included: nil for null, empty (not nil) for
// the empty string.
func (r *Reader) UTF16() []uint16 {
	b := r.buf
	if len(b) < 4 {
		r.eof()
		return nil
	}
	h := int32(binary.LittleEndian.Uint32(b))
	b = b[4:]
	if h < NullLength {
		n := int(^h)
		if n > len(b) {
			r.eof()
			return nil
		}
		r.buf = b[n:]
		units := make([]uint16, n)
		for i, c := range b[:n] {
			units[i] = uint16(c)
		}
		return units
	}
	if h > 0 {
		size := uint64(h) * 2
		if size > uint64(len(b)) {
			r.eof()
			return nil
		}
		r.buf = b[size:]
		return decodeUnits(b[:size])
	}
	r.buf = b
	if h == 0 {
		return []uint16{}
	}
	return nil
}

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
	c := r.I32()
	if c <= 0 {
		if c == 0 {
			return "", r.err == nil
		}
		if c != NullLength {
			r.badLength(int64(c))
		}
		return "", false
	}
	// As .NET: a byte count past the data (or negative) is data that ends early; fewer bytes than chars, or bytes
	// that are not strict UTF-8 or decode to another number of chars, are invalid.
	size := r.I32()
	b := r.buf
	if uint32(size) > uint32(len(b)) {
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
func utf8Units(b []byte, units int) bool {
	if len(b) == units {
		// As many bytes as chars: only ASCII can be.
		return isASCII(b)
	}
	n, ok := utf8Scan(b)
	return ok && n == units
}

// utf8Scan reports whether b is strict UTF-8 (what utf8.Valid accepts: no overlong forms, encoded surrogates or
// values past U+10FFFF, no sequence cut short) and returns its length in UTF-16 code units, in one pass: a
// shift-based DFA, one table load, shift and mask a byte, with no branch on the data. Each row of utf8DFA holds,
// for every state (a multiple of 6, the shift that picks it), the next state in the 6 bits at that shift, and in
// its top two bits the code units the byte starts (0 for a continuation byte, 2 for a 4-byte lead, else 1).
func utf8Scan(b []byte) (units int, ok bool) {
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

const highBits = 0x8080808080808080

// isASCII reports whether every byte of b is below 0x80: up to 16 bytes as one or two words that may overlap (no
// loop), longer text eight bytes at a time.
func isASCII(b []byte) bool {
	n := len(b)
	if n > 16 {
		return isASCIILong(b)
	}
	if n >= 8 {
		return (binary.LittleEndian.Uint64(b)|binary.LittleEndian.Uint64(b[n-8:]))&highBits == 0
	}
	if n >= 4 {
		return (binary.LittleEndian.Uint32(b)|binary.LittleEndian.Uint32(b[n-4:]))&0x80808080 == 0
	}
	var or byte
	for _, c := range b {
		or |= c
	}
	return or < 0x80
}

// isASCIILong is [isASCII] past 16 bytes. It stops at the first word with a byte of 0x80 or above, so text that is
// not ASCII (written as Latin-1 or UTF-16 next) is not read to its end first.
//
//go:noinline
func isASCIILong(b []byte) bool {
	if binary.LittleEndian.Uint64(b[len(b)-8:])&highBits != 0 {
		return false
	}
	for ; len(b) >= 8; b = b[8:] {
		if binary.LittleEndian.Uint64(b)&highBits != 0 {
			return false
		}
	}
	return true
}

// Short strings (at most textShort bytes of UTF-8) are converted straight into a block of memory the reader
// keeps (r.text), one after the other, each its own part of the block, never written again once it is a string:
// one allocation for many strings. A block is at most textBlock bytes, and never more than the text the data left
// can hold, so what it allocates stays in proportion to the data. A string kept keeps its whole block alive.
//
// Pointers to strings (StringPtr, UTF8Ptr; what generated code reads a C# string into) point into a block of up to
// stringBoxes strings the reader keeps, so a pointer kept keeps that block, and the strings in it, alive.
const (
	stringBoxes = 32
	textShort   = 128
	textBlock   = 2048
)

// textRoom makes r.text hold at least n bytes (n <= textShort): a new block, as large as the text left can take.
//
//go:noinline
func (r *Reader) textRoom(n int) {
	r.text = make([]byte, min(textBlock, n+2*len(r.buf)))
}

// stringBox returns a pointer to s, in a block of them the reader keeps (r.boxes): one allocation for many.
func (r *Reader) stringBox(s string) *string {
	if len(r.boxes) == 0 {
		r.boxRoom()
	}
	p := &r.boxes[0]
	r.boxes = r.boxes[1:]
	*p = s
	return p
}

// boxRoom gives r.boxes a new block: at most stringBoxes, and no more than the strings the data left can hold (each
// takes at least its 4-byte header).
//
//go:noinline
func (r *Reader) boxRoom() {
	r.boxes = make([]string, min(stringBoxes, 1+len(r.buf)/4))
}

// textCopy returns a copy of b (0 < len(b) <= textShort), in r.text.
func (r *Reader) textCopy(b []byte) string {
	if len(r.text) < len(b) {
		r.textRoom(len(b))
	}
	t := r.text
	m := copy(t, b)
	r.text = t[m:]
	return unsafe.String(unsafe.SliceData(t), m)
}

// latin1Short returns the text of Latin-1 bytes (0 < len(b) <= textShort/2) as UTF-8, in r.text.
func (r *Reader) latin1Short(b []byte) string {
	n := 2 * len(b)
	if len(r.text) < n {
		r.textRoom(n)
	}
	t := r.text
	var m int
	if isASCII(b) {
		m = copy(t, b)
	} else if useRead && len(t) >= n+wireSlack {
		// The vector path (strings_wire_amd64.s), with room for its stores.
		m = readLatin1(unsafe.Pointer(unsafe.SliceData(t)), unsafe.Pointer(unsafe.SliceData(b)), len(b))
	} else {
		m = latin1UTF8(t, b)
	}
	r.text = t[m:]
	return unsafe.String(unsafe.SliceData(t), m)
}

// utf16Short returns the text of UTF-16 code units (0 < len(b)/2 <= textShort/3) as WTF-8, in r.text.
func (r *Reader) utf16Short(b []byte) string {
	n := 3 * (len(b) / 2)
	if len(r.text) < n {
		r.textRoom(n)
	}
	t := r.text
	var m int
	if useRead && len(t) >= n+wireSlack {
		// The vector path (strings_wire_amd64.s), with room for its stores.
		m = readUTF16(unsafe.Pointer(unsafe.SliceData(t)), unsafe.Pointer(unsafe.SliceData(b)), len(b)/2)
	} else {
		m = utf16UTF8(t, b)
	}
	r.text = t[m:]
	return unsafe.String(unsafe.SliceData(t), m)
}

// stringBytes is the bytes of s, without a copy (never written to).
func stringBytes(s string) []byte {
	return unsafe.Slice(unsafe.StringData(s), len(s))
}

// latin1String returns the text of Latin-1 bytes as UTF-8.
func latin1String(b []byte) string {
	if isASCII(b) {
		return string(b)
	}
	return latin1Wide(b)
}

// shortText is the most bytes of UTF-8 a short text read takes: it is converted on the stack in one pass and
// copied into its string (one allocation, not cleared first). Longer text is measured first, then converted into
// its own allocation.
const shortText = 192

// latin1Wide is [latin1String] of text with a byte of 0x80 or above: each such byte is two bytes of UTF-8.
//
//go:noinline
func latin1Wide(b []byte) string {
	if len(b) <= shortText/2 {
		var buf [shortText]byte
		return string(buf[:latin1UTF8(buf[:], b)])
	}
	high := 0
	p := b
	for ; len(p) >= 8; p = p[8:] {
		high += bits.OnesCount64(binary.LittleEndian.Uint64(p) & highBits)
	}
	for _, c := range p {
		high += int(c >> 7)
	}
	out := make([]byte, len(b)+high)
	return unsafe.String(unsafe.SliceData(out), latin1UTF8(out, b))
}

// latin1UTF8 writes Latin-1 bytes as UTF-8 to dst and returns how many bytes it wrote (all of them when dst holds
// two bytes for each byte of 0x80 or above). Eight bytes are read at a time: their leading ASCII bytes are copied at
// once (the word is stored whole: what follows them is written over or cut), and four bytes of 0x80 or above are
// written as eight bytes at once. Each word is read before any byte at its position, and the byte that ends an ASCII
// run is taken from the word: the compiler then reads the word as one load, not eight.
func latin1UTF8(dst, b []byte) int {
	i, j := 0, 0
	for i < len(b) {
		var c byte
		if i+8 <= len(b) && j+8 <= len(dst) {
			word := binary.LittleEndian.Uint64(b[i:])
			high := word & highBits
			if word&0x80 == 0 {
				binary.LittleEndian.PutUint64(dst[j:], word)
				if high == 0 {
					i, j = i+8, j+8
					continue
				}
				t := bits.TrailingZeros64(high) >> 3
				i, j = i+t, j+t
				c = byte(word >> (8 * t))
			} else if v := uint32(word); v&0x80808080 == 0x80808080 {
				// Each byte xxyyyyyy as 110000xx 10yyyyyy.
				x := widen(v)
				binary.LittleEndian.PutUint64(dst[j:], 0x80C080C080C080C0|x>>6&0x0003000300030003|(x&0x003F003F003F003F)<<8)
				i, j = i+4, j+8
				continue
			} else {
				c = byte(word)
			}
		} else {
			c = b[i]
			if c < 0x80 {
				if j >= len(dst) {
					break
				}
				dst[j] = c
				i, j = i+1, j+1
				continue
			}
		}
		if j+2 > len(dst) {
			break
		}
		dst[j] = 0xC0 | c>>6
		dst[j+1] = 0x80 | c&0x3F
		i, j = i+1, j+2
	}
	return j
}

// utf16String returns the text of UTF-16 code units (2 bytes each, little-endian) as WTF-8: UTF-8, with each
// unpaired surrogate in the 3-byte form UTF-8 reserves for it (so the text keeps every code unit; see [Reader.String]).
func utf16String(b []byte) string {
	b = b[:len(b)&^1]
	if len(b) <= 2*shortText/3 {
		// At most 3 bytes of UTF-8 a unit.
		var buf [shortText]byte
		return string(buf[:utf16UTF8(buf[:], b)])
	}
	size := 0
	for p := b; len(p) >= 2; {
		if len(p) >= 8 && binary.LittleEndian.Uint64(p)&asciiUnits == 0 {
			size += 4
			p = p[8:]
			continue
		}
		u := uint32(p[0]) | uint32(p[1])<<8
		p = p[2:]
		switch {
		case u < 0x80:
			size++
		case u < 0x800:
			size += 2
		case u&0xFC00 == 0xD800 && len(p) >= 2 && p[1]&0xFC == 0xDC:
			// A pair: one char of 4 bytes.
			size += 4
			p = p[2:]
		default:
			// Any other char, or an unpaired surrogate (in WTF-8): 3 bytes.
			size += 3
		}
	}
	out := make([]byte, size)
	return unsafe.String(unsafe.SliceData(out), utf16UTF8(out, b))
}

// utf16UTF8 writes UTF-16 code units (2 bytes each, little-endian) as WTF-8 to dst and returns how many bytes it
// wrote (all of them when dst holds their UTF-8 length). Four units are read at a time: four ASCII units are written
// as four bytes, a run of units of U+0080 to U+07FF (two bytes of UTF-8 each) four at a time, and two units of three
// bytes each at once.
func utf16UTF8(dst, p []byte) int {
	i, j := 0, 0
	for i+2 <= len(p) {
		var u uint32
		if i+8 <= len(p) && j+8 <= len(dst) {
			word := binary.LittleEndian.Uint64(p[i:])
			if word&asciiUnits == 0 {
				word = (word | word>>8) & 0x0000FFFF0000FFFF
				binary.LittleEndian.PutUint32(dst[j:], uint32(word|word>>16))
				i, j = i+8, j+4
				continue
			}
			// A unit of U+0080 to U+07FF has no bit in 0xF800 and one in 0x0780 (adding 0x7F80 to those bits
			// carries into bit 15 exactly when one is set).
			bad := word&0xF800F800F800F800 |
				((word&0x0780078007800780+0x7F807F807F807F80)&0x8000800080008000 ^ 0x8000800080008000)
			if k := bits.TrailingZeros64(bad) >> 4; k > 0 {
				// The unit xxxxxyyyyyy as 110xxxxx 10yyyyyy.
				binary.LittleEndian.PutUint64(dst[j:], 0x80C080C080C080C0|word>>6&0x001F001F001F001F|
					(word&0x003F003F003F003F)<<8)
				i, j = i+2*k, j+2*k
				continue
			}
			u = uint32(word & 0xFFFF)
			if u1 := uint32(word>>16) & 0xFFFF; u >= 0x800 && u&0xFC00 != 0xD800 && u1 >= 0x800 && u1&0xFC00 != 0xD800 {
				// Two units of three bytes each (CJK...): neither is a high surrogate, so neither starts a pair (a low
				// surrogate here has no high one before it, and is written in the same 3-byte form).
				binary.LittleEndian.PutUint64(dst[j:], utf8Of3(u)|utf8Of3(u1)<<24)
				i, j = i+4, j+6
				continue
			}
		} else {
			u = uint32(p[i]) | uint32(p[i+1])<<8
		}
		i += 2
		switch {
		case u < 0x80:
			if j >= len(dst) {
				return j
			}
			dst[j] = byte(u)
			j++
		case u < 0x800:
			if j+2 > len(dst) {
				return j
			}
			dst[j] = 0xC0 | byte(u>>6)
			dst[j+1] = 0x80 | byte(u)&0x3F
			j += 2
		case u&0xFC00 == 0xD800 && i+2 <= len(p) && p[i+1]&0xFC == 0xDC:
			if j+4 > len(dst) {
				return j
			}
			c := 0x10000 + (u-0xD800)<<10 + (uint32(p[i]) | uint32(p[i+1])<<8 - 0xDC00)
			i += 2
			dst[j] = 0xF0 | byte(c>>18)
			dst[j+1] = 0x80 | byte(c>>12)&0x3F
			dst[j+2] = 0x80 | byte(c>>6)&0x3F
			dst[j+3] = 0x80 | byte(c)&0x3F
			j += 4
		default:
			// A char of 3 bytes, or an unpaired surrogate in WTF-8 (the same form).
			if j+3 > len(dst) {
				return j
			}
			dst[j] = 0xE0 | byte(u>>12)
			dst[j+1] = 0x80 | byte(u>>6)&0x3F
			dst[j+2] = 0x80 | byte(u)&0x3F
			j += 3
		}
	}
	return j
}

// utf8Of3 returns the three bytes of UTF-8 (or WTF-8, for a surrogate) of a unit of 0x800 or above, little-endian.
func utf8Of3(u uint32) uint64 {
	return uint64(0xE0|u>>12) | uint64(0x80|u>>6&0x3F)<<8 | uint64(0x80|u&0x3F)<<16
}

// asciiUnits is the bits of four UTF-16 code units (little-endian) that are zero when all four are ASCII.
const asciiUnits = 0xFF80FF80FF80FF80

// nextUnit decodes the char at s[i:] as WTF-8: a scalar value, an unpaired surrogate (0xD800 to 0xDFFF, from its
// 3-byte form), or utf8.RuneError for a byte that is neither (size 1).
func nextUnit(s string, i int) (rune, int) {
	c, size := utf8.DecodeRuneInString(s[i:])
	if c == utf8.RuneError && size == 1 && s[i] == 0xED && i+2 < len(s) &&
		s[i+1] >= 0xA0 && s[i+1] <= 0xBF && s[i+2] >= 0x80 && s[i+2] <= 0xBF {
		return 0xD000 | rune(s[i+1]&0x3F)<<6 | rune(s[i+2]&0x3F), 3
	}
	return c, size
}

// decodeUnits returns the UTF-16 code units of b (2 bytes each, little-endian).
func decodeUnits(b []byte) []uint16 {
	units := make([]uint16, len(b)/2)
	for i := range units {
		if len(b) < 2 {
			break
		}
		units[i] = binary.LittleEndian.Uint16(b)
		b = b[2:]
	}
	return units
}

// NullableString writes s, or null when ok is false.
func (w *Writer) NullableString(s string, ok bool) {
	if !ok {
		w.Null()
		return
	}
	w.String(s)
}

// StringPtr writes *s, or null for nil.
func (w *Writer) StringPtr(s *string) {
	if s == nil {
		w.Null()
		return
	}
	w.String(*s)
}

// pairs2 and pairs2Want: the bits of four 2-byte UTF-8 chars (as 16-bit lanes, little-endian: the lead byte low)
// that tell a lead byte (110xxxxx) followed by a continuation byte (10xxxxxx).
const (
	pairs2     = 0xC0E0C0E0C0E0C0E0
	pairs2Want = 0x80C080C080C080C0
	laneHigh   = 0x0100010001000100
)

// pairBad returns, for the eight bytes of word read as four 16-bit lanes, a value whose lowest set bit lies in the
// first lane that is not a valid 2-byte UTF-8 char (a lead byte of C2 to DF, then a continuation byte); 0 when all
// four are.
func pairBad(word uint64) uint64 {
	// The lead's bits 0x1E are zero only for C0 and C1 (overlong). Per lane they are at most 0x1E: adding 0xFE
	// carries into bit 8 exactly when they are not zero.
	return (word&pairs2 ^ pairs2Want) | ((word&0x001E001E001E001E+0x00FE00FE00FE00FE)&laneHigh ^ laneHigh)
}

// widen returns four ASCII bytes as four UTF-16 code units, little-endian.
func widen(v uint32) uint64 {
	w := uint64(v)
	w = (w | w<<16) & 0x0000FFFF0000FFFF
	return (w | w<<8) & 0x00FF00FF00FF00FF
}

// UTF16 writes a string of UTF-16 code units as they are (unpaired surrogates included): nil is null.
func (w *Writer) UTF16(units []uint16) {
	if units == nil {
		w.Null()
		return
	}
	n := len(units)
	if n == 0 {
		w.U32(0)
		return
	}
	if n > maxInt32 {
		w.Fail(CodeTooLarge, "string")
		return
	}
	narrow := true
	for _, u := range units {
		if u > 0xFF {
			narrow = false
			break
		}
	}
	if narrow {
		b := w.Extend(4 + n)
		if len(b) < 4+n {
			return
		}
		binary.LittleEndian.PutUint32(b, uint32(^int32(n)))
		for i, u := range units {
			b[4+i] = byte(u)
		}
		return
	}
	b := w.Extend(4 + 2*n)
	if len(b) < 4+2*n {
		return
	}
	binary.LittleEndian.PutUint32(b, uint32(n))
	for i, u := range units {
		binary.LittleEndian.PutUint16(b[4+2*i:], u)
	}
}

// UTF8 writes a [NinoUtf8] string. Each byte of invalid UTF-8 is written as U+FFFD.
func (w *Writer) UTF8(s string) {
	n := len(s)
	if n == 0 {
		w.U32(0)
		return
	}
	bytes := stringBytes(s)
	units := n
	if !isASCII(bytes) {
		// Valid UTF-8 (the common case) is written as it is: its byte count is len(s). Anything else (an unpaired
		// surrogate in WTF-8, a byte that is not UTF-8) goes through the char by char path.
		var ok bool
		if units, ok = utf8Scan(bytes); !ok {
			w.wideUtf8(s)
			return
		}
	}
	if n > maxInt32 {
		w.Fail(CodeTooLarge, "string")
		return
	}
	b := w.Extend(8 + n)
	if len(b) < 8 {
		return
	}
	binary.LittleEndian.PutUint32(b, uint32(units))
	binary.LittleEndian.PutUint32(b[4:], uint32(n))
	copy(b[8:], s)
}

// NullableUTF8 writes a [NinoUtf8] string, or null when ok is false.
func (w *Writer) NullableUTF8(s string, ok bool) {
	if !ok {
		w.Null()
		return
	}
	w.UTF8(s)
}

// UTF8Ptr writes a [NinoUtf8] string: nil is null.
func (w *Writer) UTF8Ptr(s *string) {
	if s == nil {
		w.Null()
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
	b := w.Extend(8 + size)
	if len(b) < 8+size {
		return
	}
	binary.LittleEndian.PutUint32(b, uint32(units))
	binary.LittleEndian.PutUint32(b[4:], uint32(size))
	j := 8
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

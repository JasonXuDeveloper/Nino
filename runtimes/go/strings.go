package nino

import (
	"encoding/binary"
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
		return latin1String(b[:n]), true
	}
	if h > 0 {
		size := uint64(h) * 2
		if size > uint64(len(b)) {
			r.eof()
			return "", false
		}
		r.buf = b[size:]
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
	return &s
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

// Utf8 reads a [NinoUtf8] string; null reads as "".
func (r *Reader) Utf8() string {
	s, _ := r.NullableUtf8()
	return s
}

// Utf8Ptr reads a [NinoUtf8] string: nil for null.
func (r *Reader) Utf8Ptr() *string {
	s, ok := r.NullableUtf8()
	if !ok {
		return nil
	}
	return &s
}

// NullableUtf8 reads a [NinoUtf8] string: ok is false for null.
func (r *Reader) NullableUtf8() (s string, ok bool) {
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
	return string(b), true
}

// utf8Units reports whether b is strict UTF-8 of exactly units UTF-16 code units.
func utf8Units(b []byte, units int) bool {
	if len(b) == units {
		// As many bytes as chars: only ASCII can be.
		return isASCII(b)
	}
	n := 0
	for i := 0; i < len(b); {
		c := b[i]
		if c < utf8.RuneSelf {
			i++
			n++
			continue
		}
		r, size := utf8.DecodeRune(b[i:])
		if r == utf8.RuneError && size == 1 {
			return false
		}
		i += size
		if r >= 0x10000 {
			n += 2
		} else {
			n++
		}
	}
	return n == units
}

const highBits = 0x8080808080808080

// isASCII reports whether every byte of b is below 0x80.
func isASCII(b []byte) bool {
	for len(b) >= 8 {
		if binary.LittleEndian.Uint64(b)&highBits != 0 {
			return false
		}
		b = b[8:]
	}
	for _, c := range b {
		if c >= 0x80 {
			return false
		}
	}
	return true
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
	high := 0
	for _, c := range b {
		high += int(c >> 7)
	}
	out := make([]byte, len(b)+high)
	j := 0
	for _, c := range b {
		if c < 0x80 {
			out[j] = c
			j++
			continue
		}
		if j+1 >= len(out) {
			break
		}
		out[j] = 0xC0 | c>>6
		out[j+1] = 0x80 | c&0x3F
		j += 2
	}
	return unsafe.String(unsafe.SliceData(out), len(out))
}

// utf16String returns the text of UTF-16 code units (2 bytes each, little-endian) as WTF-8: UTF-8, with each
// unpaired surrogate in the 3-byte form UTF-8 reserves for it (so the text keeps every code unit; see [Reader.String]).
func utf16String(b []byte) string {
	b = b[:len(b)&^1]
	// The UTF-8 length first, then one allocation.
	size := 0
	for p := b; len(p) >= 2; p = p[2:] {
		u := binary.LittleEndian.Uint16(p)
		switch {
		case u < 0x80:
			size++
		case u < 0x800:
			size += 2
		case u >= 0xD800 && u < 0xDC00 && len(p) >= 4 && isLow(binary.LittleEndian.Uint16(p[2:])):
			size += 4
			p = p[2:]
		default:
			// Any other char, or an unpaired surrogate (in WTF-8): 3 bytes.
			size += 3
		}
	}
	out := make([]byte, size)
	j := 0
	for p := b; len(p) >= 2; p = p[2:] {
		u := rune(binary.LittleEndian.Uint16(p))
		if u >= 0xD800 && u < 0xDC00 && len(p) >= 4 {
			if lo := rune(binary.LittleEndian.Uint16(p[2:])); isLow(uint16(lo)) {
				u = 0x10000 + (u-0xD800)<<10 + (lo - 0xDC00)
				p = p[2:]
			}
		}
		if u >= 0xD800 && u < 0xE000 {
			// An unpaired surrogate, in WTF-8.
			if j+3 > len(out) {
				break
			}
			out[j], out[j+1], out[j+2] = 0xE0|byte(u>>12), 0x80|byte(u>>6)&0x3F, 0x80|byte(u)&0x3F
			j += 3
			continue
		}
		if j+utf8.RuneLen(u) > len(out) {
			break
		}
		j += utf8.EncodeRune(out[j:], u)
	}
	return unsafe.String(unsafe.SliceData(out), j)
}

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

func isLow(u uint16) bool { return u >= 0xDC00 && u < 0xE000 }

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

// String writes a string: one byte a char when every char is at most U+00FF, else UTF-16. Each byte of invalid
// UTF-8 is written as U+FFFD.
func (w *Writer) String(s string) {
	n := len(s)
	if n == 0 {
		w.U32(0)
		return
	}
	if !isASCII(stringBytes(s)) {
		w.wideString(s)
		return
	}
	if n > maxInt32 {
		w.Fail(CodeTooLarge, "string")
		return
	}
	b := w.Extend(4 + n)
	if len(b) < 4 {
		return
	}
	binary.LittleEndian.PutUint32(b, uint32(^int32(n)))
	copy(b[4:], s)
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

// wideString writes text that is not ASCII: Latin-1 when every char is at most U+00FF, else UTF-16.
func (w *Writer) wideString(s string) {
	chars, units := 0, 0
	latin1 := true
	for i := 0; i < len(s); {
		c, size := nextUnit(s, i)
		i += size
		chars++
		units++
		if c > 0xFF {
			latin1 = false
			if c >= 0x10000 {
				units++
			}
		}
	}
	if latin1 {
		if chars > maxInt32 {
			w.Fail(CodeTooLarge, "string")
			return
		}
		b := w.Extend(4 + chars)
		if len(b) < 4+chars {
			return
		}
		binary.LittleEndian.PutUint32(b, uint32(^int32(chars)))
		j := 4
		for i := 0; i < len(s) && j < len(b); j++ {
			c, size := nextUnit(s, i)
			i += size
			b[j] = byte(c)
		}
		return
	}
	if units > maxInt32 {
		w.Fail(CodeTooLarge, "string")
		return
	}
	b := w.Extend(4 + 2*units)
	if len(b) < 4+2*units {
		return
	}
	binary.LittleEndian.PutUint32(b, uint32(units))
	j := 4
	for i := 0; i < len(s); {
		c, size := nextUnit(s, i)
		i += size
		if c >= 0x10000 {
			if j+4 > len(b) {
				break
			}
			c -= 0x10000
			binary.LittleEndian.PutUint16(b[j:], uint16(0xD800+c>>10))
			binary.LittleEndian.PutUint16(b[j+2:], uint16(0xDC00+c&0x3FF))
			j += 4
			continue
		}
		if j+2 > len(b) {
			break
		}
		binary.LittleEndian.PutUint16(b[j:], uint16(c))
		j += 2
	}
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

// Utf8 writes a [NinoUtf8] string. Each byte of invalid UTF-8 is written as U+FFFD.
func (w *Writer) Utf8(s string) {
	n := len(s)
	if n == 0 {
		w.U32(0)
		return
	}
	bytes := stringBytes(s)
	if isASCII(bytes) {
		if n > maxInt32 {
			w.Fail(CodeTooLarge, "string")
			return
		}
		b := w.Extend(8 + n)
		if len(b) < 8 {
			return
		}
		binary.LittleEndian.PutUint32(b, uint32(n))
		binary.LittleEndian.PutUint32(b[4:], uint32(n))
		copy(b[8:], s)
		return
	}
	w.wideUtf8(s)
}

// NullableUtf8 writes a [NinoUtf8] string, or null when ok is false.
func (w *Writer) NullableUtf8(s string, ok bool) {
	if !ok {
		w.Null()
		return
	}
	w.Utf8(s)
}

// Utf8Ptr writes a utf8 string: nil is null.
func (w *Writer) Utf8Ptr(s *string) {
	if s == nil {
		w.Null()
		return
	}
	w.Utf8(*s)
}

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

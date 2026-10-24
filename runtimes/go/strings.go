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

// isASCIILong is [isASCII] past 16 bytes.
//
//go:noinline
func isASCIILong(b []byte) bool {
	high := binary.LittleEndian.Uint64(b[len(b)-8:])
	for ; len(b) >= 8; b = b[8:] {
		high |= binary.LittleEndian.Uint64(b)
	}
	return high&highBits == 0
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
// two bytes for each byte of 0x80 or above). From an ASCII byte on, eight bytes are read at a time and their leading
// ASCII bytes copied at once (the word is stored whole: what follows them is written over or cut).
func latin1UTF8(dst, b []byte) int {
	size := len(dst)
	for len(b) > 0 {
		c := b[0]
		if c < 0x80 {
			if len(b) >= 8 && len(dst) >= 8 {
				word := binary.LittleEndian.Uint64(b)
				binary.LittleEndian.PutUint64(dst, word)
				ascii := 8
				if h := word & highBits; h != 0 {
					ascii = bits.TrailingZeros64(h) >> 3
				}
				b, dst = b[ascii:], dst[ascii:]
				continue
			}
			if len(dst) < 1 {
				break
			}
			dst[0] = c
			b, dst = b[1:], dst[1:]
			continue
		}
		if len(dst) < 2 {
			break
		}
		dst[0] = 0xC0 | c>>6
		dst[1] = 0x80 | c&0x3F
		b, dst = b[1:], dst[2:]
	}
	return size - len(dst)
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
// wrote (all of them when dst holds their UTF-8 length). Four ASCII units are taken at a time.
func utf16UTF8(dst, p []byte) int {
	size := len(dst)
	for len(p) >= 2 {
		if len(p) >= 8 && len(dst) >= 4 {
			if word := binary.LittleEndian.Uint64(p); word&asciiUnits == 0 {
				word = (word | word>>8) & 0x0000FFFF0000FFFF
				binary.LittleEndian.PutUint32(dst, uint32(word|word>>16))
				p, dst = p[8:], dst[4:]
				continue
			}
		}
		u := uint32(p[0]) | uint32(p[1])<<8
		p = p[2:]
		switch {
		case u < 0x80:
			if len(dst) < 1 {
				return size - len(dst)
			}
			dst[0] = byte(u)
			dst = dst[1:]
		case u < 0x800:
			if len(dst) < 2 {
				return size - len(dst)
			}
			dst[0] = 0xC0 | byte(u>>6)
			dst[1] = 0x80 | byte(u)&0x3F
			dst = dst[2:]
		case u&0xFC00 == 0xD800 && len(p) >= 2 && p[1]&0xFC == 0xDC:
			if len(dst) < 4 {
				return size - len(dst)
			}
			c := 0x10000 + (u-0xD800)<<10 + (uint32(p[0]) | uint32(p[1])<<8 - 0xDC00)
			p = p[2:]
			dst[0] = 0xF0 | byte(c>>18)
			dst[1] = 0x80 | byte(c>>12)&0x3F
			dst[2] = 0x80 | byte(c>>6)&0x3F
			dst[3] = 0x80 | byte(c)&0x3F
			dst = dst[4:]
		default:
			// A char of 3 bytes, or an unpaired surrogate in WTF-8 (the same form).
			if len(dst) < 3 {
				return size - len(dst)
			}
			dst[0] = 0xE0 | byte(u>>12)
			dst[1] = 0x80 | byte(u>>6)&0x3F
			dst[2] = 0x80 | byte(u)&0x3F
			dst = dst[3:]
		}
	}
	return size - len(dst)
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

// wideString writes text that is not ASCII: Latin-1 when every char is at most U+00FF, else UTF-16. Either takes
// at most one byte or one code unit per byte of the text, so room for that is taken, filled in one pass over the
// text and cut to what was written; the header is written last. In UTF-8 a char of U+0080 to U+00FF is the lead
// byte 0xC2 or 0xC3 and a continuation byte: any other byte above 0x7F ends the Latin-1 pass, and the UTF-16 one
// starts over.
//
//go:noinline
func (w *Writer) wideString(s string) {
	at := len(w.buf)
	b := w.Extend(4 + len(s))
	if len(b) < 4 {
		return
	}
	if n, ok := latin1Into(b[4:], stringBytes(s)); ok {
		w.endString(at, n, true)
		return
	}
	w.buf = w.buf[:at]
	b = w.Extend(4 + 2*len(s))
	if len(b) < 4 {
		return
	}
	n := utf16Into(b[4:], s)
	w.endString(at, n, false)
}

// endString cuts the buffer to a string started at at, of count chars (bytes for Latin-1, code units for UTF-16),
// and writes its header.
func (w *Writer) endString(at, count int, latin1 bool) {
	if count > maxInt32 {
		w.buf = w.buf[:at]
		w.Fail(CodeTooLarge, "string")
		return
	}
	header, size := int32(count), 2*count
	if latin1 {
		header, size = ^header, count
	}
	w.buf = w.buf[:at+4+size]
	binary.LittleEndian.PutUint32(w.buf[at:], uint32(header))
}

// latin1Into writes the Latin-1 bytes of src (UTF-8) to dst, which holds len(src) bytes, and returns how many it
// wrote; ok is false at a char above U+00FF or a byte that is not UTF-8. From an ASCII byte on, eight bytes are read
// at a time and their leading ASCII bytes copied at once (the word is stored whole: what follows them is written
// over or cut).
func latin1Into(dst, src []byte) (n int, ok bool) {
	size := len(dst)
	for len(src) > 0 && len(dst) > 0 {
		c := src[0]
		if c < 0x80 {
			if len(src) >= 8 && len(dst) >= 8 {
				word := binary.LittleEndian.Uint64(src)
				binary.LittleEndian.PutUint64(dst, word)
				ascii := 8
				if high := word & highBits; high != 0 {
					ascii = bits.TrailingZeros64(high) >> 3
				}
				src, dst = src[ascii:], dst[ascii:]
				continue
			}
			dst[0] = c
			src, dst = src[1:], dst[1:]
			continue
		}
		if c&0xFE != 0xC2 || len(src) < 2 || src[1]&0xC0 != 0x80 {
			return size - len(dst), false
		}
		dst[0] = c<<6 | src[1]&0x3F
		src, dst = src[2:], dst[1:]
	}
	return size - len(dst), len(src) == 0
}

// utf16Into writes the UTF-16 code units of s (WTF-8, see [Reader.String]) to dst, which holds 2*len(s) bytes (no
// char takes more units than bytes), and returns how many it wrote. From an ASCII byte on, eight bytes are read at a
// time and their leading ASCII bytes widened at once (the 16 bytes stored whole, as in [latin1Into]); valid chars of two to four bytes and
// unpaired surrogates are decoded inline, anything else (bytes that are not UTF-8) by nextUnit.
func utf16Into(dst []byte, s string) int {
	src := stringBytes(s)
	size := len(dst)
	for len(src) > 0 && len(dst) >= 2 {
		c := uint32(src[0])
		switch {
		case c < 0x80:
			if len(src) >= 8 && len(dst) >= 16 {
				word := binary.LittleEndian.Uint64(src)
				binary.LittleEndian.PutUint64(dst, widen(uint32(word)))
				binary.LittleEndian.PutUint64(dst[8:], widen(uint32(word>>32)))
				ascii := 8
				if high := word & highBits; high != 0 {
					ascii = bits.TrailingZeros64(high) >> 3
				}
				src, dst = src[ascii:], dst[2*ascii:]
				continue
			}
			src = src[1:]
		case c < 0xE0 && c >= 0xC2 && len(src) >= 2 && src[1]&0xC0 == 0x80:
			c = (c&0x1F)<<6 | uint32(src[1]&0x3F)
			src = src[2:]
		case c < 0xF0 && c >= 0xE0 && len(src) >= 3 && src[1]&0xC0 == 0x80 && src[2]&0xC0 == 0x80 &&
			(c != 0xE0 || src[1] >= 0xA0):
			// ED A0 to ED BF is an unpaired surrogate (WTF-8): written as that code unit, as nextUnit reads it.
			c = (c&0x0F)<<12 | uint32(src[1]&0x3F)<<6 | uint32(src[2]&0x3F)
			src = src[3:]
		case c < 0xF5 && c >= 0xF0 && len(src) >= 4 && len(dst) >= 4 && src[1]&0xC0 == 0x80 && src[2]&0xC0 == 0x80 &&
			src[3]&0xC0 == 0x80 && (c != 0xF0 || src[1] >= 0x90) && (c != 0xF4 || src[1] < 0x90):
			c = ((c&0x07)<<18 | uint32(src[1]&0x3F)<<12 | uint32(src[2]&0x3F)<<6 | uint32(src[3]&0x3F)) - 0x10000
			src = src[4:]
			binary.LittleEndian.PutUint32(dst, (0xD800+c>>10)|(0xDC00+c&0x3FF)<<16)
			dst = dst[4:]
			continue
		default:
			// Not UTF-8: nextUnit gives U+FFFD for each such byte.
			r, n := nextUnit(s, len(s)-len(src))
			src = src[n:]
			c = uint32(r)
		}
		binary.LittleEndian.PutUint16(dst, uint16(c))
		dst = dst[2:]
	}
	return (size - len(dst)) >> 1
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

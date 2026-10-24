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
// written as eight bytes at once. As in [latin1Into], each word is read before any byte at its position.
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

// wideString writes text that is not ASCII: Latin-1 when every char is at most U+00FF, else UTF-16. Room for one
// byte per byte of the text is taken and filled with Latin-1 in one pass over it. At the first char above U+00FF
// (or byte that is not UTF-8), the Latin-1 bytes written so far are widened in place to code units, and the rest of
// the text is written as UTF-16 after them, so no byte of the text is decoded twice. The header is written last.
//
//go:noinline
func (w *Writer) wideString(s string) {
	at := len(w.buf)
	b := w.Extend(4 + len(s))
	if len(b) < 4 {
		return
	}
	n, rest := latin1Into(b[4:], stringBytes(s))
	if rest == 0 {
		w.endString(at, n, true)
		return
	}
	// Each byte left takes at most one code unit.
	w.buf = w.buf[:at+4+n]
	if w.Extend(n+2*rest) == nil {
		return
	}
	dst := w.buf[at+4:]
	widenInPlace(dst, n)
	w.endString(at, n+utf16Into(dst[2*n:], stringBytes(s)[len(s)-rest:]), false)
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
// wrote and how many bytes of src are left: none, or the bytes from the first char above U+00FF or byte that is not
// UTF-8 on. Eight bytes are read at a time and stored whole, their ASCII bytes kept (what follows is written over
// or cut); a run of chars of two bytes (U+0080 to U+00FF: C2 or C3, then a continuation byte) is taken four at a
// time.
//
// Each word is read before any byte at its position, and the byte that ends an ASCII run is taken from the word:
// the compiler then reads the word as one load, not eight.
func latin1Into(dst, src []byte) (n, rest int) {
	i, j := 0, 0
	for i < len(src) && j < len(dst) {
		var c byte
		if i+8 <= len(src) && j+8 <= len(dst) {
			word := binary.LittleEndian.Uint64(src[i:])
			high := word & highBits
			if word&0x80 == 0 {
				binary.LittleEndian.PutUint64(dst[j:], word)
				if high == 0 {
					// A run of ASCII, in a loop of its own (nothing else to keep in registers).
					i, j = i+8, j+8
					for i+8 <= len(src) && j+8 <= len(dst) {
						word = binary.LittleEndian.Uint64(src[i:])
						if word&highBits != 0 {
							break
						}
						binary.LittleEndian.PutUint64(dst[j:], word)
						i, j = i+8, j+8
					}
					continue
				}
				t := bits.TrailingZeros64(high) >> 3
				i, j = i+t, j+t
				c = byte(word >> (8 * t))
			} else if bad, units := latin1Pairs(word); bad == 0 {
				// The common case on its own, so the next word's position does not wait on the count.
				binary.LittleEndian.PutUint32(dst[j:], units)
				i, j = i+8, j+4
				continue
			} else if k := bits.TrailingZeros64(bad) >> 4; k > 0 {
				binary.LittleEndian.PutUint32(dst[j:], units)
				i, j = i+2*k, j+k
				continue
			} else {
				c = byte(word)
			}
		} else {
			c = src[i]
			if c < 0x80 {
				dst[j] = c
				i, j = i+1, j+1
				continue
			}
		}
		if c&0xFE != 0xC2 || i+1 >= len(src) || src[i+1]&0xC0 != 0x80 {
			return j, len(src) - i
		}
		dst[j] = c<<6 | src[i+1]&0x3F
		i, j = i+2, j+1
	}
	return j, len(src) - i
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

// latin1Pairs reads word as four 2-byte UTF-8 chars of U+0080 to U+00FF and returns a value whose lowest set bit
// lies in the first 16-bit lane that is not one (0 when all four are), and the four chars as Latin-1 bytes (those
// from that lane on are not chars).
func latin1Pairs(word uint64) (bad uint64, units uint32) {
	// A lead byte of C2 or C3 is 1100001x.
	bad = word&0xC0FEC0FEC0FEC0FE ^ 0x80C280C280C280C2
	// Each lane's char: 1, the lead's low bit, then the continuation's six.
	v := 0x0080008000800080 | (word&0x0001000100010001)<<6 | (word>>8)&0x003F003F003F003F
	v = (v | v>>8) & 0x0000FFFF0000FFFF
	return bad, uint32(v | v>>16)
}

// widenInPlace widens the n Latin-1 bytes at the start of dst (which holds 2*n bytes) to n UTF-16 code units, from
// the end so no byte is written over before it is read.
func widenInPlace(dst []byte, n int) {
	for ; n >= 8 && 2*n <= len(dst); n -= 8 {
		word := binary.LittleEndian.Uint64(dst[n-8:])
		binary.LittleEndian.PutUint64(dst[2*n-16:], widen(uint32(word)))
		binary.LittleEndian.PutUint64(dst[2*n-8:], widen(uint32(word>>32)))
	}
	for ; n > 0 && 2*n <= len(dst); n-- {
		binary.LittleEndian.PutUint16(dst[2*n-2:], uint16(dst[n-1]))
	}
}

// utf16Into writes the UTF-16 code units of s (WTF-8, see [Reader.String]) to dst, which holds 2*len(s) bytes (no
// char takes more units than bytes), and returns how many it wrote. Eight bytes are read at a time: their leading
// ASCII bytes are widened at once (the 16 bytes stored whole, as in [latin1Into]), and a run of 2-byte chars (U+0080
// to U+07FF: Latin, Greek, Cyrillic, Hebrew, Arabic...) is converted four at a time. Other valid chars of two to
// four bytes and unpaired surrogates are decoded inline; each other byte (not UTF-8) is U+FFFD.
func utf16Into(dst, src []byte) int {
	i, j := 0, 0
	for i < len(src) && j+2 <= len(dst) {
		var c uint32
		if i+8 <= len(src) && j+16 <= len(dst) {
			word := binary.LittleEndian.Uint64(src[i:])
			if word&0x80 == 0 {
				binary.LittleEndian.PutUint64(dst[j:], widen(uint32(word)))
				binary.LittleEndian.PutUint64(dst[j+8:], widen(uint32(word>>32)))
				high := word & highBits
				if high == 0 {
					// A run of ASCII, in a loop of its own (nothing else to keep in registers).
					i, j = i+8, j+16
					for i+8 <= len(src) && j+16 <= len(dst) {
						word = binary.LittleEndian.Uint64(src[i:])
						if word&highBits != 0 {
							break
						}
						binary.LittleEndian.PutUint64(dst[j:], widen(uint32(word)))
						binary.LittleEndian.PutUint64(dst[j+8:], widen(uint32(word>>32)))
						i, j = i+8, j+16
					}
					continue
				}
				t := bits.TrailingZeros64(high) >> 3
				i, j = i+t, j+2*t
				c = uint32(byte(word >> (8 * t)))
			} else if word&0xE0 == 0xC0 {
				// Lead xxxyyyyy and continuation 10zzzzzz: the unit yyyyyzzzzzz.
				units := (word&0x001F001F001F001F)<<6 | (word>>8)&0x003F003F003F003F
				bad := pairBad(word)
				if bad == 0 {
					// The common case on its own, so the next word's position does not wait on the count.
					binary.LittleEndian.PutUint64(dst[j:], units)
					i, j = i+8, j+8
					continue
				}
				// The first lane is a valid char unless its lead is C0 or C1, or no continuation follows.
				k := bits.TrailingZeros64(bad) >> 4
				if k == 0 {
					c = uint32(byte(word))
				} else {
					binary.LittleEndian.PutUint64(dst[j:], units)
					i, j = i+2*k, j+2*k
					continue
				}
			} else if word&0xF0 == 0xE0 {
				// Two 3-byte chars: lead 1110xxxx, then two continuation bytes; at least U+0800 (not overlong).
				// ED A0 to ED BF are unpaired surrogates (WTF-8), written as those units.
				form := word&0xC0C0F0C0C0F0 ^ 0x8080E08080E0
				u0 := uint32(word&0x0F)<<12 | uint32(word>>8&0x3F)<<6 | uint32(word>>16&0x3F)
				u1 := uint32(word>>24&0x0F)<<12 | uint32(word>>32&0x3F)<<6 | uint32(word>>40&0x3F)
				if form&0xFFFFFF != 0 || u0 < 0x800 {
					c = uint32(byte(word))
				} else if form>>24 == 0 && u1 >= 0x800 {
					binary.LittleEndian.PutUint32(dst[j:], u0|u1<<16)
					i, j = i+6, j+4
					continue
				} else {
					binary.LittleEndian.PutUint16(dst[j:], uint16(u0))
					i, j = i+3, j+2
					continue
				}
			} else if cp := uint32(word&0x07)<<18 | uint32(word>>8&0x3F)<<12 | uint32(word>>16&0x3F)<<6 |
				uint32(word>>24&0x3F); word&0xC0C0C0F8 == 0x808080F0 && cp-0x10000 < 0x100000 {
				// A 4-byte char (lead 11110xxx, three continuation bytes) of U+10000 to U+10FFFF: a surrogate pair.
				cp -= 0x10000
				binary.LittleEndian.PutUint32(dst[j:], (0xD800+cp>>10)|(0xDC00+cp&0x3FF)<<16)
				i, j = i+4, j+4
				continue
			} else {
				c = uint32(byte(word))
			}
		} else {
			c = uint32(src[i])
			if c < 0x80 {
				binary.LittleEndian.PutUint16(dst[j:], uint16(c))
				i, j = i+1, j+2
				continue
			}
		}
		// c is src[i], at least 0x80.
		switch {
		case c < 0xE0 && c >= 0xC2 && i+1 < len(src) && src[i+1]&0xC0 == 0x80:
			c = (c&0x1F)<<6 | uint32(src[i+1]&0x3F)
			i += 2
		case c < 0xF0 && c >= 0xE0 && i+2 < len(src) && src[i+1]&0xC0 == 0x80 && src[i+2]&0xC0 == 0x80 &&
			(c != 0xE0 || src[i+1] >= 0xA0):
			// ED A0 to ED BF is an unpaired surrogate (WTF-8): written as that code unit, as nextUnit reads it.
			c = (c&0x0F)<<12 | uint32(src[i+1]&0x3F)<<6 | uint32(src[i+2]&0x3F)
			i += 3
		case c < 0xF5 && c >= 0xF0 && i+3 < len(src) && j+4 <= len(dst) && src[i+1]&0xC0 == 0x80 && src[i+2]&0xC0 == 0x80 &&
			src[i+3]&0xC0 == 0x80 && (c != 0xF0 || src[i+1] >= 0x90) && (c != 0xF4 || src[i+1] < 0x90):
			c = ((c&0x07)<<18 | uint32(src[i+1]&0x3F)<<12 | uint32(src[i+2]&0x3F)<<6 | uint32(src[i+3]&0x3F)) - 0x10000
			i += 4
			binary.LittleEndian.PutUint32(dst[j:], (0xD800+c>>10)|(0xDC00+c&0x3FF)<<16)
			j += 4
			continue
		default:
			// Not UTF-8 (every valid form, and each unpaired surrogate's, is taken above): U+FFFD for the byte, as
			// nextUnit reads it.
			c = utf8.RuneError
			i++
		}
		binary.LittleEndian.PutUint16(dst[j:], uint16(c))
		j += 2
	}
	return j >> 1
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

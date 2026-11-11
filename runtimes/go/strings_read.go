package nino

import (
	"encoding/binary"
	"math/bits"
	"unsafe"
)

// Turning the bytes of a string into a Go string: the reader's blocks of text and of string pointers, and Latin-1 and
// UTF-16 decoded to UTF-8 (WTF-8 for unpaired surrogates).

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

// textRoom makes r.text hold at least n bytes from r.textAt (n <= textShort): a new block, as large as the text left
// can take.
//
//go:noinline
func (r *Reader) textRoom(n int) {
	r.text, r.textAt = make([]byte, min(textBlock, n+2*len(r.buf))), 0
}

// stringBox returns a pointer to s, in a block of them the reader keeps (r.boxes): one allocation for many.
func (r *Reader) stringBox(s string) *string {
	if r.boxAt >= len(r.boxes) {
		r.boxRoom()
	}
	p := &r.boxes[r.boxAt]
	r.boxAt++
	*p = s
	return p
}

// boxRoom gives r.boxes a new block: at most stringBoxes, and no more than the strings the data left can hold (each
// takes at least its one-byte header).
//
//go:noinline
func (r *Reader) boxRoom() {
	r.boxes, r.boxAt = make([]string, min(stringBoxes, 1+len(r.buf))), 0
}

// shortWords is the most bytes of text [Reader.NullableString] reads as two words.
const shortWords = 16

// textCopy returns a copy of b (0 < len(b) <= textShort), in r.text.
func (r *Reader) textCopy(b []byte) string {
	if len(r.text)-r.textAt < len(b) {
		r.textRoom(len(b))
	}
	t := r.text[r.textAt:]
	m := copy(t, b)
	r.textAt += m
	return unsafe.String(unsafe.SliceData(t), m)
}

// latin1Short returns the text of Latin-1 bytes (0 < len(b) <= textShort/2) as UTF-8, in r.text.
func (r *Reader) latin1Short(b []byte) string {
	n := 2 * len(b)
	if len(r.text)-r.textAt < n {
		r.textRoom(n)
	}
	t := r.text[r.textAt:]
	var m int
	if isASCII(b) {
		m = copy(t, b)
	} else {
		m = latin1Text(t, b)
	}
	r.textAt += m
	return unsafe.String(unsafe.SliceData(t), m)
}

// latin1Above is latin1Short for text known to hold a char above U+007F (NullableString's ASCII test failed):
// testing it for ASCII again cost Latin-1 text 14% on x86_64.
func (r *Reader) latin1Above(b []byte) string {
	n := 2 * len(b)
	if len(r.text)-r.textAt < n {
		r.textRoom(n)
	}
	t := r.text[r.textAt:]
	m := latin1Text(t, b)
	r.textAt += m
	return unsafe.String(unsafe.SliceData(t), m)
}

// latin1Text writes the Latin-1 bytes b as UTF-8 to t (room for 2*len(b)) and returns the bytes written.
func latin1Text(t, b []byte) int {
	if useRead && len(t) >= 2*len(b)+wireSlack {
		// The vector path (strings_wire_amd64.s), with room for its stores.
		return readLatin1(unsafe.Pointer(unsafe.SliceData(t)), unsafe.Pointer(unsafe.SliceData(b)), len(b))
	}
	return latin1UTF8(t, b)
}

// utf16Short returns the text of UTF-16 code units (0 < len(b)/2 <= textShort/3) as WTF-8, in r.text.
func (r *Reader) utf16Short(b []byte) string {
	n := 3 * (len(b) / 2)
	if len(r.text)-r.textAt < n {
		r.textRoom(n)
	}
	t := r.text[r.textAt:]
	var m int
	if useRead && len(t) >= n+wireSlack {
		// The vector path (strings_wire_amd64.s), with room for its stores.
		m = readUTF16(unsafe.Pointer(unsafe.SliceData(t)), unsafe.Pointer(unsafe.SliceData(b)), len(b)/2)
	} else {
		m = utf16UTF8(t, b)
	}
	r.textAt += m
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
		d := dst[j : j+2 : j+2]
		d[0] = 0xC0 | c>>6
		d[1] = 0x80 | c&0x3F
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
			u = uint32(binary.LittleEndian.Uint16(p[i:]))
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
			d := dst[j : j+2 : j+2]
			d[0] = 0xC0 | byte(u>>6)
			d[1] = 0x80 | byte(u)&0x3F
			j += 2
		case u&0xFC00 == 0xD800 && i+2 <= len(p) && p[i+1]&0xFC == 0xDC:
			if j+4 > len(dst) {
				return j
			}
			c := 0x10000 + (u-0xD800)<<10 + (uint32(binary.LittleEndian.Uint16(p[i:])) - 0xDC00)
			i += 2
			binary.LittleEndian.PutUint32(dst[j:], uint32(0xF0|byte(c>>18))|uint32(0x80|byte(c>>12)&0x3F)<<8|
				uint32(0x80|byte(c>>6)&0x3F)<<16|uint32(0x80|byte(c)&0x3F)<<24)
			j += 4
		default:
			// A char of 3 bytes, or an unpaired surrogate in WTF-8 (the same form).
			if j+3 > len(dst) {
				return j
			}
			d := dst[j : j+3 : j+3]
			d[0] = 0xE0 | byte(u>>12)
			d[1] = 0x80 | byte(u>>6)&0x3F
			d[2] = 0x80 | byte(u)&0x3F
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

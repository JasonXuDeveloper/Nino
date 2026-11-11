package nino

import (
	"encoding/binary"
	"unsafe"
)

// Strings.
//
// The default encoding: a header h below 2^32 whose first byte gives its size, as MessagePack's string markers do: h
// itself up to 0x7F, else 0x80 and h as a little-endian uint16 up to 0xFFFF, else 0x81 and h as a uint32; minimal
// (the shortest form that holds h), and no other first byte. 0 for null;
// h = 2n+1 (1 for empty) then n bytes when every char is at most U+00FF (Latin-1, which covers ASCII); else h = 2n
// then n UTF-16 code units. .NET
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
// [NinoUtf8] strings: c+1 as a header, c the char count (UTF-16 code units; 0 null, 1 empty, nothing after either),
// then the byte count b as a header and b bytes of UTF-8, with c <= b < 2^31. The reader accepts only strict UTF-8 (no overlong forms,
// no encoded surrogates, nothing past U+10FFFF) that decodes to exactly c code units.

// String reads a string; null reads as "" (see [Reader.NullableString] to tell them apart).
func (r *Reader) String() string {
	s, _ := r.NullableString()
	return s
}

// ReadStrings reads a sequence of strings into a []string (nil for null; a null element reads as ""): the bytes
// [WriteStrings] writes, read as a loop of [Reader.String] reads them, without a call through a func value
// per string. Short ASCII text, the common case, is read in the loop itself (the first path of
// [Reader.NullableString]), with no call a string; any other string through [Reader.String].
func ReadStrings(r *Reader) []string {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	v := make([]string, r.SliceCap(n, int(unsafe.Sizeof(""))))
	for i := 0; ; {
		// The elements v holds from i on, with no bounds check; then v grows, up to n.
		rest := v[i:]
		for j := range rest {
			b := r.buf
			// A one-byte header of Latin-1 of 1 to shortWords chars (h odd, 3 <= h < 3+2*shortWords), with 16 bytes of
			// data after it and 16 bytes of r.text left, as in NullableString.
			if len(b) > 16 && b[0]&1 != 0 && uint(b[0])-3 < 2*shortWords && len(r.text)-r.textAt >= 16 {
				p := unsafe.Pointer(&b[1])
				w0, w1 := load64(p, 0), load64(p, 8)
				m := uint(b[0]) >> 1
				k := 8 * m
				m0, m1 := ^uint64(0), uint64(0)
				if k < 64 {
					m0 = 1<<k - 1
				} else {
					m1 = 1<<(k-64) - 1
				}
				if (w0&m0|w1&m1)&highBits == 0 {
					t := unsafe.Add(unsafe.Pointer(unsafe.SliceData(r.text)), r.textAt)
					store64(t, 0, w0)
					store64(t, 8, w1)
					r.textAt += int(m)
					r.buf = b[1+m:]
					rest[j] = unsafe.String((*byte)(t), int(m))
					continue
				}
			}
			rest[j] = r.String()
			if r.err != nil {
				return nil
			}
		}
		if i += len(rest); i >= n {
			return v
		}
		v = growSlice(v, n)
	}
}

// header reads a string header (see the top of this file). The string readers decode a
// one-byte header in place instead (a call to header, above the inlining budget, cost 7-9% on short strings).
func (r *Reader) header() uint32 {
	b := r.buf
	if len(b) != 0 && b[0] < 0x80 {
		r.buf = b[1:]
		return uint32(b[0])
	}
	return r.longHeader()
}

// longHeader reads a header of three or five bytes (a first byte of 0x80 or above), or fails (and returns 0) for one
// cut by the end of the data, one with another first byte or one that a shorter form holds (not minimal).
//
//go:noinline
func (r *Reader) longHeader() uint32 {
	b := r.buf
	if len(b) == 0 {
		r.eof()
		return 0
	}
	switch b[0] {
	case header16:
		if len(b) < 3 {
			r.eof()
			return 0
		}
		v := uint32(binary.LittleEndian.Uint16(b[1:3]))
		if v < 0x80 {
			r.Fail(CodeInvalidData, "string header not minimal")
			return 0
		}
		r.buf = b[3:]
		return v
	case header32:
		if len(b) < 5 {
			r.eof()
			return 0
		}
		v := binary.LittleEndian.Uint32(b[1:5])
		if v <= 0xFFFF {
			r.Fail(CodeInvalidData, "string header not minimal")
			return 0
		}
		r.buf = b[5:]
		return v
	}
	r.Fail(CodeInvalidData, "string header out of range")
	return 0
}

// NullableString reads a string: ok is false for null.
func (r *Reader) NullableString() (s string, ok bool) {
	b := r.buf
	var h uint32
	if len(b) != 0 && b[0] < 0x80 {
		h, b = uint32(b[0]), b[1:]
		if h&1 != 0 && h-3 < 2*shortWords && len(b) >= 16 && len(r.text)-r.textAt >= 16 {
			// Latin-1 of 1 to shortWords chars, with 16 bytes of data and of r.text left: when it is ASCII, read
			// and stored as two words, the bytes past the text with them (r.text past the string is written again
			// by the next), with no call to memmove. Written here, as a call (above the inlining budget) cost more
			// than it saves.
			n := int(h >> 1)
			p := unsafe.Pointer(unsafe.SliceData(b))
			w0, w1 := load64(p, 0), load64(p, 8)
			// The bytes of the text in each word.
			k := uint(8 * n)
			m0, m1 := ^uint64(0), uint64(0)
			if k < 64 {
				m0 = 1<<k - 1
			} else {
				m1 = 1<<(k-64) - 1
			}
			if (w0&m0|w1&m1)&highBits == 0 {
				t := unsafe.Add(unsafe.Pointer(unsafe.SliceData(r.text)), r.textAt)
				store64(t, 0, w0)
				store64(t, 8, w1)
				r.textAt += n
				r.buf = b[n:]
				return unsafe.String((*byte)(t), n), true
			}
			// A char above U+007F: decoded without testing the text for ASCII again.
			r.buf = b[n:]
			return r.latin1Above(b[:n]), true
		}
	} else {
		h = r.longHeader()
		b = r.buf
	}
	n := uint64(h >> 1)
	if h&1 != 0 {
		// One byte a char; n is at most 2^31-1.
		if n > uint64(len(b)) {
			r.eof()
			return "", false
		}
		r.buf = b[n:]
		if n <= textShort/2 {
			return r.latin1Short(b[:n]), true
		}
		return latin1String(b[:n]), true
	}
	if h != 0 {
		size := n * 2
		if size > uint64(len(b)) {
			r.eof()
			return "", false
		}
		r.buf = b[size:]
		if n <= textShort/3 {
			return r.utf16Short(b[:size]), true
		}
		return utf16String(b[:size]), true
	}
	// Null, or a failed header.
	r.buf = b
	return "", false
}

// StringPtr reads a string: nil for null.
func (r *Reader) StringPtr() *string {
	s, ok := r.NullableString()
	if !ok {
		return nil
	}
	return r.stringBox(s)
}

// NullableString writes s, or null when ok is false.
func (w *Writer) NullableString(s string, ok bool) {
	if !ok {
		w.U8(0)
		return
	}
	w.String(s)
}

// StringPtr writes *s, or null for nil.
func (w *Writer) StringPtr(s *string) {
	if s == nil {
		w.U8(0)
		return
	}
	w.String(*s)
}

// The first bytes of the string headers longer than a byte: a uint16 or a uint32 follows.
const (
	header16 = 0x80
	header32 = 0x81
)

// headerSize returns the bytes the string header v takes: 1, 3 or 5.
func headerSize(v uint32) int {
	if v < 0x80 {
		return 1
	}
	if v <= 0xFFFF {
		return 3
	}
	return 5
}

// putHeader writes the string header v at the start of b (room for headerSize(v) bytes) and returns its size.
func putHeader(b []byte, v uint32) int {
	if v < 0x80 {
		b[0] = byte(v)
		return 1
	}
	if v <= 0xFFFF {
		_ = b[2]
		b[0] = header16
		binary.LittleEndian.PutUint16(b[1:3], uint16(v))
		return 3
	}
	_ = b[4]
	b[0] = header32
	binary.LittleEndian.PutUint32(b[1:5], v)
	return 5
}

// appendHeader appends the string header v.
func (w *Writer) appendHeader(v uint32) {
	if v < 0x80 {
		w.buf = append(w.buf, byte(v))
		return
	}
	if v <= 0xFFFF {
		w.buf = append(w.buf, header16, byte(v), byte(v>>8))
		return
	}
	w.buf = append(w.buf, header32, byte(v), byte(v>>8), byte(v>>16), byte(v>>24))
}

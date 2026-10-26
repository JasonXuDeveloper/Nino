package nino

import (
	"encoding/binary"
	"math/bits"
	"unicode/utf8"
	"unsafe"
)

// Writing strings: the text is converted straight into the writer's buffer, eight bytes of it at a time (Latin-1
// text with chars above ASCII a char at a time, see latin1Chars).
//
// The loops address the text and the buffer through unsafe pointers, so they carry no bounds checks: the text is
// a Go string (read only within its length), and the room written is reserved first, as much as the text can take,
// the buffer cut to the bytes written at the end. Every store in the UTF-16 loops is a whole word (8 or 16 bytes)
// whatever is left, so they run when the buffer's capacity holds that much past the most the string can take, which
// it nearly always does (a buffer that grows for a string gets [stringSlack] more); in a buffer that ends sooner the
// chars are written one at a time (utf16Tail). Latin-1 is written only within the string's bytes. A buffer with room for a string's bytes is never grown for it.
// Each word is read and written with encoding/binary's little-endian order, so the code is the same on every
// platform: the compiler makes each one load or store on hosts that allow unaligned ones (amd64, arm64, ppc64le,
// ...), byte-reversed on big-endian hosts that have such loads (ppc64, s390x), and byte by byte elsewhere. The
// reader takes untrusted bytes and stays on checked slices (strings.go).
//
// On amd64 (SSE2) and arm64 (NEON), runs of ASCII of [asciiLong] bytes or more are copied or widened to UTF-16 16
// bytes at a time in assembly (asciiCopy, widenASCII: strings_amd64.s, strings_arm64.s); with the purego build tag,
// and on every other platform, they take the same word loops as the rest of the text (strings_noasm.go).

// stringSlack is the most a store may write past a string's bytes (16 bytes of UTF-16 for eight ASCII bytes, of
// which at least one is text): the loops store whole words when this much room is left past the most a string
// can take.
const stringSlack = 16

// asciiLong is the fewest bytes of text left for which the writer calls asciiCopy or widenASCII.
const asciiLong = 64

// load64 returns the eight bytes at p+i, little-endian.
func load64(p unsafe.Pointer, i int) uint64 {
	return binary.LittleEndian.Uint64((*[8]byte)(unsafe.Add(p, i))[:])
}

// load32 returns the four bytes at p+i, little-endian.
func load32(p unsafe.Pointer, i int) uint32 {
	return binary.LittleEndian.Uint32((*[4]byte)(unsafe.Add(p, i))[:])
}

// load8 returns the byte at p+i.
func load8(p unsafe.Pointer, i int) byte {
	return *(*byte)(unsafe.Add(p, i))
}

// store64 writes v at p+i, little-endian.
func store64(p unsafe.Pointer, i int, v uint64) {
	binary.LittleEndian.PutUint64((*[8]byte)(unsafe.Add(p, i))[:], v)
}

// store32 writes v at p+i, little-endian.
func store32(p unsafe.Pointer, i int, v uint32) {
	binary.LittleEndian.PutUint32((*[4]byte)(unsafe.Add(p, i))[:], v)
}

// store16 writes v at p+i, little-endian.
func store16(p unsafe.Pointer, i int, v uint16) {
	binary.LittleEndian.PutUint16((*[2]byte)(unsafe.Add(p, i))[:], v)
}

// The loops read a word at i while eight bytes of text are left. The fewer than eight bytes at the end are read as
// the low bytes of a word, with zeros above (no valid multi-byte char has a zero byte, and the ASCII bytes a word
// holds are counted only up to the end): the text's last eight bytes, shifted, or for text of fewer than eight bytes
// loadShort. Only bytes of the text are read.

// loadShort returns the n bytes of text at p (0 < n < 8) as the low bytes of a word: two loads of four bytes, or
// three bytes, that may overlap (the bytes they share are the same).
func loadShort(p unsafe.Pointer, n int) uint64 {
	if n >= 4 {
		return uint64(load32(p, 0)) | uint64(load32(p, n-4))<<(8*(n-4)&63)
	}
	h := n >> 1
	return uint64(load8(p, 0)) | uint64(load8(p, h))<<(8*h&63) | uint64(load8(p, n-1))<<(8*(n-1)&63)
}

// asciiPrefix returns how many of the left bytes of word (left > 0) are ASCII from the first on, up to eight.
func asciiPrefix(word uint64, left int) int {
	return min(bits.TrailingZeros64(word&highBits)>>3, left)
}

// String writes a string: one byte a char when every char is at most U+00FF, else UTF-16. Each byte of invalid
// UTF-8 is written as U+FFFD (an unpaired surrogate's 3-byte form, as [Reader.String] returns it, is that unit).
//
// ASCII is copied a word at a time while it is checked; at the first word with a byte of 0x80 or above the rest is
// converted by [Writer.wideString].
func (w *Writer) String(s string) {
	n := len(s)
	at := len(w.buf)
	// Room for the header and one byte a byte (the sum wraps only on 32-bit hosts, for text that does not fit the
	// header anyway); growing, the slack too. The header takes one byte up to 63 chars; the text has at most n.
	end := at + 1 + n
	if uint(n-1) >= maxInt32 || end < at {
		w.emptyString(n)
		return
	}
	if n >= 64 {
		// Out of line, so that this path makes no call (nothing kept on the stack around one).
		w.longString(s, at)
		return
	}
	if end > cap(w.buf) {
		w.grow(1 + n + stringSlack)
	}
	w.buf = w.buf[:end]
	b := w.buf[at:end]
	src := unsafe.Pointer(unsafe.StringData(s))
	dst := unsafe.Pointer(&b[1])
	// The header, 2n+1 (one byte: n < 64), goes in the low byte of the first store, the text's first bytes above it:
	// one store fewer than the header as a byte of its own. Each word is stored before it is checked: at a byte of
	// 0x80 or above, the ASCII bytes before it are written ([Writer.wideString] writes its own header).
	h := uint64(2*n + 1)
	if n >= 8 {
		// The header and the first seven bytes, the words from the eighth byte on before the last, then the last
		// eight bytes (over the bytes before them: n >= 8, so they cover the eighth).
		word := load64(src, 0)
		store64(unsafe.Pointer(&b[0]), 0, word<<8|h)
		if high := word & highBits; high != 0 {
			w.wideString(s, at, bits.TrailingZeros64(high)>>3, 0)
			return
		}
		for i := 7; i < n-8; i += 8 {
			word := load64(src, i)
			store64(dst, i, word)
			if high := word & highBits; high != 0 {
				w.wideString(s, at, i+bits.TrailingZeros64(high)>>3, 0)
				return
			}
		}
		word = load64(src, n-8)
		store64(dst, n-8, word)
		if high := word & highBits; high != 0 {
			w.wideString(s, at, n-8+bits.TrailingZeros64(high)>>3, 0)
		}
		return
	}
	word := loadShort(src, n)
	if cap(w.buf)-end >= 7-n {
		store64(unsafe.Pointer(&b[0]), 0, word<<8|h)
	} else {
		b[0] = byte(h)
		copy(b[1:], s)
	}
	if high := word & highBits; high != 0 {
		w.wideString(s, at, bits.TrailingZeros64(high)>>3, word)
	}
}

// shortUTF8 writes a [NinoUtf8] string of n bytes (0 < n < 127) and units chars (units <= n): its two one-byte
// headers in the low bytes of the first store, the text's first bytes above them, then the text a word at a time
// (as [Writer.String] does): no byte stores and no call to memmove, which Zen 5 ran more than twice as slowly after
// the two header bytes.
func (w *Writer) shortUTF8(s string, units int) {
	n := len(s)
	at := len(w.buf)
	end := at + 2 + n
	if end > cap(w.buf) {
		w.grow(2 + n + stringSlack)
	}
	w.buf = w.buf[:end]
	b := w.buf[at:end]
	src := unsafe.Pointer(unsafe.StringData(s))
	h := uint64(units+1) | uint64(n)<<8
	if n >= 8 {
		// The headers and the first six bytes, the words from the seventh byte on before the last, then the last
		// eight bytes (over the bytes before them: n >= 8, so they cover the seventh and eighth).
		dst := unsafe.Pointer(&b[2])
		store64(unsafe.Pointer(&b[0]), 0, load64(src, 0)<<16|h)
		for i := 6; i < n-8; i += 8 {
			store64(dst, i, load64(src, i))
		}
		store64(dst, n-8, load64(src, n-8))
		return
	}
	if cap(w.buf)-end >= 6-n {
		// The headers and up to six bytes; a seventh after them.
		store64(unsafe.Pointer(&b[0]), 0, loadShort(src, n)<<16|h)
		if n == 7 {
			b[8] = s[6]
		}
		return
	}
	store16(unsafe.Pointer(&b[0]), 0, uint16(h))
	copy(b[2:], s)
}

// stringRoom is the bytes reserved for the header of a string of n bytes: that of 2n, n chars of Latin-1 or UTF-16
// (no char takes fewer bytes in UTF-8). A string of fewer chars may have a shorter header
// ([Writer.endLongString]).
func stringRoom(n int) int {
	if n < 64 {
		return 1
	}
	return headerSize(uint32(2 * n))
}

// endShort writes the header v of the string at at, whose size bytes of text (fewer than 64, room 1: see
// stringRoom) were written after a byte for its header, and cuts the buffer after it. Fewer than 64 chars take a
// one-byte header, so nothing moves; the callers test room themselves, so that this inlines.
func (w *Writer) endShort(at, size int, v uint32) {
	b := w.buf[:at+1+size]
	b[at] = byte(v)
	w.buf = b
}

// endLongString writes the header v of the string at at, whose size bytes of text were written after room bytes for
// its header (see [stringRoom]), moving the text down when the header is shorter (fewer chars than bytes, around
// 64 and 32,768 chars), and cuts the buffer after it. Out of line: only text of 64 bytes or more takes it.
//
//go:noinline
func (w *Writer) endLongString(at, room, size int, v uint32) {
	hs := headerSize(v)
	b := w.buf[at : at+room+size]
	if hs != room {
		copy(b[hs:], b[room:])
	}
	w.buf = w.buf[:at+hs+size]
	putHeader(b, v)
}

// longString is [Writer.String] of text of 64 bytes or more: its header may be longer than a byte. Where the
// platform has vector instructions it is copied 16 bytes at a time while it is ASCII (asciiCopy), then a word at a
// time as in String.
//
//go:noinline
func (w *Writer) longString(s string, at int) {
	n := len(s)
	room := stringRoom(n)
	if at+room+n > cap(w.buf) {
		w.grow(room + n + stringSlack)
	}
	w.buf = w.buf[:at+room+n]
	src := unsafe.Pointer(unsafe.StringData(s))
	dst := unsafe.Pointer(&w.buf[at+room])
	i := 0
	if hasASCIIRuns {
		i = asciiCopy(dst, src, n)
	}
	for ; i < n-8; i += 8 {
		word := load64(src, i)
		store64(dst, i, word)
		if high := word & highBits; high != 0 {
			w.wideString(s, at, i+bits.TrailingZeros64(high)>>3, 0)
			return
		}
	}
	word := load64(src, n-8)
	store64(dst, n-8, word)
	if high := word & highBits; high != 0 {
		w.wideString(s, at, n-8+bits.TrailingZeros64(high)>>3, 0)
		return
	}
	putHeader(w.buf[at:], uint32(2*n+1))
}

// emptyString writes the empty string, or fails for a string of n bytes, more than a string's header can count.
//
//go:noinline
func (w *Writer) emptyString(n int) {
	if n == 0 {
		w.U8(1)
		return
	}
	w.Fail(CodeTooLarge, "string")
}

// wideString writes the string started at at (room reserved for the header, [stringRoom], and a byte a byte) from
// byte i on, its first byte of 0x80 or above, the bytes before it ASCII and written (short is the text as a word when
// it is fewer than eight bytes, see [textWord]): Latin-1 when every char is at most U+00FF, else UTF-16. Latin-1 is
// written in place; at the first char above U+00FF (or byte that is not UTF-8), the Latin-1 bytes written so far are
// widened in place to code units, and the rest of the text is written as UTF-16 after them, so no byte of the text
// is decoded twice. The header is written last ([Writer.endShort], [Writer.endLongString]). Each count is at most len(s), which fits the
// header.
//
//go:noinline
func (w *Writer) wideString(s string, at, i int, short uint64) {
	n := len(s)
	room := stringRoom(n)
	src := unsafe.Pointer(unsafe.StringData(s))
	if useWire && cap(w.buf)-at-room >= 2*n+wireSlack {
		// The vector path, where the platform has one (strings_wire_amd64.s), for UTF-8 (WTF-8) text in a buffer
		// with room for its stores.
		dst := unsafe.Pointer(&w.buf[at+room])
		if r := wireString(dst, i, src, n); r != wireInvalid {
			if r >= 0 {
				j := i + r
				if room == 1 {
					w.endShort(at, j, uint32(2*j+1))
				} else {
					w.endLongString(at, room, j, uint32(2*j+1))
				}
				return
			}
			units := i + ^r
			widenInPlace(dst, i)
			if room == 1 {
				w.endShort(at, 2*units, uint32(2*units))
			} else {
				w.endLongString(at, room, 2*units, uint32(2*units))
			}
			return
		}
	}
	j := i
	if load8(src, i)&0xFE == 0xC2 {
		// A char of U+0080 to U+00FF (or a lead of one, the rest not checked yet) first: try Latin-1.
		i, j = latin1Into(unsafe.Pointer(&w.buf[at+room]), src, i, n)
		if i == n {
			if room == 1 {
				w.endShort(at, j, uint32(2*j+1))
			} else {
				w.endLongString(at, room, j, uint32(2*j+1))
			}
			return
		}
	}
	// Each byte left takes at most one code unit (the sum wraps only on 32-bit hosts, for text of about 2^30 bytes).
	w.buf = w.buf[:at+room+j]
	end := at + room + 2*j + 2*(n-i)
	if end < at {
		w.buf = w.buf[:at]
		w.Fail(CodeTooLarge, "string")
		return
	}
	if end > cap(w.buf) {
		w.grow(end - len(w.buf) + stringSlack)
	}
	w.buf = w.buf[:end]
	dst := unsafe.Pointer(&w.buf[at+room])
	if j > 0 {
		widenInPlace(dst, j)
	}
	units := utf16Into(dst, 2*j, cap(w.buf)-at-room, s, i, short)
	if room == 1 {
		w.endShort(at, 2*units, uint32(2*units))
	} else {
		w.endLongString(at, room, 2*units, uint32(2*units))
	}
}

// textWord returns the eight bytes of the text at src (n bytes) from i on, little-endian; fewer than eight are left
// as the low bytes, with zeros above (no valid multi-byte char has a zero byte, and the ASCII bytes a word holds are
// counted only up to n). Only bytes of the text are read: from i while eight are left, else the last eight, shifted,
// or for text of fewer than eight bytes short, all of it (from [loadShort]).
func textWord(src unsafe.Pointer, i, n int, short uint64) uint64 {
	if n-i >= 8 {
		return load64(src, i)
	}
	if n >= 8 {
		return load64(src, n-8) >> ((64 - 8*(n-i)) & 63)
	}
	return short >> (8 * i & 63)
}

// latin1Into writes the Latin-1 bytes of s (UTF-8, at src, n bytes) from byte i on at dst+i (room for n bytes from
// dst on), and returns where it stopped: n and the bytes dst holds, or the first char above U+00FF or byte that is
// not UTF-8 and the bytes dst holds before it. It writes only the bytes of the chars (a Latin-1 char is never longer
// than its UTF-8), so it needs no room past them.
func latin1Into(dst, src unsafe.Pointer, i, n int) (int, int) {
	j := i
	for {
		var run bool
		i, j, run = latin1Chars(dst, src, i, j, n)
		if !run {
			return i, j
		}
		// A run of ASCII: 16 bytes at a time where the platform has vector instructions (asciiCopy), then eight at
		// a time, outside the char loop so that loop makes no call.
		if n-i >= asciiLong {
			k := asciiCopy(unsafe.Add(dst, j), unsafe.Add(src, i), n-i)
			i, j = i+k, j+k
		}
		for n-i >= 8 {
			word := load64(src, i)
			if word&highBits != 0 {
				break
			}
			store64(dst, j, word)
			i, j = i+8, j+8
		}
	}
}

// latin1Chars is the char loop of [latin1Into]: a char at a time, which in short text with chars of U+0080 to
// U+00FF among ASCII (French, German...) beats converting a word at a time, its branches being predictable where the
// word steps wait on each other. It returns where it stopped, the bytes dst holds then, and whether it stopped after
// eight ASCII bytes in a row with at least eight more left (a run, taken a word at a time by latin1Into).
func latin1Chars(dst, src unsafe.Pointer, i, j, n int) (int, int, bool) {
	run := 0
	for i < n {
		c := load8(src, i)
		if c < 0x80 {
			*(*byte)(unsafe.Add(dst, j)) = c
			i, j = i+1, j+1
			if run++; run == 8 && n-i >= 8 {
				return i, j, true
			}
			continue
		}
		// C2 or C3, then a continuation byte: the char's low six bits, and the lead's low bit above them.
		if c&0xFE != 0xC2 || i+1 >= n {
			break
		}
		c1 := load8(src, i+1)
		if c1&0xC0 != 0x80 {
			break
		}
		*(*byte)(unsafe.Add(dst, j)) = c<<6 | c1&0x3F
		i, j, run = i+2, j+1, 0
	}
	return i, j, false
}

// widenInPlace widens the n Latin-1 bytes at dst (which holds 2*n bytes) to n UTF-16 code units, from the end so no
// byte is written over before it is read.
func widenInPlace(dst unsafe.Pointer, n int) {
	for ; n >= 8; n -= 8 {
		word := load64(dst, n-8)
		store64(dst, 2*n-16, widen(uint32(word)))
		store64(dst, 2*n-8, widen(uint32(word>>32)))
	}
	for ; n > 0; n-- {
		store16(dst, 2*n-2, uint16(load8(dst, n-1)))
	}
}

// utf16Into writes the UTF-16 code units of s (WTF-8, see [Reader.String]) from byte i on at dst+j (room bytes from
// dst on, at least j+2*(len(s)-i): no char takes more units than bytes), and returns how many units dst holds then
// (j/2 before). short is s as a word when it is fewer than eight bytes (see [textWord]). Each word holds eight bytes
// of text: its ASCII bytes from the first on are widened at once (16 bytes stored whole), a run of 2-byte chars
// (U+0080 to U+07FF: Latin, Greek, Cyrillic, Hebrew, Arabic...) is converted four at a time, and two 3-byte chars
// (CJK...) at once. Each other byte (not UTF-8) is U+FFFD.
func utf16Into(dst unsafe.Pointer, j, room int, s string, i int, short uint64) int {
	n := len(s)
	src := unsafe.Pointer(unsafe.StringData(s))
	if room-j-2*(n-i) < stringSlack {
		// No room for whole words up to the end (each byte left takes at most one unit).
		return utf16Tail(dst, j, s, i)
	}
	for {
		i, j = utf16Words(dst, j, src, i, n, short)
		if i == n {
			return j >> 1
		}
		// Eight ASCII bytes with a long run of text after them: those words 16 bytes at a time where the platform
		// has vector instructions (widenASCII), outside the word loop so that loop makes no call.
		word := load64(src, i)
		store64(dst, j, widen(uint32(word)))
		store64(dst, j+8, widen(uint32(word>>32)))
		i, j = i+8, j+16
		k := widenASCII(unsafe.Add(dst, j), unsafe.Add(src, i), n-i)
		i, j = i+k, j+2*k
	}
}

// utf16Words is the word loop of [utf16Into]: it returns where it stopped, the end of the text or eight ASCII bytes
// with at least [asciiLong] more after them, and the bytes dst holds.
func utf16Words(dst unsafe.Pointer, j int, src unsafe.Pointer, i, n int, short uint64) (int, int) {
	runs := n - 8 - asciiLong
	for i < n {
		word := textWord(src, i, n, short)
		switch {
		case word&0x80 == 0:
			store64(dst, j, widen(uint32(word)))
			store64(dst, j+8, widen(uint32(word>>32)))
			if word&highBits == 0 && n-i >= 8 {
				if hasASCIIRuns && i <= runs {
					return i, j
				}
				// The common case on its own, so the next word's position does not wait on the count.
				i, j = i+8, j+16
				continue
			}
			t := asciiPrefix(word, n-i)
			i, j = i+t, j+2*t
			continue
		case word&0xE0 == 0xC0:
			// Lead xxxyyyyy and continuation 10zzzzzz: the unit yyyyyzzzzzz. The first lane is a valid char unless
			// its lead is C0 or C1, or no continuation follows.
			units := (word&0x001F001F001F001F)<<6 | (word>>8)&0x003F003F003F003F
			bad := pairBad(word)
			if bad == 0 {
				// The common case on its own, as above.
				store64(dst, j, units)
				i, j = i+8, j+8
				continue
			}
			if k := bits.TrailingZeros64(bad) >> 4; k > 0 {
				store64(dst, j, units)
				i, j = i+2*k, j+2*k
				continue
			}
		case word&0xF0 == 0xE0:
			// Two 3-byte chars: lead 1110xxxx, then two continuation bytes; at least U+0800 (not overlong).
			// ED A0 to ED BF are unpaired surrogates (WTF-8), written as those units.
			form := word&0xC0C0F0C0C0F0 ^ 0x8080E08080E0
			u0 := uint32(word&0x0F)<<12 | uint32(word>>8&0x3F)<<6 | uint32(word>>16&0x3F)
			u1 := uint32(word>>24&0x0F)<<12 | uint32(word>>32&0x3F)<<6 | uint32(word>>40&0x3F)
			if form&0xFFFFFF == 0 && u0 >= 0x800 {
				if form>>24 == 0 && u1 >= 0x800 {
					store32(dst, j, u0|u1<<16)
					i, j = i+6, j+4
					continue
				}
				store16(dst, j, uint16(u0))
				i, j = i+3, j+2
				continue
			}
		case word&0xF8 == 0xF0:
			// A 4-byte char (lead 11110xxx, three continuation bytes) of U+10000 to U+10FFFF: a surrogate pair.
			cp := uint32(word&0x07)<<18 | uint32(word>>8&0x3F)<<12 | uint32(word>>16&0x3F)<<6 | uint32(word>>24&0x3F)
			if word&0xC0C0C0F8 == 0x808080F0 && cp-0x10000 < 0x100000 {
				cp -= 0x10000
				store32(dst, j, (0xD800+cp>>10)|(0xDC00+cp&0x3FF)<<16)
				i, j = i+4, j+4
				continue
			}
		}
		// Not UTF-8 (every valid form, and each unpaired surrogate's, is taken above): U+FFFD for the byte, as
		// nextUnit reads it.
		store16(dst, j, utf8.RuneError)
		i, j = i+1, j+2
	}
	return i, j
}

// utf16Tail is [utf16Into] a char at a time, writing only the units of the chars: for a buffer without room for
// whole words past them.
//
//go:noinline
func utf16Tail(dst unsafe.Pointer, j int, s string, i int) int {
	for i < len(s) {
		c, size := nextUnit(s, i)
		i += size
		if c >= 0x10000 {
			c -= 0x10000
			store32(dst, j, uint32(0xD800+c>>10)|uint32(0xDC00+c&0x3FF)<<16)
			j += 4
			continue
		}
		store16(dst, j, uint16(c))
		j += 2
	}
	return j >> 1
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

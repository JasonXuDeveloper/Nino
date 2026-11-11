package nino

import (
	"encoding/binary"
	"unsafe"
)

// Strings as UTF-16 code units ([Reader.UTF16], [Writer.UTF16]): the units as they are, unpaired surrogates included.

// UTF16 reads a string as its UTF-16 code units, unpaired surrogates included: nil for null, empty (not nil) for
// the empty string.
func (r *Reader) UTF16() []uint16 {
	h := r.header()
	b := r.buf
	n := uint64(h >> 1)
	if h&1 != 0 {
		if n > uint64(len(b)) {
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
	if h != 0 {
		size := n * 2
		if size > uint64(len(b)) {
			r.eof()
			return nil
		}
		r.buf = b[size:]
		return decodeUnits(b[:size])
	}
	return nil
}

// decodeUnits returns the UTF-16 code units of b (2 bytes each, little-endian): its bytes copied as the units'
// memory on a little-endian host.
func decodeUnits(b []byte) []uint16 {
	units := make([]uint16, len(b)/2)
	if littleEndian {
		copy(unsafeBytes(units), b)
		return units
	}
	for i := range units {
		if len(b) < 2 {
			break
		}
		units[i] = binary.LittleEndian.Uint16(b)
		b = b[2:]
	}
	return units
}

// unsafeBytes returns the memory of units as bytes.
func unsafeBytes(units []uint16) []byte {
	return unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(units))), 2*len(units))
}

// narrowUnits writes the code units of src (each at most 0xFF) to dst (as long) as bytes: four a word on a
// little-endian host.
func narrowUnits(dst []byte, src []uint16) {
	dst = dst[:len(src)]
	if littleEndian {
		d, s := unsafe.Pointer(unsafe.SliceData(dst)), unsafe.Pointer(unsafe.SliceData(src))
		i := 0
		for ; i+4 <= len(src); i += 4 {
			x := load64(s, 2*i)
			x = (x | x>>8) & 0x0000FFFF0000FFFF
			store32(d, i, uint32(x|x>>16))
		}
		dst, src = dst[i:], src[i:]
	}
	for i, u := range src {
		dst[i] = byte(u)
	}
}

// narrowable reports whether every unit of units is at most 0xFF: four a word on a little-endian host, stopping at
// the first word with a wider one.
func narrowable(units []uint16) bool {
	if littleEndian {
		p := unsafe.Pointer(unsafe.SliceData(units))
		i := 0
		for ; i+4 <= len(units); i += 4 {
			if load64(p, 2*i)&0xFF00FF00FF00FF00 != 0 {
				return false
			}
		}
		units = units[i:]
	}
	for _, u := range units {
		if u > 0xFF {
			return false
		}
	}
	return true
}

// UTF16 writes a string of UTF-16 code units as they are (unpaired surrogates included): nil is null.
func (w *Writer) UTF16(units []uint16) {
	if units == nil {
		w.U8(0)
		return
	}
	n := len(units)
	if n == 0 {
		w.U8(1)
		return
	}
	if n > maxInt32 {
		w.Fail(CodeTooLarge, "string")
		return
	}
	hs := headerSize(uint32(2 * n))
	if narrowable(units) {
		b := w.Extend(hs + n)
		if len(b) < hs+n {
			return
		}
		putHeader(b, uint32(2*n+1))
		narrowUnits(b[hs:], units)
		return
	}
	b := w.Extend(hs + 2*n)
	if len(b) < hs+2*n {
		return
	}
	putHeader(b, uint32(2*n))
	if littleEndian {
		copy(b[hs:], unsafeBytes(units))
		return
	}
	for i, u := range units {
		binary.LittleEndian.PutUint16(b[hs+2*i:], u)
	}
}

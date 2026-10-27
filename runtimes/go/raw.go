package nino

import (
	"encoding/binary"
	"unsafe"
)

// Primitive is the element types whose arrays and lists are written as one block of their little-endian bytes
// (bool excepted: each byte must be 0 or 1, see [ReadBools]).
type Primitive interface {
	~int8 | ~uint8 | ~int16 | ~uint16 | ~int32 | ~uint32 | ~int64 | ~uint64 | ~float32 | ~float64
}

// ReadPrimitives reads an array or list of primitives: length, then one block (nil for null). On little-endian
// hosts the block is copied as memory, with one bounds check.
func ReadPrimitives[T Primitive](r *Reader) []T {
	var zero T
	size := int(unsafe.Sizeof(zero))
	n := r.Length(size)
	if n < 0 {
		return nil
	}
	b := r.Take(n * size)
	if len(b) != n*size {
		return nil
	}
	v := make([]T, n)
	decodePrimitives(v, b)
	return v
}

// ReadPrimitivesInto reads len(dst) primitives (a block without a length) into dst.
func ReadPrimitivesInto[T Primitive](r *Reader, dst []T) {
	var zero T
	size := int(unsafe.Sizeof(zero))
	b := r.Take(len(dst) * size)
	if len(b) != len(dst)*size {
		return
	}
	decodePrimitives(dst, b)
}

// WritePrimitives writes an array or list of primitives: length, then one block (nil is null).
func WritePrimitives[T Primitive](w *Writer, v []T) {
	if v == nil {
		w.Null()
		return
	}
	w.Length(len(v))
	WritePrimitivesRaw(w, v)
}

// WritePrimitivesRaw writes primitives as one block, without a length.
func WritePrimitivesRaw[T Primitive](w *Writer, v []T) {
	var zero T
	size := int(unsafe.Sizeof(zero))
	b := w.Extend(len(v) * size)
	if len(b) != len(v)*size {
		return
	}
	encodePrimitives(b, v)
}

// decodePrimitives fills v from b (len(v) * size bytes).
func decodePrimitives[T Primitive](v []T, b []byte) {
	if len(v) == 0 {
		return
	}
	var zero T
	size := int(unsafe.Sizeof(zero))
	if littleEndian || size == 1 {
		copy(unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(v))), len(v)*size), b)
		return
	}
	decodeEach(v, b)
}

// decodeEach is decodePrimitives element by element, whatever the host's byte order.
func decodeEach[T Primitive](v []T, b []byte) {
	var zero T
	switch size := int(unsafe.Sizeof(zero)); size {
	case 1:
		for i := range v {
			x := b[i]
			v[i] = *(*T)(unsafe.Pointer(&x))
		}
	case 2:
		for i := range v {
			x := binary.LittleEndian.Uint16(b[2*i:])
			v[i] = *(*T)(unsafe.Pointer(&x))
		}
	case 4:
		for i := range v {
			x := binary.LittleEndian.Uint32(b[4*i:])
			v[i] = *(*T)(unsafe.Pointer(&x))
		}
	case 8:
		for i := range v {
			x := binary.LittleEndian.Uint64(b[8*i:])
			v[i] = *(*T)(unsafe.Pointer(&x))
		}
	}
}

// encodePrimitives writes v into b (len(v) * size bytes).
func encodePrimitives[T Primitive](b []byte, v []T) {
	if len(v) == 0 {
		return
	}
	var zero T
	size := int(unsafe.Sizeof(zero))
	if littleEndian || size == 1 {
		copy(b, unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(v))), len(v)*size))
		return
	}
	encodeEach(b, v)
}

// encodeEach is encodePrimitives element by element, whatever the host's byte order.
func encodeEach[T Primitive](b []byte, v []T) {
	var zero T
	switch size := int(unsafe.Sizeof(zero)); size {
	case 1:
		for i := range v {
			b[i] = *(*uint8)(unsafe.Pointer(&v[i]))
		}
	case 2:
		for i := range v {
			binary.LittleEndian.PutUint16(b[2*i:], *(*uint16)(unsafe.Pointer(&v[i])))
		}
	case 4:
		for i := range v {
			binary.LittleEndian.PutUint32(b[4*i:], *(*uint32)(unsafe.Pointer(&v[i])))
		}
	case 8:
		for i := range v {
			binary.LittleEndian.PutUint64(b[8*i:], *(*uint64)(unsafe.Pointer(&v[i])))
		}
	}
}

// ReadBools reads a bool[] or List<bool>: length, then a byte each (nil for null). A byte other than 0 or 1 fails
// with CodeInvalidData, as [Reader.Bool].
func ReadBools(r *Reader) []bool {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	b := r.Take(n)
	if len(b) != n {
		return nil
	}
	if !boolBytes(b) {
		r.Fail(CodeInvalidData, "invalid bool")
		return nil
	}
	// A Go bool is one byte, 0 or 1: the checked bytes are the slice's memory.
	v := make([]bool, n)
	copy(unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(v))), n), b)
	return v
}

// notBool has the bits of a byte that are clear in 0 and 1, in each byte of a word.
const notBool = 0xFEFEFEFEFEFEFEFE

// boolBytes reports whether every byte of b is 0 or 1: the bytes ORed together, eight at a time (in any byte
// order), have no bit but the lowest set.
func boolBytes(b []byte) bool {
	var acc uint64
	for len(b) >= 32 {
		acc |= binary.LittleEndian.Uint64(b) | binary.LittleEndian.Uint64(b[8:]) |
			binary.LittleEndian.Uint64(b[16:]) | binary.LittleEndian.Uint64(b[24:32])
		b = b[32:]
	}
	for len(b) >= 8 {
		acc |= binary.LittleEndian.Uint64(b)
		b = b[8:]
	}
	for _, c := range b {
		acc |= uint64(c)
	}
	return acc&notBool == 0
}

// WriteBools writes a bool[] or List<bool> (nil is null).
func WriteBools(w *Writer, v []bool) {
	if v == nil {
		w.Null()
		return
	}
	w.Length(len(v))
	b := w.Extend(len(v))
	if len(b) != len(v) {
		return
	}
	// A Go bool is 0 or 1, so its memory is the bytes, unless one was set through unsafe to another byte, which a
	// reader would reject: those become 1.
	copy(b, unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(v))), len(v)))
	if !boolBytes(b) {
		normalizeBools(b)
	}
}

// normalizeBools sets each byte of b that is not 0 to 1.
//
//go:noinline
func normalizeBools(b []byte) {
	for i, c := range b {
		if c != 0 {
			b[i] = 1
		}
	}
}

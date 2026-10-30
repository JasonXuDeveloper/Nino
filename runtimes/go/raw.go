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

// ReadPrimitiveEntries reads a dictionary of primitive keys and values as its pairs in the order of the data (nil
// for null): a key read twice fails with CodeDuplicateKey, as [ReadUniqueEntries]. On little-endian hosts, where
// Entry[K, V] has no padding (its memory is the bytes of a pair), the pairs are one block copied as memory; elsewhere
// (big-endian hosts, a pair such as int8 and int64 that is padded in memory) they are read one at a time.
func ReadPrimitiveEntries[K, V Primitive](r *Reader) []Entry[K, V] {
	var e Entry[K, V]
	size := int(unsafe.Sizeof(e))
	wire := int(unsafe.Sizeof(e.Key) + unsafe.Sizeof(e.Value))
	if !littleEndian || size != wire {
		return ReadUniqueEntries(r, wire, readPrimitive[K], readPrimitive[V])
	}
	n := r.Length(size)
	if n < 0 {
		return nil
	}
	b := r.Take(n * size)
	if len(b) != n*size {
		return nil
	}
	v := make([]Entry[K, V], n)
	copy(unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(v))), n*size), b)
	if repeated[K](unsafe.Pointer(unsafe.SliceData(v)), n, size, &r.keys) {
		r.Fail(CodeDuplicateKey, "")
		return nil
	}
	return v
}

// WritePrimitiveEntries writes a dictionary of primitive keys and values as its pairs, in order (nil is null): one
// block copied from memory where [ReadPrimitiveEntries] reads one, else one pair at a time.
func WritePrimitiveEntries[K, V Primitive](w *Writer, e []Entry[K, V]) {
	var p Entry[K, V]
	size := int(unsafe.Sizeof(p))
	if !littleEndian || size != int(unsafe.Sizeof(p.Key)+unsafe.Sizeof(p.Value)) {
		WriteEntries(w, e, writePrimitive[K], writePrimitive[V])
		return
	}
	if e == nil {
		w.Null()
		return
	}
	w.Length(len(e))
	b := w.Extend(len(e) * size)
	if len(b) != len(e)*size {
		return
	}
	copy(b, unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(e))), len(e)*size))
}

// ReadPrimitiveMap reads a dictionary of primitive keys and values into a map (nil for null): a key read twice
// fails with CodeDuplicateKey, as [ReadMap]. The pairs are one block, checked against the bytes left once, and
// each key and value is decoded at a constant offset in it (no call per pair); the map is presized as [ReadMap]
// presizes it.
func ReadPrimitiveMap[K, V Primitive](r *Reader) map[K]V {
	var k K
	var x V
	ks, vs := int(unsafe.Sizeof(k)), int(unsafe.Sizeof(x))
	wire := ks + vs
	n := r.Length(wire)
	if n < 0 {
		return nil
	}
	m := make(map[K]V, capacity(n, wire+hashSlot, len(r.buf)))
	b := r.Take(n * wire)
	if len(b) != n*wire {
		return nil
	}
	for i := 1; len(b) >= wire; i++ {
		if m[loadPrimitive[K](b)] = loadPrimitive[V](b[ks:]); len(m) != i {
			r.Fail(CodeDuplicateKey, "")
			return nil
		}
		b = b[wire:]
	}
	return m
}

// WritePrimitiveMap writes a dictionary of primitive keys and values in Go's map order (nil is null): its pairs
// as one block, each key and value stored at a constant offset in it.
func WritePrimitiveMap[K, V Primitive](w *Writer, m map[K]V) {
	if m == nil {
		w.Null()
		return
	}
	var k K
	var x V
	ks, vs := int(unsafe.Sizeof(k)), int(unsafe.Sizeof(x))
	wire := ks + vs
	w.Length(len(m))
	b := w.Extend(len(m) * wire)
	if len(b) != len(m)*wire {
		return
	}
	for key, value := range m {
		if len(b) < wire {
			return // the map grew while written (a data race)
		}
		storePrimitive(b, key)
		storePrimitive(b[ks:], value)
		b = b[wire:]
	}
}

// loadPrimitive decodes the primitive at the start of b (little-endian), whatever the host's byte order.
func loadPrimitive[T Primitive](b []byte) T {
	var v T
	switch unsafe.Sizeof(v) {
	case 1:
		x := b[0]
		v = *(*T)(unsafe.Pointer(&x))
	case 2:
		x := binary.LittleEndian.Uint16(b)
		v = *(*T)(unsafe.Pointer(&x))
	case 4:
		x := binary.LittleEndian.Uint32(b)
		v = *(*T)(unsafe.Pointer(&x))
	case 8:
		x := binary.LittleEndian.Uint64(b)
		v = *(*T)(unsafe.Pointer(&x))
	}
	return v
}

// storePrimitive encodes v at the start of b (little-endian), whatever the host's byte order.
func storePrimitive[T Primitive](b []byte, v T) {
	switch unsafe.Sizeof(v) {
	case 1:
		b[0] = *(*uint8)(unsafe.Pointer(&v))
	case 2:
		binary.LittleEndian.PutUint16(b, *(*uint16)(unsafe.Pointer(&v)))
	case 4:
		binary.LittleEndian.PutUint32(b, *(*uint32)(unsafe.Pointer(&v)))
	case 8:
		binary.LittleEndian.PutUint64(b, *(*uint64)(unsafe.Pointer(&v)))
	}
}

// readPrimitive reads one primitive in little-endian order.
func readPrimitive[T Primitive](r *Reader) T {
	var v [1]T
	ReadPrimitivesInto(r, v[:])
	return v[0]
}

// writePrimitive writes one primitive in little-endian order.
func writePrimitive[T Primitive](w *Writer, x T) {
	v := [1]T{x}
	WritePrimitivesRaw(w, v[:])
}

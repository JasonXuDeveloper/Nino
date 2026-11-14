package nino

import (
	"encoding/binary"
	"unsafe"
)

// NinoNullable is .NET's Nino.Core.NinoNullable<T>: a nullable raw T (a number, a date, a raw struct) that is written
// as its memory wherever it is (a member, an element of any collection, a field of a raw struct), 2 * size(T)
// bytes: the presence, as wide as T (0 or 1 in its first byte, zeros above), then the value, zeros when absent.
// The zero value is absent. A .NET T? is not these bytes and stays a *T.
//
// Value is written only when HasValue is set: an absent value is zeros on the wire whatever Value holds. Reading
// rejects a presence other than 0 or 1, a byte set above it and an absent value that is not zeros, as .NET's
// untrusted mode does ([Reader.NinoNullableAt]).
type NinoNullable[T any] struct {
	HasValue bool
	Value    T
}

// NinoNullableOf returns the NinoNullable that holds value.
func NinoNullableOf[T any](value T) NinoNullable[T] {
	return NinoNullable[T]{HasValue: true, Value: value}
}

// Get returns the value and whether there is one (the zero T when absent).
func (n NinoNullable[T]) Get() (T, bool) {
	if !n.HasValue {
		var zero T
		return zero, false
	}
	return n.Value, true
}

// ReadNinoNullables reads an array or list of NinoNullable<T> of a primitive T: length, then the elements as one
// block (nil for null), checked as [Reader.NinoNullableAt] checks each. Where the memory of a NinoNullable[T] is
// its bytes (little-endian hosts that align T to its size) the block is copied as memory.
func ReadNinoNullables[T Primitive](r *Reader) []NinoNullable[T] {
	var zero NinoNullable[T]
	size := int(unsafe.Sizeof(zero.Value))
	wire := 2 * size
	n := r.Length(wire)
	if n < 0 {
		return nil
	}
	b := r.Take(n * wire)
	if len(b) != n*wire {
		return nil
	}
	if !ninoNullablesValid(b, size) {
		failNinoNullables(r, b, size)
		return nil
	}
	// As many bytes of memory as of data, or fewer.
	v := make([]NinoNullable[T], n)
	if n == 0 {
		return v
	}
	if littleEndian && int(unsafe.Sizeof(zero)) == wire && int(unsafe.Offsetof(zero.Value)) == size {
		// The presence's first byte (0 or 1, checked) is HasValue, the bytes above it padding.
		copy(unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(v))), n*wire), b)
		return v
	}
	for i := range v {
		e := b[i*wire : (i+1)*wire : (i+1)*wire]
		if e[0] == 1 {
			v[i].HasValue = true
			decodePrimitives(unsafe.Slice(&v[i].Value, 1), e[size:])
		}
	}
	return v
}

// WriteNinoNullables writes an array or list of NinoNullable<T> of a primitive T: length, then the elements as one
// block (nil is null), an absent element as zeros.
func WriteNinoNullables[T Primitive](w *Writer, v []NinoNullable[T]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	var zero T
	size := int(unsafe.Sizeof(zero))
	b := w.Extend(len(v) * 2 * size)
	if len(b) != len(v)*2*size {
		return
	}
	// The value's bits through its address (a number of its size on every host), kept only where it is present.
	switch size {
	case 1:
		for i := range v {
			e, p := &v[i], uint16(0)
			if e.HasValue {
				p = 1
			}
			binary.LittleEndian.PutUint16(b[2*i:], p|(uint16(*(*uint8)(unsafe.Pointer(&e.Value)))&-p)<<8)
		}
	case 2:
		for i := range v {
			e, p := &v[i], uint32(0)
			if e.HasValue {
				p = 1
			}
			binary.LittleEndian.PutUint32(b[4*i:], p|(uint32(*(*uint16)(unsafe.Pointer(&e.Value)))&-p)<<16)
		}
	case 4:
		for i := range v {
			e, p := &v[i], uint64(0)
			if e.HasValue {
				p = 1
			}
			binary.LittleEndian.PutUint64(b[8*i:], p|(uint64(*(*uint32)(unsafe.Pointer(&e.Value)))&-p)<<32)
		}
	case 8:
		for i := range v {
			e, p := &v[i], uint64(0)
			if e.HasValue {
				p = 1
			}
			d := b[16*i : 16*i+16 : 16*i+16]
			binary.LittleEndian.PutUint64(d, p)
			binary.LittleEndian.PutUint64(d[8:], *(*uint64)(unsafe.Pointer(&e.Value))&-p)
		}
	}
}

// ninoNullablesValid reports whether every element of b (a presence and a value of size bytes each) is what
// [Reader.NinoNullableAt] accepts: the presence 0 or 1 as a whole number of size bytes, and the value zeros where
// it is 0. One pass with no branch an element: p>>1 is set for a presence above 1, and p-1 is all ones for 0, so
// (p-1)&value is the value an absent element must not have.
//
//go:noinline
func ninoNullablesValid(b []byte, size int) bool {
	var bad uint64
	switch size {
	case 1:
		for ; len(b) >= 2; b = b[2:] {
			p, x := uint64(b[0]), uint64(b[1])
			bad |= p>>1 | (p-1)&x
		}
	case 2:
		for ; len(b) >= 4; b = b[4:] {
			e := binary.LittleEndian.Uint32(b)
			p, x := uint64(e&0xFFFF), uint64(e>>16)
			bad |= p>>1 | (p-1)&x
		}
	case 4:
		for ; len(b) >= 8; b = b[8:] {
			e := binary.LittleEndian.Uint64(b)
			p, x := e&0xFFFFFFFF, e>>32
			bad |= p>>1 | (p-1)&x
		}
	case 8:
		for ; len(b) >= 16; b = b[16:] {
			p, x := binary.LittleEndian.Uint64(b), binary.LittleEndian.Uint64(b[8:])
			bad |= p>>1 | (p-1)&x
		}
	}
	return bad == 0
}

// failNinoNullables fails r with the error of the first element of b that [Reader.NinoNullableAt] rejects.
//
//go:noinline
func failNinoNullables(r *Reader, b []byte, size int) {
	for ; len(b) >= 2*size && r.err == nil; b = b[2*size:] {
		r.NinoNullableAt(b[:size], b[size:2*size])
	}
}

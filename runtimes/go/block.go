package nino

import (
	"encoding/binary"
	"unsafe"
)

// BlockValue is the values other than numbers whose arrays and lists are one block of their bytes on the wire,
// which is their memory in Go on a little-endian host: DateTime, DateOnly, TimeOnly and Decimal, whose invalid
// values are rejected as when read or written alone, and Guid, Int128 and UInt128.
type BlockValue interface {
	DateTime | DateOnly | TimeOnly | Decimal | Guid | Int128 | UInt128
}

// ReadBlock reads an array or list of a BlockValue: length, then one block (nil for null), copied as memory on a
// little-endian host, then checked. A value its reader would reject (a DateTime past MaxTicks...) fails with
// CodeInvalidData, at the offset reading the elements one by one gives.
func ReadBlock[T BlockValue](r *Reader) []T {
	var zero T
	size := int(unsafe.Sizeof(zero)) // the bytes on the wire too (see TestBlockSizes)
	n := r.Length(size)
	if n < 0 {
		return nil
	}
	start := r.buf
	b := r.Take(n * size)
	if len(b) != n*size {
		return nil
	}
	v := make([]T, n)
	if littleEndian {
		copy(unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(v))), n*size), b)
	} else {
		decodeBlock(v, b)
	}
	if i, what := invalidAt(v); i >= 0 {
		r.buf = start[(i+1)*size:]
		r.Fail(CodeInvalidData, "invalid "+what)
		return nil
	}
	return v
}

// WriteBlock writes an array or list of a BlockValue: length, then one block (nil is null). A value its writer
// would reject fails with CodeInvalidValue, after the elements before it, as writing them one by one does.
func WriteBlock[T BlockValue](w *Writer, v []T) {
	if v == nil {
		w.NullLength()
		return
	}
	if i, _ := invalidAt(v); i >= 0 {
		writeEachBlock(w, v)
		return
	}
	var zero T
	size := int(unsafe.Sizeof(zero))
	w.Length(len(v))
	b := w.Extend(len(v) * size)
	if len(b) != len(v)*size {
		return
	}
	if littleEndian {
		copy(b, unsafe.Slice((*byte)(unsafe.Pointer(unsafe.SliceData(v))), len(v)*size))
	} else {
		encodeBlock(b, v)
	}
}

// invalidAt returns the index of the first value of v that is not valid and the type's name, or -1. Each type's check
// is a function of its own, out of line: inlined here, every instantiation compiled all four loops, and the DateOnly
// loop, its instructions unchanged, ran 6.8 % slower on Neoverse-N2 at its new place.
func invalidAt[T BlockValue](v []T) (int, string) {
	switch v := any(v).(type) {
	case []DateTime:
		return invalidDateTime(v), "DateTime"
	case []DateOnly:
		return invalidDateOnly(v), "DateOnly"
	case []TimeOnly:
		return invalidTimeOnly(v), "TimeOnly"
	case []Decimal:
		return invalidDecimal(v), "decimal"
	}
	return -1, ""
}

// invalidDateTime, invalidTimeOnly and invalidDecimal return the index of the first value of v that is not valid, or
// -1. Four values are tested per step, one compare each (the Valid methods), so the loop's own branches are a quarter
// as many; the step that fails is searched value by value.
//
//go:noinline
func invalidDateTime(v []DateTime) int {
	w := v
	for len(w) >= 4 && w[0].Valid() && w[1].Valid() && w[2].Valid() && w[3].Valid() {
		w = w[4:]
	}
	for i, d := range w {
		if !d.Valid() {
			return len(v) - len(w) + i
		}
	}
	return -1
}

//go:noinline
func invalidTimeOnly(v []TimeOnly) int {
	w := v
	for len(w) >= 4 && w[0].Valid() && w[1].Valid() && w[2].Valid() && w[3].Valid() {
		w = w[4:]
	}
	for i, t := range w {
		if !t.Valid() {
			return len(v) - len(w) + i
		}
	}
	return -1
}

//go:noinline
func invalidDecimal(v []Decimal) int {
	w := v
	for len(w) >= 4 && w[0].Valid() && w[1].Valid() && w[2].Valid() && w[3].Valid() {
		w = w[4:]
	}
	for i, d := range w {
		if !d.Valid() {
			return len(v) - len(w) + i
		}
	}
	return -1
}

// invalidDateOnly is the same check for DateOnly, one value at a time: four a step (compare-branches, or two
// words of two lanes with one branch) wrote 8-19 % slower on Xeon 8370C, EPYC 7763 and 9V74.
//
//go:noinline
func invalidDateOnly(v []DateOnly) int {
	for i, d := range v {
		if !d.Valid() {
			return i
		}
	}
	return -1
}

// writeEachBlock writes v element by element: the writer's checks fail where they would for each value alone.
//
//go:noinline
func writeEachBlock[T BlockValue](w *Writer, v []T) {
	switch v := any(v).(type) {
	case []DateTime:
		writeSlice(w, v, (*Writer).DateTime)
	case []DateOnly:
		writeSlice(w, v, (*Writer).DateOnly)
	case []TimeOnly:
		writeSlice(w, v, (*Writer).TimeOnly)
	case []Decimal:
		writeSlice(w, v, (*Writer).Decimal)
	}
}

// decodeBlock fills v from b (little-endian, len(v) * the size of T) on any host.
func decodeBlock[T BlockValue](v []T, b []byte) {
	switch v := any(v).(type) {
	case []DateTime:
		for i := range v {
			v[i] = DateTime(binary.LittleEndian.Uint64(b[8*i:]))
		}
	case []DateOnly:
		for i := range v {
			v[i] = DateOnly(int32(binary.LittleEndian.Uint32(b[4*i:])))
		}
	case []TimeOnly:
		for i := range v {
			v[i] = TimeOnly(int64(binary.LittleEndian.Uint64(b[8*i:])))
		}
	case []Decimal:
		for i := range v {
			v[i] = DecimalAt(b[16*i:])
		}
	case []Guid:
		for i := range v {
			copy(v[i][:], b[16*i:])
		}
	case []Int128:
		for i := range v {
			v[i] = Int128{binary.LittleEndian.Uint64(b[16*i:]), binary.LittleEndian.Uint64(b[16*i+8:])}
		}
	case []UInt128:
		for i := range v {
			v[i] = UInt128{binary.LittleEndian.Uint64(b[16*i:]), binary.LittleEndian.Uint64(b[16*i+8:])}
		}
	}
}

// encodeBlock writes v into b (little-endian, len(v) * the size of T) on any host.
func encodeBlock[T BlockValue](b []byte, v []T) {
	switch v := any(v).(type) {
	case []DateTime:
		for i, d := range v {
			binary.LittleEndian.PutUint64(b[8*i:], uint64(d))
		}
	case []DateOnly:
		for i, d := range v {
			binary.LittleEndian.PutUint32(b[4*i:], uint32(d))
		}
	case []TimeOnly:
		for i, t := range v {
			binary.LittleEndian.PutUint64(b[8*i:], uint64(t))
		}
	case []Decimal:
		for i, d := range v {
			d.Put(b[16*i:])
		}
	case []Guid:
		for i := range v {
			copy(b[16*i:], v[i][:])
		}
	case []Int128:
		for i, x := range v {
			binary.LittleEndian.PutUint64(b[16*i:], x.Lo)
			binary.LittleEndian.PutUint64(b[16*i+8:], x.Hi)
		}
	case []UInt128:
		for i, x := range v {
			binary.LittleEndian.PutUint64(b[16*i:], x.Lo)
			binary.LittleEndian.PutUint64(b[16*i+8:], x.Hi)
		}
	}
}

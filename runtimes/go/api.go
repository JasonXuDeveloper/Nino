package nino

import "sync"

var (
	readers = sync.Pool{New: func() any { return new(Reader) }}
	writers = sync.Pool{New: func() any { return &Writer{buf: make([]byte, 0, 256)} }}
)

// Deserialize reads one value written by NinoSerializer.Serialize (or [Serialize]) with read, the value's
// generated read function. On any failure it returns the zero value and an *Error: never part of a value.
// Bytes after the value are ignored, as in .NET.
func Deserialize[T any](data []byte, read func(*Reader) T) (T, error) {
	r := readers.Get().(*Reader)
	r.Reset(data)
	v := read(r)
	err := r.Err()
	r.Reset(nil)
	readers.Put(r)
	if err != nil {
		var zero T
		return zero, err
	}
	return v, nil
}

// DeserializeInto reads one value into *v with read, the value's generated ReadXInto function, as
// NinoDeserializer.Deserialize(data, ref value) does in .NET: the objects *v holds are read into (their members in
// place), slices keep their storage and read their elements into the old ones by index, a polymorphic member is read
// into only when the data holds the type it holds, and tracked objects are read new. Bytes after the value are
// ignored. On a failure it returns an *Error, and *v holds what was read before it, with zero values from there on
// (the Reader's reads return zeros after the first failure), but no partial object of a different type.
func DeserializeInto[T any](data []byte, v *T, read func(*Reader, *T)) error {
	r := readers.Get().(*Reader)
	r.Reset(data)
	read(r, v)
	err := r.Err()
	r.Reset(nil)
	readers.Put(r)
	return err
}

// DeserializeExact is [Deserialize] that also fails (CodeInvalidData) when bytes follow the value.
func DeserializeExact[T any](data []byte, read func(*Reader) T) (T, error) {
	r := readers.Get().(*Reader)
	r.Reset(data)
	v := read(r)
	if r.err == nil && len(r.buf) != 0 {
		r.Fail(CodeInvalidData, "bytes after the value")
	}
	err := r.Err()
	r.Reset(nil)
	readers.Put(r)
	if err != nil {
		var zero T
		return zero, err
	}
	return v, nil
}

// Serialize writes v with write, the value's generated write function, into a new slice (what
// NinoSerializer.Serialize writes in .NET). On a failure it returns nil and an *Error.
func Serialize[T any](v T, write func(*Writer, T)) ([]byte, error) {
	w := writers.Get().(*Writer)
	w.Reset()
	write(w, v)
	var out []byte
	err := w.Err()
	if err == nil {
		out = make([]byte, len(w.buf))
		copy(out, w.buf)
	}
	release(w)
	return out, err
}

// Append is [Serialize] appending to dst: with room in dst, it allocates nothing. On a failure it returns dst
// unchanged (its length; the bytes past it may have been written) and an *Error.
func Append[T any](dst []byte, v T, write func(*Writer, T)) ([]byte, error) {
	w := writers.Get().(*Writer)
	own := w.buf
	w.Reset()
	w.buf = dst
	write(w, v)
	out, err := w.buf, w.Err()
	w.buf = own
	release(w)
	if err != nil {
		return dst, err
	}
	return out, nil
}

// release returns w to the pool, unless its buffer grew large.
func release(w *Writer) {
	if cap(w.buf) > 1<<20 {
		return
	}
	w.Reset()
	writers.Put(w)
}

package nino

import (
	"reflect"
	"sync"
	"sync/atomic"
)

// Marshal writes v with the codec plan of its type, built from its struct tags (see the README, "Struct tags").
// v's type decides the encoding: a pointer to a struct is a class (a presence byte or a type id, then its body), a
// struct value is a struct, a slice a sequence, and so on, exactly as for a field of that type. A type the tags
// cannot map returns a *TagError; a value that cannot be written (an unregistered type in an interface, a length
// past 2^31-1...) an *Error.
func Marshal(v any) ([]byte, error) {
	return MarshalAppend(nil, v)
}

// MarshalAs is [Marshal] of v as a T, for a T that Marshal cannot see in an any: an interface (a polymorphic root,
// written behind its type id), or a nil.
func MarshalAs[T any](v T) ([]byte, error) {
	return marshal(nil, reflect.TypeFor[T](), reflect.ValueOf(&v).Elem())
}

// MarshalAppend is [Marshal] appending to dst.
func MarshalAppend(dst []byte, v any) ([]byte, error) {
	rv := reflect.ValueOf(v)
	if !rv.IsValid() {
		return dst, &TagError{Detail: "Marshal(nil): no type to write (see MarshalAs)"}
	}
	return marshal(dst, rv.Type(), rv)
}

func marshal(dst []byte, t reflect.Type, rv reflect.Value) ([]byte, error) {
	c, err := codecOf(t, variantDefault)
	if err != nil {
		return dst, err
	}
	// The codecs take addressable values: a copy, unless every value they reach is behind the root (a pointer,
	// a slice, a map or an interface).
	tmp := rv
	switch t.Kind() {
	case reflect.Pointer, reflect.Slice, reflect.Map, reflect.Interface:
	default:
		if !rv.CanAddr() {
			tmp = reflect.New(t).Elem()
			tmp.Set(rv)
		}
	}
	w := writers.Get().(*Writer)
	own := w.buf
	w.Reset()
	w.buf = dst
	c.write(w, tmp)
	out, werr := w.buf, w.Err()
	w.buf = own
	release(w)
	if werr != nil {
		return dst, werr
	}
	return out, nil
}

// Unmarshal reads data into *v (v a non-nil pointer) with the codec plan of v's element type: the inverse of
// [Marshal] of a value of that type. On a failure *v is left unchanged and the error is an *Error (bad data) or a
// *TagError (a type the tags cannot map). Bytes after the value are ignored, as in .NET; see [UnmarshalExact].
func Unmarshal(data []byte, v any) error { return unmarshal(data, v, false) }

// UnmarshalExact is [Unmarshal] that also fails (CodeInvalidData) when bytes follow the value.
func UnmarshalExact(data []byte, v any) error { return unmarshal(data, v, true) }

func unmarshal(data []byte, v any, exact bool) error {
	rv := reflect.ValueOf(v)
	if rv.Kind() != reflect.Pointer || rv.IsNil() {
		return &TagError{Type: reflect.TypeOf(v), Detail: "Unmarshal needs a non-nil pointer"}
	}
	t := rv.Type().Elem()
	c, err := codecOf(t, variantDefault)
	if err != nil {
		return err
	}
	tmp := reflect.New(t).Elem()
	r := readers.Get().(*Reader)
	r.Reset(data)
	c.read(r, tmp)
	if exact && r.err == nil && len(r.buf) != 0 {
		r.Fail(CodeInvalidData, "bytes after the value")
	}
	rerr := r.Err()
	r.Reset(nil)
	readers.Put(r)
	if rerr != nil {
		return rerr
	}
	rv.Elem().Set(tmp)
	return nil
}

// ReadValue reads into *v (a non-nil pointer) with v's tag plan, from inside a hand-written or generated read
// function. A type the tags cannot map fails the reader with CodeInvalidValue.
func ReadValue(r *Reader, v any) {
	rv := reflect.ValueOf(v)
	if rv.Kind() != reflect.Pointer || rv.IsNil() {
		r.Fail(CodeInvalidValue, "ReadValue needs a non-nil pointer")
		return
	}
	c, err := codecOf(rv.Type().Elem(), variantDefault)
	if err != nil {
		r.Fail(CodeInvalidValue, err.Error())
		return
	}
	c.read(r, rv.Elem())
}

// WriteValue writes v with its type's tag plan, from inside a hand-written or generated write function. A type the
// tags cannot map fails the writer with CodeInvalidValue.
func WriteValue(w *Writer, v any) {
	rv := reflect.ValueOf(v)
	if !rv.IsValid() {
		w.Fail(CodeInvalidValue, "WriteValue(nil)")
		return
	}
	c, err := codecOf(rv.Type(), variantDefault)
	if err != nil {
		w.Fail(CodeInvalidValue, err.Error())
		return
	}
	tmp := reflect.New(rv.Type()).Elem()
	tmp.Set(rv)
	c.write(w, tmp)
}

// Check builds the plan of t (and of every type it reaches) and returns the *TagError, if any: call it from a test
// or at init to find a bad tag before the first message.
func Check(t reflect.Type) error {
	if t == nil {
		return &TagError{Detail: "Check(nil)"}
	}
	_, err := codecOf(t, variantDefault)
	return err
}

// ---------------------------------------------------------------- the registry of polymorphic types

// A registered type can be read from an interface field (object, an abstract class, an interface in .NET) whose
// Go interface it implements. Reading an id that is not registered, or whose type does not fit the field, fails
// with CodeUnknownType: data never chooses a type the program did not register.
type registered struct {
	id int32
	t  reflect.Type // *T for a class, T for a struct

	once sync.Once
	c    *codec
	err  error
}

func (e *registered) codec() (*codec, error) {
	e.once.Do(func() { e.c, e.err = codecOf(e.t, variantDefault) })
	return e.c, e.err
}

type registry struct {
	byID   map[int32]*registered
	byType map[reflect.Type]*registered
}

var (
	registryMu sync.Mutex
	registryV  atomic.Pointer[registry]
)

func lookupRegistered(id int32) *registered {
	if r := registryV.Load(); r != nil {
		return r.byID[id]
	}
	return nil
}

func lookupRegisteredType(t reflect.Type) *registered {
	if r := registryV.Load(); r != nil {
		return r.byType[t]
	}
	return nil
}

// Register registers types that interface fields may hold, by a value of each: a nil pointer (*Dog)(nil) for a
// class, a zero value Circle{} for a struct. Each type's id is its layout's typeId or its NinoTypeID. The plan of
// each type is built and checked now. Registering a type again is a no-op; two types with one id are an error.
func Register(values ...any) error {
	for _, v := range values {
		t := reflect.TypeOf(v)
		if t == nil {
			return &TagError{Detail: "Register(nil)"}
		}
		st := t
		if t.Kind() == reflect.Pointer {
			st = t.Elem()
		}
		if st.Kind() != reflect.Struct {
			return &TagError{Type: t, Detail: "only structs and pointers to structs can be registered"}
		}
		c, err := codecOf(t, variantDefault)
		if err != nil {
			return err
		}
		if c.plan == nil || !c.plan.hasID {
			return &TagError{Type: t, Detail: "no type id: declare typeId in NinoLayout, implement NinoTypeID, or use RegisterID"}
		}
		if err := RegisterID(c.plan.id, v); err != nil {
			return err
		}
	}
	return nil
}

// RegisterID is [Register] of one type under the id given (for a closed generic type, whose layout publishes
// typeId 0: its id is TypeID of its .NET name).
func RegisterID(id int32, v any) error {
	t := reflect.TypeOf(v)
	if t == nil {
		return &TagError{Detail: "RegisterID(nil)"}
	}
	if id == NullTypeID {
		return &TagError{Type: t, Detail: "type id 0 is null"}
	}
	c, err := codecOf(t, variantDefault)
	if err != nil {
		return err
	}
	if c.kind != kClass && c.kind != kStruct && c.kind != kRawStruct {
		return &TagError{Type: t, Detail: "only structs and pointers to classes can be registered"}
	}
	registryMu.Lock()
	defer registryMu.Unlock()
	old := registryV.Load()
	next := &registry{byID: map[int32]*registered{}, byType: map[reflect.Type]*registered{}}
	if old != nil {
		for k, e := range old.byID {
			next.byID[k] = e
		}
		for k, e := range old.byType {
			next.byType[k] = e
		}
	}
	if e, ok := next.byID[id]; ok {
		if e.t == t {
			return nil
		}
		return &TagError{Type: t, Detail: "type id " + hex32(id) + " is already " + e.t.String() + "'s"}
	}
	if e, ok := next.byType[t]; ok {
		return &TagError{Type: t, Detail: "already registered with id " + hex32(e.id)}
	}
	e := &registered{id: id, t: t}
	next.byID[id] = e
	next.byType[t] = e
	registryV.Store(next)
	return nil
}

func hex32(id int32) string {
	const digits = "0123456789ABCDEF"
	b := []byte("0x00000000")
	u := uint32(id)
	for i := 9; i >= 2; i-- {
		b[i] = digits[u&15]
		u >>= 4
	}
	return string(b)
}

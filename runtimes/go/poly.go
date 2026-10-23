package nino

import (
	"slices"
	"strconv"
)

// Polymorphism.
//
// A non-sealed class, an abstract class, an interface or object is written behind an int32 type id (0 for null):
// the FNV-1a hash of the .NET type's full name ([TypeID]). A reader only ever creates a type that the caller
// allows where it reads: generated code switches over the ids of the types assignable to the declared type, and
// [ReadAny] takes the allowed set ([TypeSet]) explicitly, so data cannot name any other type.

// TypeID returns the type id of the .NET type named name in its runtime form ("Namespace.Outer+Inner", generic
// arguments in angle brackets as Nino names them): FNV-1a over its UTF-16 code units, low byte first, never 0.
func TypeID(name string) int32 {
	const (
		offset = 2166136261
		prime  = 16777619
	)
	h := uint32(offset)
	unit := func(u uint16) {
		h ^= uint32(u & 0xFF)
		h *= prime
		h ^= uint32(u >> 8)
		h *= prime
	}
	for _, c := range name {
		if c >= 0x10000 {
			c -= 0x10000
			unit(uint16(0xD800 + c>>10))
			unit(uint16(0xDC00 + c&0x3FF))
			continue
		}
		unit(uint16(c))
	}
	if h == 0 {
		return 1
	}
	return int32(h)
}

// Typed is implemented by the generated types written behind a type id: the id of the .NET type.
type Typed interface {
	NinoTypeID() int32
}

// Subtype is one concrete type that can be read and written behind a type id; see [ClassType] and [StructType].
type Subtype interface {
	// ID returns the type id.
	ID() int32
	read(r *Reader) any
	write(w *Writer, v any) bool
}

type classType[T any] struct {
	id int32
	c  *Class[T]
}

// ClassType returns the Subtype of a class: its body read and written by c (counting the depth, deferred as
// .NET defers it). Values are *T.
func ClassType[T any](id int32, c *Class[T]) Subtype { return &classType[T]{id, c} }

func (t *classType[T]) ID() int32 { return t.id }

func (t *classType[T]) read(r *Reader) any { return ReadClass(r, t.c) }

func (t *classType[T]) write(w *Writer, v any) bool {
	p, ok := v.(*T)
	if !ok {
		return false
	}
	if p == nil {
		w.I32(NullTypeID)
		return true
	}
	w.I32(t.id)
	WriteClass(w, p, t.c)
	return true
}

type structType[T any] struct {
	id     int32
	reader func(*Reader, *T)
	writer func(*Writer, *T)
}

// StructType returns the Subtype of a struct written behind a type id (in an object or interface slot): its body
// does not count toward the depth. Values are T.
func StructType[T any](id int32, read func(*Reader, *T), write func(*Writer, *T)) Subtype {
	return &structType[T]{id, read, write}
}

func (t *structType[T]) ID() int32 { return t.id }

func (t *structType[T]) read(r *Reader) any {
	var v T
	t.reader(r, &v)
	return v
}

func (t *structType[T]) write(w *Writer, v any) bool {
	s, ok := v.(T)
	if !ok {
		return false
	}
	w.I32(t.id)
	t.writer(w, &s)
	return true
}

// TypeSet is the set of types allowed behind a type id where a value is read or written: the concrete types
// assignable to the declared type. It is immutable once built.
type TypeSet struct {
	ids   []int32
	types []Subtype // by ids
}

// NewTypeSet returns the set of types. Two types with one id panic: a programming error, found when the set is
// built (at init), never while reading.
func NewTypeSet(types ...Subtype) *TypeSet {
	s := &TypeSet{}
	sorted := slices.Clone(types)
	slices.SortFunc(sorted, func(a, b Subtype) int {
		switch {
		case a.ID() < b.ID():
			return -1
		case a.ID() > b.ID():
			return 1
		}
		return 0
	})
	for i, t := range sorted {
		if t.ID() == NullTypeID {
			panic("nino: type id 0 is null")
		}
		if i > 0 && sorted[i-1].ID() == t.ID() {
			panic("nino: two types with id " + strconv.Itoa(int(t.ID())))
		}
		s.ids = append(s.ids, t.ID())
		s.types = append(s.types, t)
	}
	return s
}

// Lookup returns the type of id in the set, or nil.
func (s *TypeSet) Lookup(id int32) Subtype {
	if s == nil {
		return nil
	}
	if i, ok := slices.BinarySearch(s.ids, id); ok {
		return s.types[i]
	}
	return nil
}

// ReadAny reads a value written behind a type id: nil for null, else a value of a type in allowed (*T for a
// class, T for a struct). Any other id fails with CodeUnknownType.
func ReadAny(r *Reader, allowed *TypeSet) any {
	id := r.I32()
	if id == NullTypeID || r.err != nil {
		return nil
	}
	t := allowed.Lookup(id)
	if t == nil {
		r.UnknownType(id)
		return nil
	}
	return t.read(r)
}

// WriteAny writes v behind its type id: 0 for nil (or a nil pointer), else the id and body of its type, which must
// be in allowed (CodeUnknownType otherwise).
func WriteAny(w *Writer, v Typed, allowed *TypeSet) {
	if v == nil {
		w.I32(NullTypeID)
		return
	}
	id := v.NinoTypeID()
	t := allowed.Lookup(id)
	if t == nil {
		w.Fail(CodeUnknownType, "type id "+strconv.Itoa(int(id)))
		return
	}
	if !t.write(w, v) {
		w.Fail(CodeUnknownType, "type id "+strconv.Itoa(int(id))+" is another type")
	}
}

// UnknownType fails for a type id that is not one of the types allowed where it was read: the default case of a
// generated dispatch.
//
//go:noinline
func (r *Reader) UnknownType(id int32) {
	r.Fail(CodeUnknownType, "type id 0x"+strconv.FormatUint(uint64(uint32(id)), 16))
}

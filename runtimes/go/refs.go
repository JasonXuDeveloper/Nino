package nino

import "strconv"

// Reference tracking (wire-format spec §7.9).
//
// An object of a type declared with trackReferences is written behind a reference header, an int32: 0 for null,
// -1 for an object not written before (its body follows), or n > 0 for the n-th object of a tracked type written
// in the payload (numbered from 1, in the order of their headers). A reader records each new object as it creates
// it, before its body is read (a deferred body too, as .NET does), so a body can refer to the object holding it.
//
// Three forms, all built from the functions here by generated code:
//   - a tracked class without subtypes: the header, then the body ([ReadTracked], [WriteTracked]);
//   - a tracked class with subtypes or interface: the header, then (for -1) the type id and the body ([Reader.Ref],
//     [Back], [ReadTrackedNew], [Writer.Ref]);
//   - a slot that does not track (object, an untracked base) holding a tracked type: the type id, then the header,
//     then the body for -1; null is a type id of 0, never a header of 0 ([ReadTrackedClass], [WriteTypedTracked]).
//
// Hostile data: a header is checked against the objects read so far (a back reference names one of them, never a
// later one), and the object it names must be of a type the slot allows. Every new object takes at least its 4
// header bytes, so the table grows with the data and no faster.

// Ref reads a reference header: 0 (null), -1 (a new object) or the number of an object read before. Any other
// value fails with CodeInvalidData, and Ref returns 0.
func (r *Reader) Ref() int32 {
	h := r.I32()
	if h < -1 || (h > 0 && (r.ctx == nil || int(h) > len(r.ctx.refs))) {
		r.badRef(h)
		return 0
	}
	return h
}

//go:noinline
func (r *Reader) badRef(h int32) {
	r.Fail(CodeInvalidData, "object reference "+strconv.Itoa(int(h)))
}

// Track records a new object of a tracked type, as it is created: the next back reference names it.
func (r *Reader) Track(v any) {
	ctx := r.context()
	ctx.refs = append(ctx.refs, v)
}

// Back returns the object a back reference h (from [Reader.Ref], > 0) names, which must be a T (the type, or the
// interface of the types, the slot allows); otherwise it fails with CodeInvalidData and returns the zero T.
func Back[T any](r *Reader, h int32) T {
	if h > 0 && r.ctx != nil && int(h) <= len(r.ctx.refs) {
		if v, ok := r.ctx.refs[h-1].(T); ok {
			return v
		}
	}
	var zero T
	if !r.Failed() {
		r.badRef(h)
	}
	return zero
}

// ReadTrackedNew creates an object of a tracked class, records it ([Reader.Track]) and reads its body into it:
// the -1 case of a tracked dispatch, after its type id.
func ReadTrackedNew[T any](r *Reader, c *Class[T]) *T {
	v := new(T)
	r.Track(v)
	ReadClassInto(r, v, c)
	return v
}

// ReadTracked reads a tracked class without subtypes: its reference header, then its body for a new object.
func ReadTracked[T any](r *Reader, c *Class[T]) *T {
	switch h := r.Ref(); {
	case h == -1:
		return ReadTrackedNew(r, c)
	case h > 0:
		return Back[*T](r, h)
	}
	return nil
}

// ReadTrackedClass reads a tracked class in a slot that does not track, after its type id: its reference header
// (not 0: null was the type id), then its body for a new object.
func ReadTrackedClass[T any](r *Reader, c *Class[T]) *T {
	switch h := r.Ref(); {
	case h == -1:
		return ReadTrackedNew(r, c)
	case h > 0:
		return Back[*T](r, h)
	}
	if !r.Failed() {
		r.badRef(0)
	}
	return nil
}

// Ref writes the reference header of v (an object of a tracked type, or nil): 0 for nil, its number for an object
// written before, else -1, and then it returns true: the caller writes the rest (type id, body).
func (w *Writer) Ref(v any) bool {
	if v == nil {
		w.I32(0)
		return false
	}
	ctx := w.context()
	if n, ok := ctx.refs[v]; ok {
		w.I32(n)
		return false
	}
	if ctx.refs == nil {
		ctx.refs = make(map[any]int32)
	}
	ctx.refs[v] = int32(len(ctx.refs) + 1)
	w.I32(-1)
	return true
}

// WriteTracked writes a tracked class without subtypes: its reference header, then its body for a new object.
func WriteTracked[T any](w *Writer, v *T, c *Class[T]) {
	if v == nil {
		w.I32(0)
		return
	}
	if w.Ref(v) {
		WriteClass(w, v, c)
	}
}

// WriteTypedTracked writes a tracked class in a slot that does not track: 0 for nil, else its type id, its
// reference header and, for a new object, its body.
func WriteTypedTracked[T any](w *Writer, id int32, v *T, c *Class[T]) {
	if v == nil {
		w.I32(NullTypeID)
		return
	}
	w.I32(id)
	if w.Ref(v) {
		WriteClass(w, v, c)
	}
}

type trackedClassType[T any] struct {
	id int32
	c  *Class[T]
}

// TrackedClassType is [ClassType] for a tracked class: in an object slot, its type id is followed by its reference
// header. Values are *T.
func TrackedClassType[T any](id int32, c *Class[T]) Subtype { return &trackedClassType[T]{id, c} }

func (t *trackedClassType[T]) ID() int32 { return t.id }

func (t *trackedClassType[T]) read(r *Reader) any { return ReadTrackedClass(r, t.c) }

func (t *trackedClassType[T]) write(w *Writer, v any) bool {
	p, ok := v.(*T)
	if !ok {
		return false
	}
	WriteTypedTracked(w, t.id, p, t.c)
	return true
}

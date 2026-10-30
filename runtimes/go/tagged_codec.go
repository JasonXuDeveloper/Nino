package nino

import (
	"reflect"
)

// Struct tags (tagged.go): the codecs a plan is made of, and how a plan reads and writes a struct's body.

type codecKind uint8

const (
	kBool codecKind = iota + 1
	kInt
	kUint
	kFloat32
	kFloat64
	kDecimal
	kGuid
	kDateTime
	kTimeSpan
	kDateOnly
	kTimeOnly
	kInt128
	kUInt128
	kHalf
	kDTO
	kBigInt
	kVersion
	kString
	kStringPtr
	kBytes
	kPrimSlice // slice of fixed-size numbers: one block
	kSlice
	kStride // slice of nullable raw values: the fixed stride (spec §6.2)
	kSet
	kMap
	kNullable
	kClass     // pointer to a class: presence byte or type id, then the body
	kStruct    // a struct written member by member
	kRawStruct // a raw struct
	kIface     // interface: type id, then a registered type
)

// codec reads and writes one Go type. read and write take an addressable value.
type codec struct {
	typ   reflect.Type
	kind  codecKind
	size  int // bytes on the wire when fixed (numbers, raw structs), else 0
	align int // canonical alignment (spec §5.2) of a raw value; 0 when unknown or not raw
	min   int // the fewest bytes a value takes on the wire
	utf8  bool
	quirk bool // .NET publishes a wrong raw layout for it (spec §10, item 1)
	elem  *codec
	key   *codec
	plan  *plan
	// enter: the value can hold itself through no class body, so its reads count with EnterStruct.
	enter bool
	read  func(r *Reader, v reflect.Value)
	write func(w *Writer, v reflect.Value)
}

// rawCapable: the value can be a field of a raw struct (a fixed-size leaf or a raw struct).
func (c *codec) rawCapable() bool { return c.kind <= kHalf || c.kind == kRawStruct }

// plan is the body of a struct type.
type plan struct {
	typ        reflect.Type
	layout     *layoutDecl
	format     int
	header     int
	id         int32
	hasID      bool
	deferrable bool
	fields     []field
	keys       map[int32]int // keyed: key and former keys to field index (nil for few members)
	raw        bool          // as a value: one raw block
	size       int           // raw size
	align      int           // raw alignment
	entry      bool          // nino.Entry: a pair
	rawErr     string        // why the type cannot be raw although its fields are fixed-size
}

type field struct {
	name    string // Go field path
	index   []int
	c       *codec
	key     int32
	formers []int32
	off     int // raw offset
	member  int // index of the layout member, -1 without one
}

func (p *plan) fieldValue(v reflect.Value, f *field) reflect.Value {
	if len(f.index) == 1 {
		return v.Field(f.index[0])
	}
	for _, i := range f.index {
		v = v.Field(i)
	}
	return v
}

// body: a plan reads and writes class bodies that were deferred.
func (p *plan) readAny(r *Reader, v any) {
	rv := reflect.ValueOf(v)
	if rv.Kind() == reflect.Pointer && !rv.IsNil() && rv.Type().Elem() == p.typ {
		p.readBody(r, rv.Elem())
	}
}

func (p *plan) writeAny(w *Writer, v any) {
	rv := reflect.ValueOf(v)
	if rv.Kind() == reflect.Pointer && !rv.IsNil() && rv.Type().Elem() == p.typ {
		p.writeBody(w, rv.Elem())
	}
}

func (p *plan) readBody(r *Reader, v reflect.Value) {
	switch p.format {
	case formatPositional:
		for i := range p.fields {
			f := &p.fields[i]
			f.c.read(r, p.fieldValue(v, f))
			if r.err != nil {
				return
			}
		}
	case formatTolerant:
		g := r.BeginRegion()
		for i := range p.fields {
			if !r.More() {
				break
			}
			f := &p.fields[i]
			f.c.read(r, p.fieldValue(v, f))
		}
		r.EndRegion(g)
	default:
		p.readKeyed(r, v)
	}
}

func (p *plan) readKeyed(r *Reader, v reflect.Value) {
	g := r.BeginRegion()
	var seen uint64
	var seenMany []bool
	if len(p.fields) > 64 {
		seenMany = make([]bool, len(p.fields))
	}
	for r.More() {
		key := r.I32()
		e := r.BeginMember()
		i := p.lookupKey(key)
		if i < 0 || r.err != nil {
			r.SkipMember(e)
			continue
		}
		if seenMany != nil {
			if seenMany[i] {
				r.Fail(CodeDuplicateKey, "keyed member repeated")
				break
			}
			seenMany[i] = true
		} else if !r.KeyedSeen(&seen, uint(i)) {
			break
		}
		f := &p.fields[i]
		f.c.read(r, p.fieldValue(v, f))
		r.EndMember(e)
	}
	r.EndRegion(g)
}

func (p *plan) lookupKey(key int32) int {
	if p.keys != nil {
		if i, ok := p.keys[key]; ok {
			return i
		}
		return -1
	}
	for i := range p.fields {
		f := &p.fields[i]
		if f.key == key {
			return i
		}
		for _, k := range f.formers {
			if k == key {
				return i
			}
		}
	}
	return -1
}

func (p *plan) writeBody(w *Writer, v reflect.Value) {
	switch p.format {
	case formatPositional:
		for i := range p.fields {
			f := &p.fields[i]
			f.c.write(w, p.fieldValue(v, f))
		}
	case formatTolerant:
		g := w.BeginRegion()
		for i := range p.fields {
			f := &p.fields[i]
			f.c.write(w, p.fieldValue(v, f))
		}
		w.EndRegion(g)
	default:
		g := w.BeginRegion()
		for i := range p.fields {
			f := &p.fields[i]
			e := w.BeginMember(f.key)
			f.c.write(w, p.fieldValue(v, f))
			w.EndMember(e)
		}
		w.EndRegion(g)
	}
}

// readClass reads a class body into *ptr, counting the depth as ReadClass does.
func (p *plan) readClass(r *Reader, ptr reflect.Value) {
	d := r.depth
	if d >= InlineDepth {
		r.readDeep(ptr.Interface(), p, p.deferrable)
		return
	}
	r.depth = d + 1
	p.readBody(r, ptr.Elem())
	r.exitBody(d)
}

func (p *plan) writeClass(w *Writer, ptr reflect.Value) {
	d := w.depth
	if d >= InlineDepth {
		w.writeDeep(ptr.Interface(), p, p.deferrable)
		return
	}
	w.depth = d + 1
	p.writeBody(w, ptr.Elem())
	w.exitBody(d)
}

func (p *plan) readRaw(r *Reader, v reflect.Value) {
	if len(r.buf) < p.size {
		r.eof()
		return
	}
	pos := 0
	for i := range p.fields {
		f := &p.fields[i]
		if f.off > pos {
			r.Skip(f.off - pos)
		}
		f.c.read(r, p.fieldValue(v, f))
		pos = f.off + f.c.size
	}
	r.Skip(p.size - pos)
}

func (p *plan) writeRaw(w *Writer, v reflect.Value) {
	pos := 0
	for i := range p.fields {
		f := &p.fields[i]
		if f.off > pos {
			w.ExtendZero(f.off - pos)
		}
		f.c.write(w, p.fieldValue(v, f))
		pos = f.off + f.c.size
	}
	if p.size > pos {
		w.ExtendZero(p.size - pos)
	}
}

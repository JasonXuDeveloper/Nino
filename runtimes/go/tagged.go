package nino

import (
	"cmp"
	"fmt"
	"math/big"
	"reflect"
	"slices"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"unsafe"
)

// Struct tags: Go structs mapped to .NET [NinoType]s without generated code (see the README, "Struct tags").
//
// Each Go type gets a codec plan the first time it is used: its fields, their order, keys and encodings, checked
// against the type's NinoLayout when it declares one. Plans are built once (under a lock), cached by reflect.Type,
// and immutable after; the steady-state cost is walking the plan. Every read goes through the same Reader as
// generated code, so the bounds, the length checks before allocation, the depth counting and the deferral are the
// same, and a plan that cannot express a type fails when it is built, with a *TagError naming the field.

// TagError reports a Go type that struct tags cannot map: a bad tag, a field type the wire format has no encoding
// for, or a mismatch with the type's layout. It is a programming error, returned before any byte is read or
// written.
type TagError struct {
	Type   reflect.Type
	Field  string // the Go field (a path for promoted fields); empty for the type itself
	Detail string
}

func (e *TagError) Error() string {
	name := "<nil>"
	if e.Type != nil {
		name = e.Type.String()
	}
	if e.Field != "" {
		return "nino: " + name + "." + e.Field + ": " + e.Detail
	}
	return "nino: " + name + ": " + e.Detail
}

// The interfaces a Go type implements to declare its .NET shape.
type (
	// Layouted is implemented by a Go type that declares its .NET layout: the string .NET publishes for the type
	// (NinoTypeLayout), or any part of it ("format=Keyed"). See the README.
	Layouted interface{ NinoLayout() string }
)

// ---------------------------------------------------------------- codecs

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

func (c *codec) rawCapable() bool { return c.size > 0 && c.kind != kDTO }

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
		e := r.BeginEntry()
		i := p.lookupKey(key)
		if i < 0 || r.err != nil {
			r.SkipEntry(e)
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
		r.EndEntry(e)
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
			e := w.BeginEntry(f.key)
			f.c.write(w, p.fieldValue(v, f))
			w.EndEntry(e)
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

// ---------------------------------------------------------------- the cache

type codecKey struct {
	t       reflect.Type
	variant uint8
}

const (
	variantDefault uint8 = iota
	variantUTF8
	variantElementNullable // []*T written as single T? per element (seq(nullable(...)))
)

// unsafeAccess selects pointer access to the fields of numbers instead of reflect's Set and Int (the unsafe path;
// both give the same bytes, which the tests check). Each mode has its own cache.
var unsafeAccess atomic.Bool

func init() { unsafeAccess.Store(true) }

type planCache struct {
	mu     sync.Mutex
	codecs sync.Map // codecKey -> *codec
	errs   sync.Map // codecKey -> error
	plans  map[reflect.Type]*plan
}

var caches [2]planCache

func currentCache() (*planCache, bool) {
	if unsafeAccess.Load() {
		return &caches[1], true
	}
	return &caches[0], false
}

// codecOf returns the codec of t, building it (and every type it reaches) on first use.
func codecOf(t reflect.Type, variant uint8) (*codec, error) {
	cache, fast := currentCache()
	k := codecKey{t, variant}
	if c, ok := cache.codecs.Load(k); ok {
		return c.(*codec), nil
	}
	if err, ok := cache.errs.Load(k); ok {
		return nil, err.(error)
	}
	cache.mu.Lock()
	defer cache.mu.Unlock()
	if c, ok := cache.codecs.Load(k); ok {
		return c.(*codec), nil
	}
	if cache.plans == nil {
		cache.plans = map[reflect.Type]*plan{}
	}
	b := &builder{cache: cache, fast: fast, building: map[codecKey]*codec{}, plans: map[reflect.Type]*plan{}}
	c, err := b.codec(t, variant, nil)
	if err != nil {
		cache.errs.Store(k, err)
		return nil, err
	}
	for bk, bc := range b.building {
		cache.codecs.Store(bk, bc)
	}
	for pt, p := range b.plans {
		cache.plans[pt] = p
	}
	return c, nil
}

// ---------------------------------------------------------------- the builder

type builder struct {
	cache    *planCache
	fast     bool
	building map[codecKey]*codec // codecs of this build, complete or in progress
	plans    map[reflect.Type]*plan
	stack    []*codec // composite codecs being built, outermost first
}

var (
	typeDecimal        = reflect.TypeFor[Decimal]()
	typeGuid           = reflect.TypeFor[Guid]()
	typeDateTime       = reflect.TypeFor[DateTime]()
	typeTimeSpan       = reflect.TypeFor[TimeSpan]()
	typeDateOnly       = reflect.TypeFor[DateOnly]()
	typeTimeOnly       = reflect.TypeFor[TimeOnly]()
	typeInt128         = reflect.TypeFor[Int128]()
	typeUInt128        = reflect.TypeFor[UInt128]()
	typeHalf           = reflect.TypeFor[Half]()
	typeDateTimeOffset = reflect.TypeFor[DateTimeOffset]()
	typeBigInt         = reflect.TypeFor[*big.Int]()
	typeVersion        = reflect.TypeFor[*Version]()
	typeVersionValue   = reflect.TypeFor[Version]()
	typeLayouted       = reflect.TypeFor[Layouted]()
	typeTyped          = reflect.TypeFor[Typed]()
	ninoPkg            = reflect.TypeFor[Decimal]().PkgPath()
)

func typeErr(t reflect.Type, format string, args ...any) *TagError {
	return &TagError{Type: t, Detail: fmt.Sprintf(format, args...)}
}

// codec returns the codec of t. owner is the type whose field is being built (for errors).
func (b *builder) codec(t reflect.Type, variant uint8, owner reflect.Type) (*codec, error) {
	k := codecKey{t, variant}
	if c, ok := b.building[k]; ok {
		if c.read == nil {
			b.markCycle(c)
		}
		return c, nil
	}
	if c, ok := b.cache.codecs.Load(k); ok {
		return c.(*codec), nil
	}
	c := &codec{typ: t}
	b.building[k] = c
	b.stack = append(b.stack, c)
	err := b.fill(c, variant)
	b.stack = b.stack[:len(b.stack)-1]
	if err != nil {
		delete(b.building, k)
		return nil, err
	}
	return c, nil
}

// markCycle: c is being built and reached again, so its values can nest in themselves. Unless a class body (which
// counts toward the depth) lies on the way, its reads count with EnterStruct.
func (b *builder) markCycle(c *codec) {
	for i := len(b.stack) - 1; i >= 0; i-- {
		s := b.stack[i]
		if s.kind == kClass {
			return
		}
		if s == c {
			break
		}
	}
	c.enter = true
}

func (b *builder) fill(c *codec, variant uint8) error {
	t := c.typ
	if variant == variantUTF8 && t.Kind() != reflect.String && !(t.Kind() == reflect.Pointer && t.Elem().Kind() == reflect.String) {
		return typeErr(t, "utf8 applies to string and *string fields only")
	}
	switch t {
	case typeDecimal:
		return b.leaf(c, kDecimal, 16, 8, false)
	case typeGuid:
		return b.leaf(c, kGuid, 16, 4, false)
	case typeDateTime:
		return b.leaf(c, kDateTime, 8, 8, false)
	case typeTimeSpan:
		return b.leaf(c, kTimeSpan, 8, 8, false)
	case typeDateOnly:
		return b.leaf(c, kDateOnly, 4, 4, true)
	case typeTimeOnly:
		return b.leaf(c, kTimeOnly, 8, 8, true)
	case typeInt128:
		// The alignment of Int128 differs between .NET versions: only a declared raw layout places it.
		return b.leaf(c, kInt128, 16, 0, true)
	case typeUInt128:
		return b.leaf(c, kUInt128, 16, 0, true)
	case typeHalf:
		return b.leaf(c, kHalf, 2, 2, true)
	case typeDateTimeOffset:
		c.kind, c.size, c.min = kDTO, 10, 10
		c.read = func(r *Reader, v reflect.Value) { v.Set(reflect.ValueOf(r.DateTimeOffset())) }
		c.write = func(w *Writer, v reflect.Value) { w.DateTimeOffset(v.Interface().(DateTimeOffset)) }
		return nil
	case typeBigInt:
		c.kind, c.min = kBigInt, 4
		c.read = func(r *Reader, v reflect.Value) { v.Set(reflect.ValueOf(r.BigInteger())) }
		c.write = func(w *Writer, v reflect.Value) { w.BigInteger(v.Interface().(*big.Int)) }
		return nil
	case typeVersion:
		c.kind, c.min = kVersion, 4
		c.read = func(r *Reader, v reflect.Value) { v.Set(reflect.ValueOf(r.Version())) }
		c.write = func(w *Writer, v reflect.Value) { w.Version(v.Interface().(*Version)) }
		return nil
	}
	if t == typeVersionValue {
		return typeErr(t, "a Version is written through a pointer: use *nino.Version")
	}
	switch t.Kind() {
	case reflect.Bool:
		return b.leaf(c, kBool, 1, 1, false)
	case reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return b.leaf(c, kInt, int(t.Size()), int(t.Size()), false)
	case reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		return b.leaf(c, kUint, int(t.Size()), int(t.Size()), false)
	case reflect.Float32:
		return b.leaf(c, kFloat32, 4, 4, false)
	case reflect.Float64:
		return b.leaf(c, kFloat64, 8, 8, false)
	case reflect.Int, reflect.Uint, reflect.Uintptr:
		return typeErr(t, "%s has no fixed size: use int32 or int64 (uint32 or uint64)", t.Kind())
	case reflect.String:
		c.kind, c.min, c.utf8 = kString, 4, variant == variantUTF8
		if c.utf8 {
			c.read = func(r *Reader, v reflect.Value) { v.SetString(r.Utf8()) }
			c.write = func(w *Writer, v reflect.Value) { w.Utf8(v.String()) }
		} else {
			c.read = func(r *Reader, v reflect.Value) { v.SetString(r.String()) }
			c.write = func(w *Writer, v reflect.Value) { w.String(v.String()) }
		}
		return nil
	case reflect.Pointer:
		return b.pointer(c, variant)
	case reflect.Slice:
		return b.slice(c, variant)
	case reflect.Map:
		return b.mapCodec(c)
	case reflect.Struct:
		p, err := b.plan(t)
		if err != nil {
			return err
		}
		return b.valueStruct(c, p)
	case reflect.Interface:
		return b.iface(c)
	case reflect.Array:
		return typeErr(t, "arrays have no encoding: use a slice")
	}
	return typeErr(t, "%s has no encoding in the wire format", t.Kind())
}

// leaf: a fixed-size value.
func (b *builder) leaf(c *codec, kind codecKind, size, align int, quirk bool) error {
	c.kind, c.size, c.align, c.min, c.quirk = kind, size, align, size, quirk
	fast := b.fast
	switch kind {
	case kBool:
		if fast {
			c.read = func(r *Reader, v reflect.Value) { *(*bool)(unsafe.Pointer(v.UnsafeAddr())) = r.Bool() }
			c.write = func(w *Writer, v reflect.Value) { w.Bool(*(*bool)(unsafe.Pointer(v.UnsafeAddr()))) }
		} else {
			c.read = func(r *Reader, v reflect.Value) { v.SetBool(r.Bool()) }
			c.write = func(w *Writer, v reflect.Value) { w.Bool(v.Bool()) }
		}
	case kInt, kUint:
		c.read, c.write = numberAccess(kind, size, fast)
	case kFloat32:
		// Through the bits in both modes: a float32 converted to float64 and back loses a signaling NaN's payload.
		c.read = func(r *Reader, v reflect.Value) { *(*uint32)(unsafe.Pointer(v.UnsafeAddr())) = r.U32() }
		c.write = func(w *Writer, v reflect.Value) { w.U32(*(*uint32)(unsafe.Pointer(v.UnsafeAddr()))) }
	case kFloat64:
		if fast {
			c.read = func(r *Reader, v reflect.Value) { *(*uint64)(unsafe.Pointer(v.UnsafeAddr())) = r.U64() }
			c.write = func(w *Writer, v reflect.Value) { w.U64(*(*uint64)(unsafe.Pointer(v.UnsafeAddr()))) }
		} else {
			c.read = func(r *Reader, v reflect.Value) { v.SetFloat(r.F64()) }
			c.write = func(w *Writer, v reflect.Value) { w.F64(v.Float()) }
		}
	case kDecimal:
		c.read = func(r *Reader, v reflect.Value) { v.Set(reflect.ValueOf(r.Decimal())) }
		c.write = func(w *Writer, v reflect.Value) { w.Decimal(v.Interface().(Decimal)) }
	case kGuid:
		c.read = func(r *Reader, v reflect.Value) { v.Set(reflect.ValueOf(r.Guid())) }
		c.write = func(w *Writer, v reflect.Value) { w.Guid(v.Interface().(Guid)) }
	case kDateTime:
		c.read = func(r *Reader, v reflect.Value) { v.SetUint(uint64(r.DateTime())) }
		c.write = func(w *Writer, v reflect.Value) { w.DateTime(DateTime(v.Uint())) }
	case kTimeSpan:
		c.read = func(r *Reader, v reflect.Value) { v.SetInt(int64(r.TimeSpan())) }
		c.write = func(w *Writer, v reflect.Value) { w.TimeSpan(TimeSpan(v.Int())) }
	case kDateOnly:
		c.read = func(r *Reader, v reflect.Value) { v.SetInt(int64(r.DateOnly())) }
		c.write = func(w *Writer, v reflect.Value) { w.DateOnly(DateOnly(v.Int())) }
	case kTimeOnly:
		c.read = func(r *Reader, v reflect.Value) { v.SetInt(int64(r.TimeOnly())) }
		c.write = func(w *Writer, v reflect.Value) { w.TimeOnly(TimeOnly(v.Int())) }
	case kInt128:
		c.read = func(r *Reader, v reflect.Value) { v.Set(reflect.ValueOf(r.Int128())) }
		c.write = func(w *Writer, v reflect.Value) { w.Int128(v.Interface().(Int128)) }
	case kUInt128:
		c.read = func(r *Reader, v reflect.Value) { v.Set(reflect.ValueOf(r.UInt128())) }
		c.write = func(w *Writer, v reflect.Value) { w.UInt128(v.Interface().(UInt128)) }
	case kHalf:
		c.read = func(r *Reader, v reflect.Value) { v.SetUint(uint64(r.Half())) }
		c.write = func(w *Writer, v reflect.Value) { w.Half(Half(v.Uint())) }
	}
	return nil
}

func numberAccess(kind codecKind, size int, fast bool) (func(*Reader, reflect.Value), func(*Writer, reflect.Value)) {
	if fast {
		switch size {
		case 1:
			return func(r *Reader, v reflect.Value) { *(*uint8)(unsafe.Pointer(v.UnsafeAddr())) = r.U8() },
				func(w *Writer, v reflect.Value) { w.U8(*(*uint8)(unsafe.Pointer(v.UnsafeAddr()))) }
		case 2:
			return func(r *Reader, v reflect.Value) { *(*uint16)(unsafe.Pointer(v.UnsafeAddr())) = r.U16() },
				func(w *Writer, v reflect.Value) { w.U16(*(*uint16)(unsafe.Pointer(v.UnsafeAddr()))) }
		case 4:
			return func(r *Reader, v reflect.Value) { *(*uint32)(unsafe.Pointer(v.UnsafeAddr())) = r.U32() },
				func(w *Writer, v reflect.Value) { w.U32(*(*uint32)(unsafe.Pointer(v.UnsafeAddr()))) }
		default:
			return func(r *Reader, v reflect.Value) { *(*uint64)(unsafe.Pointer(v.UnsafeAddr())) = r.U64() },
				func(w *Writer, v reflect.Value) { w.U64(*(*uint64)(unsafe.Pointer(v.UnsafeAddr()))) }
		}
	}
	if kind == kInt {
		switch size {
		case 1:
			return func(r *Reader, v reflect.Value) { v.SetInt(int64(r.I8())) }, func(w *Writer, v reflect.Value) { w.I8(int8(v.Int())) }
		case 2:
			return func(r *Reader, v reflect.Value) { v.SetInt(int64(r.I16())) }, func(w *Writer, v reflect.Value) { w.I16(int16(v.Int())) }
		case 4:
			return func(r *Reader, v reflect.Value) { v.SetInt(int64(r.I32())) }, func(w *Writer, v reflect.Value) { w.I32(int32(v.Int())) }
		default:
			return func(r *Reader, v reflect.Value) { v.SetInt(r.I64()) }, func(w *Writer, v reflect.Value) { w.I64(v.Int()) }
		}
	}
	switch size {
	case 1:
		return func(r *Reader, v reflect.Value) { v.SetUint(uint64(r.U8())) }, func(w *Writer, v reflect.Value) { w.U8(uint8(v.Uint())) }
	case 2:
		return func(r *Reader, v reflect.Value) { v.SetUint(uint64(r.U16())) }, func(w *Writer, v reflect.Value) { w.U16(uint16(v.Uint())) }
	case 4:
		return func(r *Reader, v reflect.Value) { v.SetUint(uint64(r.U32())) }, func(w *Writer, v reflect.Value) { w.U32(uint32(v.Uint())) }
	default:
		return func(r *Reader, v reflect.Value) { v.SetUint(r.U64()) }, func(w *Writer, v reflect.Value) { w.U64(v.Uint()) }
	}
}

func (b *builder) pointer(c *codec, variant uint8) error {
	t := c.typ
	et := t.Elem()
	switch {
	case et.Kind() == reflect.String:
		c.kind, c.min, c.utf8 = kStringPtr, 4, variant == variantUTF8
		utf8 := c.utf8
		c.read = func(r *Reader, v reflect.Value) {
			var s string
			var ok bool
			if utf8 {
				s, ok = r.NullableUtf8()
			} else {
				s, ok = r.NullableString()
			}
			if !ok {
				v.SetZero()
				return
			}
			p := reflect.New(et)
			p.Elem().SetString(s)
			v.Set(p)
		}
		c.write = func(w *Writer, v reflect.Value) {
			ok := !v.IsNil()
			var s string
			if ok {
				s = v.Elem().String()
			}
			if utf8 {
				w.NullableUtf8(s, ok)
			} else {
				w.NullableString(s, ok)
			}
		}
		return nil
	case et.Kind() == reflect.Struct && !isNinoValue(et):
		if declaredHeader(et) != headerNone {
			c.kind = kClass // for markCycle while the plan is built
		}
		p, err := b.plan(et)
		if err != nil {
			return err
		}
		if p.header == headerNone {
			c.kind = 0
			return b.nullable(c)
		}
		return b.class(c, p)
	case et.Kind() == reflect.Pointer || et.Kind() == reflect.Slice || et.Kind() == reflect.Map || et.Kind() == reflect.Interface:
		return typeErr(t, "a pointer to a %s has no encoding: use the %s itself (nil is null)", et.Kind(), et.Kind())
	}
	return b.nullable(c)
}

// declaredHeader returns the header t's layout declares (headerAuto without one, or for a bad one: the plan
// reports it).
func declaredHeader(t reflect.Type) int {
	s, ok := layoutOf(t)
	if !ok {
		return headerAuto
	}
	d, err := parseLayout(s)
	if err != nil {
		return headerAuto
	}
	return d.header
}

func isNinoValue(t reflect.Type) bool {
	switch t {
	case typeDecimal, typeInt128, typeUInt128, typeDateTimeOffset:
		return true
	}
	return false
}

// nullable: Nullable<T>, a presence byte then T.
func (b *builder) nullable(c *codec) error {
	t := c.typ
	e, err := b.codec(t.Elem(), variantDefault, nil)
	if err != nil {
		return err
	}
	c.kind, c.elem, c.min = kNullable, e, 1
	et := t.Elem()
	c.read = func(r *Reader, v reflect.Value) {
		if !r.Present() {
			v.SetZero()
			return
		}
		p := reflect.New(et)
		if e.enter {
			if !r.EnterStruct() {
				return
			}
			e.read(r, p.Elem())
			r.ExitStruct()
		} else {
			e.read(r, p.Elem())
		}
		if r.err == nil {
			v.Set(p)
		}
	}
	c.write = func(w *Writer, v reflect.Value) {
		if v.IsNil() {
			w.U8(0)
			return
		}
		w.U8(1)
		if e.enter {
			if !w.EnterStruct() {
				return
			}
			e.write(w, v.Elem())
			w.ExitStruct()
		} else {
			e.write(w, v.Elem())
		}
	}
	return nil
}

// class: a pointer to a class, behind a presence byte or a type id.
func (b *builder) class(c *codec, p *plan) error {
	c.kind, c.plan, c.min = kClass, p, 1
	t := p.typ
	if p.header == headerTypeID {
		if !p.hasID {
			return typeErr(t, "header=TypeId but no type id: give the layout a typeId or implement NinoTypeID")
		}
		c.min = 4
		id := p.id
		c.read = func(r *Reader, v reflect.Value) {
			got := r.I32()
			if r.err != nil {
				return
			}
			if got == NullTypeID {
				v.SetZero()
				return
			}
			if got != id {
				// A Go pointer holds only its own type: a subtype needs an interface field.
				r.UnknownType(got)
				return
			}
			ptr := reflect.New(t)
			v.Set(ptr)
			p.readClass(r, ptr)
		}
		c.write = func(w *Writer, v reflect.Value) {
			if v.IsNil() {
				w.I32(NullTypeID)
				return
			}
			w.I32(id)
			p.writeClass(w, v)
		}
		return nil
	}
	c.read = func(r *Reader, v reflect.Value) {
		if !r.Present() {
			v.SetZero()
			return
		}
		ptr := reflect.New(t)
		v.Set(ptr)
		p.readClass(r, ptr)
	}
	c.write = func(w *Writer, v reflect.Value) {
		if v.IsNil() {
			w.U8(0)
			return
		}
		w.U8(1)
		p.writeClass(w, v)
	}
	return nil
}

// valueStruct: a struct held by value.
func (b *builder) valueStruct(c *codec, p *plan) error {
	if p.header == headerNullFlag || p.header == headerTypeID {
		return typeErr(c.typ, "its layout declares a class (header=%s): hold it through a pointer", headerName(p.header))
	}
	c.plan = p
	if p.raw {
		c.kind, c.size, c.align, c.min = kRawStruct, p.size, p.align, p.size
		c.read = p.readRaw
		c.write = p.writeRaw
		return nil
	}
	c.kind = kStruct
	min := 0
	if p.format != formatPositional {
		min = 4
	} else {
		for i := range p.fields {
			if m := p.fields[i].c; m != nil {
				min += m.min
			}
		}
	}
	c.min = min
	c.read = func(r *Reader, v reflect.Value) {
		if c.enter {
			if !r.EnterStruct() {
				return
			}
			p.readBody(r, v)
			r.ExitStruct()
			return
		}
		p.readBody(r, v)
	}
	c.write = func(w *Writer, v reflect.Value) {
		if c.enter {
			if !w.EnterStruct() {
				return
			}
			p.writeBody(w, v)
			w.ExitStruct()
			return
		}
		p.writeBody(w, v)
	}
	return nil
}

func headerName(h int) string {
	switch h {
	case headerNone:
		return "None"
	case headerNullFlag:
		return "NullFlag"
	case headerTypeID:
		return "TypeId"
	}
	return "auto"
}

func (b *builder) slice(c *codec, variant uint8) error {
	t := c.typ
	et := t.Elem()
	c.min = 4
	if et.Kind() == reflect.Uint8 {
		c.kind = kBytes
		c.read = func(r *Reader, v reflect.Value) {
			bs := r.ByteArray()
			if bs == nil {
				v.SetZero()
				return
			}
			v.SetBytes(bs)
		}
		c.write = func(w *Writer, v reflect.Value) {
			if v.IsNil() {
				w.Null()
				return
			}
			w.ByteArray(v.Bytes())
		}
		return nil
	}
	// []*T of a raw T: the fixed stride, unless the layout says each element is a single T?.
	if et.Kind() == reflect.Pointer && variant != variantElementNullable && et.Elem().Kind() != reflect.String {
		pe, err := b.codec(et, variantDefault, nil)
		if err != nil {
			return err
		}
		if pe.kind == kNullable && pe.elem.rawCapable() {
			return b.stride(c, pe.elem)
		}
	}
	e, err := b.codec(et, variantDefault, nil)
	if err != nil {
		return err
	}
	c.elem = e
	if b.fast && littleEndian && (e.kind == kInt || e.kind == kUint || e.kind == kFloat32 || e.kind == kFloat64) {
		c.kind = kPrimSlice
		size := e.size
		c.read = func(r *Reader, v reflect.Value) {
			n := r.Length(size)
			if n < 0 {
				v.SetZero()
				return
			}
			src := r.Take(n * size)
			if len(src) != n*size {
				return
			}
			s := reflect.MakeSlice(t, n, n)
			if n > 0 {
				copy(unsafe.Slice((*byte)(s.UnsafePointer()), n*size), src)
			}
			v.Set(s)
		}
		c.write = func(w *Writer, v reflect.Value) {
			if v.IsNil() {
				w.Null()
				return
			}
			n := v.Len()
			w.Length(n)
			if n > 0 && w.err == nil {
				copy(w.Extend(n*size), unsafe.Slice((*byte)(v.UnsafePointer()), n*size))
			}
		}
		return nil
	}
	c.kind = kSlice
	elemSize := int(et.Size())
	c.read = func(r *Reader, v reflect.Value) {
		n := r.Length(max(e.min, 1)) // at least 1 byte an element (spec §9.2)
		if n < 0 {
			v.SetZero()
			return
		}
		if c.enter && !r.EnterStruct() {
			return
		}
		capacity := r.SliceCap(n, elemSize)
		s := reflect.MakeSlice(t, capacity, capacity)
		for i := 0; i < n; i++ {
			if i == s.Len() {
				grown := reflect.MakeSlice(t, min(n, 2*s.Len()), min(n, 2*s.Len()))
				reflect.Copy(grown, s)
				s = grown
			}
			e.read(r, s.Index(i))
			if r.err != nil {
				break
			}
		}
		if c.enter {
			r.ExitStruct()
		}
		if r.err == nil {
			v.Set(s)
		}
	}
	c.write = func(w *Writer, v reflect.Value) {
		if v.IsNil() {
			w.Null()
			return
		}
		n := v.Len()
		w.Length(n)
		if c.enter && !w.EnterStruct() {
			return
		}
		for i := 0; i < n && w.err == nil; i++ {
			e.write(w, v.Index(i))
		}
		if c.enter {
			w.ExitStruct()
		}
	}
	return nil
}

// stride: T?[] of a raw T (spec §6.2): hasValue, padding to T's alignment, then T (zero when absent).
func (b *builder) stride(c *codec, e *codec) error {
	t := c.typ
	if e.align == 0 {
		return typeErr(t, "the stride of a Nullable<%s> element has no canonical alignment", e.typ)
	}
	a := e.align
	stride := a + e.size
	c.kind, c.elem, c.size, c.min = kStride, e, stride, 4
	et := t.Elem()
	c.read = func(r *Reader, v reflect.Value) {
		n := r.Length(stride)
		if n < 0 {
			v.SetZero()
			return
		}
		s := reflect.MakeSlice(t, n, n)
		for i := 0; i < n && r.err == nil; i++ {
			present := r.Present()
			r.Skip(a - 1)
			if !present {
				r.Skip(e.size)
				continue
			}
			p := reflect.New(et.Elem())
			e.read(r, p.Elem())
			s.Index(i).Set(p)
		}
		if r.err == nil {
			v.Set(s)
		}
	}
	c.write = func(w *Writer, v reflect.Value) {
		if v.IsNil() {
			w.Null()
			return
		}
		n := v.Len()
		w.Length(n)
		for i := 0; i < n && w.err == nil; i++ {
			p := v.Index(i)
			if p.IsNil() {
				w.ExtendZero(stride)
				continue
			}
			w.U8(1)
			w.ExtendZero(a - 1)
			e.write(w, p.Elem())
		}
	}
	return nil
}

func (b *builder) mapCodec(c *codec) error {
	t := c.typ
	kc, err := b.codec(t.Key(), variantDefault, nil)
	if err != nil {
		return err
	}
	c.key, c.min = kc, 4
	kt, vt := t.Key(), t.Elem()
	set := vt.Kind() == reflect.Struct && vt.NumField() == 0
	var vc *codec
	if !set {
		if vc, err = b.codec(vt, variantDefault, nil); err != nil {
			return err
		}
		c.elem = vc
		c.kind = kMap
	} else {
		c.kind = kSet
	}
	pairMin := max(kc.min, 1)
	slot := int(kt.Size()) + hashSlot
	if vc != nil {
		pairMin += vc.min
		slot += int(vt.Size())
	}
	c.read = func(r *Reader, v reflect.Value) {
		n := r.Length(pairMin)
		if n < 0 {
			v.SetZero()
			return
		}
		if c.enter && !r.EnterStruct() {
			return
		}
		m := reflect.MakeMapWithSize(t, capacity(n, slot, len(r.buf)))
		k := reflect.New(kt).Elem()
		val := reflect.New(vt).Elem()
		for i := 0; i < n; i++ {
			k.SetZero()
			kc.read(r, k)
			if vc != nil {
				val.SetZero()
				vc.read(r, val)
			}
			if r.err != nil {
				break
			}
			m.SetMapIndex(k, val)
			if m.Len() != i+1 {
				r.Fail(CodeDuplicateKey, "")
				break
			}
		}
		if c.enter {
			r.ExitStruct()
		}
		if r.err == nil {
			v.Set(m)
		}
	}
	c.write = func(w *Writer, v reflect.Value) {
		if v.IsNil() {
			w.Null()
			return
		}
		w.Length(v.Len())
		if c.enter && !w.EnterStruct() {
			return
		}
		keys := sortedKeys(v)
		k := reflect.New(kt).Elem()
		val := reflect.New(vt).Elem()
		for _, key := range keys {
			if w.err != nil {
				break
			}
			k.Set(key)
			kc.write(w, k)
			if vc != nil {
				val.Set(v.MapIndex(key))
				vc.write(w, val)
			}
		}
		if c.enter {
			w.ExitStruct()
		}
	}
	return nil
}

// sortedKeys returns the keys of a map in order when they have one (numbers, strings, bools), so that writing a
// map gives the same bytes every time; else in Go's map order.
func sortedKeys(m reflect.Value) []reflect.Value {
	keys := m.MapKeys()
	switch m.Type().Key().Kind() {
	case reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		slices.SortFunc(keys, func(a, b reflect.Value) int { return cmp.Compare(a.Int(), b.Int()) })
	case reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		slices.SortFunc(keys, func(a, b reflect.Value) int { return cmp.Compare(a.Uint(), b.Uint()) })
	case reflect.Float32, reflect.Float64:
		slices.SortFunc(keys, func(a, b reflect.Value) int { return cmp.Compare(a.Float(), b.Float()) })
	case reflect.String:
		slices.SortFunc(keys, func(a, b reflect.Value) int { return strings.Compare(a.String(), b.String()) })
	case reflect.Bool:
		slices.SortFunc(keys, func(a, b reflect.Value) int {
			x, y := a.Bool(), b.Bool()
			if x == y {
				return 0
			} else if !x {
				return -1
			}
			return 1
		})
	}
	return keys
}

// iface: a polymorphic slot (object, an abstract class, an interface): a type id, then a registered type.
func (b *builder) iface(c *codec) error {
	t := c.typ
	c.kind, c.min = kIface, 4
	c.read = func(r *Reader, v reflect.Value) {
		id := r.I32()
		if r.err != nil {
			return
		}
		if id == NullTypeID {
			v.SetZero()
			return
		}
		e := lookupRegistered(id)
		if e == nil || !e.t.AssignableTo(t) {
			r.UnknownType(id)
			return
		}
		sc, err := e.codec()
		if err != nil {
			r.Fail(CodeUnknownType, err.Error())
			return
		}
		if sc.kind == kClass {
			ptr := reflect.New(e.t.Elem())
			v.Set(ptr)
			sc.plan.readClass(r, ptr)
			return
		}
		// A struct behind a type id: it can hold itself through the slot, so it counts with EnterStruct.
		if !r.EnterStruct() {
			return
		}
		tmp := reflect.New(e.t).Elem()
		sc.read(r, tmp)
		r.ExitStruct()
		if r.err == nil {
			v.Set(tmp)
		}
	}
	c.write = func(w *Writer, v reflect.Value) {
		if v.IsNil() {
			w.I32(NullTypeID)
			return
		}
		dyn := v.Elem()
		e := lookupRegisteredType(dyn.Type())
		if e == nil {
			w.Fail(CodeUnknownType, "Go type "+dyn.Type().String()+" is not registered (nino.Register)")
			return
		}
		sc, err := e.codec()
		if err != nil {
			w.Fail(CodeUnknownType, err.Error())
			return
		}
		if sc.kind == kClass {
			if dyn.IsNil() {
				w.I32(NullTypeID)
				return
			}
			w.I32(e.id)
			sc.plan.writeClass(w, dyn)
			return
		}
		w.I32(e.id)
		if !w.EnterStruct() {
			return
		}
		tmp := reflect.New(e.t).Elem()
		tmp.Set(dyn)
		sc.write(w, tmp)
		w.ExitStruct()
	}
	return nil
}

// ---------------------------------------------------------------- struct plans

// tagInfo is a parsed nino tag.
type tagInfo struct {
	present  bool
	skip     bool
	name     string // the name (keyed) or position (positional) part
	position int    // -1 unless the name part is a number
	utf8     bool
	formers  []string
}

func parseTag(t reflect.Type, f reflect.StructField, path string) (tagInfo, error) {
	s, ok := f.Tag.Lookup("nino")
	ti := tagInfo{present: ok, position: -1}
	if !ok {
		return ti, nil
	}
	if s == "-" {
		ti.skip = true
		return ti, nil
	}
	parts := strings.Split(s, ",")
	ti.name = strings.TrimSpace(parts[0])
	if ti.name != "" {
		if c := ti.name[0]; c >= '0' && c <= '9' {
			n, err := strconv.Atoi(ti.name)
			if err != nil || n < 0 || n > 1<<20 {
				return ti, &TagError{Type: t, Field: path, Detail: fmt.Sprintf("bad position %q", ti.name)}
			}
			ti.position = n
		}
	}
	for _, opt := range parts[1:] {
		opt = strings.TrimSpace(opt)
		switch {
		case opt == "utf8":
			ti.utf8 = true
		case strings.HasPrefix(opt, "former="):
			name := strings.TrimPrefix(opt, "former=")
			if name == "" {
				return ti, &TagError{Type: t, Field: path, Detail: "empty former name"}
			}
			ti.formers = append(ti.formers, name)
		case opt == "omitempty":
			return ti, &TagError{Type: t, Field: path, Detail: "omitempty: the wire format has no absent member (positional and version-tolerant bodies hold every member in order, and keyed writers must write an entry for every member, spec §7.6)"}
		case opt == "":
		default:
			return ti, &TagError{Type: t, Field: path, Detail: fmt.Sprintf("unknown option %q", opt)}
		}
	}
	return ti, nil
}

type rawFieldInfo struct {
	sf   reflect.StructField
	path string
	idx  []int
	tag  tagInfo
}

// collectFields lists the fields of t that take part, flattening untagged embedded structs as encoding/json does.
func collectFields(top, t reflect.Type, prefix string, index []int, out *[]rawFieldInfo) error {
	for i := 0; i < t.NumField(); i++ {
		sf := t.Field(i)
		path := sf.Name
		if prefix != "" {
			path = prefix + "." + sf.Name
		}
		idx := append(append([]int(nil), index...), i)
		ti, err := parseTag(top, sf, path)
		if err != nil {
			return err
		}
		if ti.skip {
			continue
		}
		if sf.Anonymous && !ti.present {
			ft := sf.Type
			if ft.Kind() == reflect.Struct && !isEntryType(ft) && !isNinoValue(ft) {
				if _, ok := layoutOf(ft); ok {
					return &TagError{Type: top, Field: path, Detail: "an embedded struct with its own layout: tag it (a member) or flatten it by removing its NinoLayout"}
				}
				if err := collectFields(top, ft, path, idx, out); err != nil {
					return err
				}
				continue
			}
			if ft.Kind() == reflect.Pointer && ft.Elem().Kind() == reflect.Struct {
				return &TagError{Type: top, Field: path, Detail: "an embedded pointer cannot be flattened: tag it or mark it nino:\"-\""}
			}
		}
		if !sf.IsExported() {
			if ti.present {
				return &TagError{Type: top, Field: path, Detail: "unexported fields cannot be read or written"}
			}
			continue
		}
		*out = append(*out, rawFieldInfo{sf: sf, path: path, idx: idx, tag: ti})
	}
	return nil
}

// layoutOf calls the type's NinoLayout, on a value or a pointer.
func layoutOf(t reflect.Type) (string, bool) {
	if t.Implements(typeLayouted) {
		return reflect.Zero(t).Interface().(Layouted).NinoLayout(), true
	}
	if reflect.PointerTo(t).Implements(typeLayouted) {
		return reflect.New(t).Interface().(Layouted).NinoLayout(), true
	}
	return "", false
}

func typedID(t reflect.Type) (int32, bool) {
	if t.Implements(typeTyped) && t.Kind() != reflect.Pointer {
		return reflect.Zero(t).Interface().(Typed).NinoTypeID(), true
	}
	if reflect.PointerTo(t).Implements(typeTyped) {
		return reflect.New(t).Interface().(Typed).NinoTypeID(), true
	}
	return 0, false
}

func isEntryType(t reflect.Type) bool {
	return t.PkgPath() == ninoPkg && strings.HasPrefix(t.Name(), "Entry[")
}

// plan returns the plan of the struct type t (built at most once per build; in progress when t holds itself).
func (b *builder) plan(t reflect.Type) (*plan, error) {
	if p, ok := b.plans[t]; ok {
		return p, nil
	}
	if p, ok := b.cache.plans[t]; ok {
		return p, nil
	}
	p := &plan{typ: t, deferrable: true}
	b.plans[t] = p
	if err := b.buildPlan(p); err != nil {
		delete(b.plans, t)
		return nil, err
	}
	return p, nil
}

func (b *builder) buildPlan(p *plan) error {
	t := p.typ
	if s, ok := layoutOf(t); ok {
		d, err := parseLayout(s)
		if err != nil {
			return &TagError{Type: t, Detail: err.Error()}
		}
		p.layout = d
	}
	d := p.layout
	if d == nil {
		d = &layoutDecl{}
	}
	p.entry = isEntryType(t)
	if d.track == yes {
		return typeErr(t, "trackReferences=true: reference tracking is not supported by struct tags (use generated code)")
	}
	p.header = d.header
	if d.deferrable == no {
		p.deferrable = false
	}
	if d.hasTypeID {
		p.id, p.hasID = d.typeID, true
	}
	if id, ok := typedID(t); ok {
		if p.hasID && id != p.id {
			return typeErr(t, "NinoTypeID returns 0x%08X but the layout's typeId is 0x%08X", uint32(id), uint32(p.id))
		}
		p.id, p.hasID = id, true
	}

	var infos []rawFieldInfo
	if err := collectFields(t, t, "", nil, &infos); err != nil {
		return err
	}
	positions, names := 0, 0
	for _, fi := range infos {
		if fi.tag.name == "" {
			continue
		}
		if fi.tag.position >= 0 {
			positions++
		} else {
			names++
		}
	}
	if positions > 0 && names > 0 {
		return typeErr(t, "positions and names mixed in the nino tags: positional and version-tolerant types take positions, keyed types names")
	}
	p.format = formatPositional
	if d.hasFormat {
		p.format = d.format
	} else if names > 0 {
		p.format = formatKeyed
	}
	if p.format == formatKeyed && positions > 0 {
		return typeErr(t, "a keyed type takes member names in its tags, not positions")
	}
	if p.format != formatKeyed && names > 0 {
		return typeErr(t, "a positional or version-tolerant type takes positions in its tags (nino:\"0\"), not names")
	}
	for _, fi := range infos {
		if len(fi.tag.formers) > 0 && p.format != formatKeyed {
			return &TagError{Type: t, Field: fi.path, Detail: "former names apply to keyed types only"}
		}
	}
	if d.raw == yes && p.format != formatPositional {
		return typeErr(t, "raw=true needs format=Positional")
	}

	// Order the fields.
	order := make([]int, len(infos)) // order[i]: member index of infos[i]
	pks := make([]pendingKey, len(infos))
	if p.format == formatKeyed {
		var err error
		if pks, err = keyedOrder(p, d, infos, order); err != nil {
			return err
		}
	} else if positions > 0 {
		at := map[int]string{}
		for i, fi := range infos {
			if fi.tag.position < 0 {
				return &TagError{Type: t, Field: fi.path, Detail: "no position: in a type with positions every field takes one (or nino:\"-\")"}
			}
			if prev, ok := at[fi.tag.position]; ok {
				return &TagError{Type: t, Field: fi.path, Detail: fmt.Sprintf("position %d is also %s's", fi.tag.position, prev)}
			}
			at[fi.tag.position] = fi.path
			order[i] = fi.tag.position
		}
		for pos := 0; pos < len(infos); pos++ {
			if _, ok := at[pos]; !ok {
				missing := pos
				return typeErr(t, "positions have a gap at %d: positions must be 0 to %d (a positional body has no room for a missing member)", missing, len(infos)-1)
			}
		}
	} else {
		for i := range infos {
			order[i] = i
		}
	}
	// Against the layout's members.
	members := d.members
	rawLayout := d.rawEnc
	if d.hasMembers && rawLayout == nil && p.format != formatKeyed {
		switch {
		case p.format == formatPositional && len(infos) != len(members):
			return typeErr(t, "%d fields for the %d members of the layout: a positional body holds every member", len(infos), len(members))
		case p.format == formatTolerant && len(infos) > len(members):
			return typeErr(t, "%d fields for the %d members of the layout", len(infos), len(members))
		}
	}
	if rawLayout != nil && rawLayout.fields != nil && len(infos) != len(rawLayout.fields) {
		return typeErr(t, "%d fields for the %d fields of the raw layout", len(infos), len(rawLayout.fields))
	}

	p.fields = make([]field, len(infos))
	for i, fi := range infos {
		f := &p.fields[order[i]]
		f.name, f.index, f.member = fi.path, fi.idx, -1
		f.key, f.formers = pks[i].key, pks[i].formers
		if p.format == formatKeyed {
			f.member = pks[i].member
		} else if d.hasMembers && rawLayout == nil {
			f.member = order[i]
		}
		var enc *encoding
		if f.member >= 0 {
			enc = members[f.member].enc
		}
		variant := variantDefault
		if fi.tag.utf8 {
			variant = variantUTF8
		}
		if enc != nil && enc.kind == "utf8" && !fi.tag.utf8 {
			variant = variantUTF8 // the layout says utf8: follow it
		}
		if enc != nil && enc.kind == "seq" && len(enc.args) == 1 && enc.args[0].kind == "nullable" {
			variant = variantElementNullable
		}
		c, err := b.codec(fi.sf.Type, variant, t)
		if err != nil {
			if te, ok := err.(*TagError); ok && te.Field == "" && te.Type == fi.sf.Type {
				return &TagError{Type: t, Field: fi.path, Detail: te.Detail}
			}
			return err
		}
		f.c = c
		if enc != nil {
			if err := checkEncoding(c, enc); err != nil {
				return &TagError{Type: t, Field: fi.path, Detail: fmt.Sprintf("layout member %s is %s: %v", members[f.member].name, enc.text, err)}
			}
		}
	}
	if p.format == formatKeyed && len(p.fields) > 16 {
		p.keys = map[int32]int{}
		for i := range p.fields {
			p.keys[p.fields[i].key] = i
			for _, k := range p.fields[i].formers {
				p.keys[k] = i
			}
		}
	}
	return b.rawLayout(p, d)
}

type pendingKey struct {
	key     int32
	formers []int32
	member  int
}

// keyedOrder assigns keys (the layout's, else the hash of the name) and the write order (the layout's member
// order, else the fields' order).
func keyedOrder(p *plan, d *layoutDecl, infos []rawFieldInfo, order []int) ([]pendingKey, error) {
	t := p.typ
	pks := make([]pendingKey, len(infos))
	type ranked struct{ rank, i int }
	ranks := make([]ranked, len(infos))
	owner := map[int32]string{}
	for i, fi := range infos {
		name := fi.tag.name
		if name == "" {
			name = fi.sf.Name
		}
		pk := pendingKey{key: TypeID(name), member: -1}
		for _, f := range fi.tag.formers {
			pk.formers = append(pk.formers, TypeID(f))
		}
		rank := len(d.members) + i
		if d.hasMembers {
			m := -1
			for j := range d.members {
				if d.members[j].name == name {
					if m >= 0 {
						m = -2
						break
					}
					m = j
				}
			}
			if m < 0 {
				m = -1
				for j := range d.members {
					if d.members[j].hasKey && d.members[j].key == pk.key {
						m = j
						break
					}
				}
			}
			if m < 0 {
				return nil, &TagError{Type: t, Field: fi.path, Detail: fmt.Sprintf("no member %q in the layout", name)}
			}
			lm := &d.members[m]
			if lm.hasKey {
				pk.key = lm.key // published keys win over recomputed ones (spec §7.6)
			}
			for _, f := range pk.formers {
				if !slices.Contains(lm.formers, f) {
					return nil, &TagError{Type: t, Field: fi.path, Detail: fmt.Sprintf("former key 0x%08X is not one the layout publishes for %s", uint32(f), lm.name)}
				}
			}
			pk.formers = slices.Clone(lm.formers)
			pk.member = m
			rank = m
		}
		for _, k := range append([]int32{pk.key}, pk.formers...) {
			if prev, ok := owner[k]; ok {
				return nil, &TagError{Type: t, Field: fi.path, Detail: fmt.Sprintf("key 0x%08X is also %s's", uint32(k), prev)}
			}
			owner[k] = fi.path
		}
		pks[i] = pk
		ranks[i] = ranked{rank, i}
	}
	slices.SortStableFunc(ranks, func(a, b ranked) int { return cmp.Compare(a.rank, b.rank) })
	for pos, r := range ranks {
		order[r.i] = pos
	}
	return pks, nil
}

// rawLayout decides whether the type, held by value, is one raw block, and places its fields.
func (b *builder) rawLayout(p *plan, d *layoutDecl) error {
	t := p.typ
	if p.entry || d.raw == no || p.format != formatPositional {
		return nil
	}
	if d.rawEnc != nil {
		e := d.rawEnc
		if len(p.fields) == 0 {
			// An empty struct: .NET writes its one byte.
			if e.size > 1 {
				return typeErr(t, "an empty struct is one byte, the layout says %d", e.size)
			}
			p.raw, p.size, p.align = true, 1, 1
			return nil
		}
		if e.size <= 0 {
			return typeErr(t, "the raw layout has no size (a host-dependent layout, spec §10 item 3)")
		}
		p.raw, p.size, p.align = true, e.size, 1
		for i := range p.fields {
			f := &p.fields[i]
			if !f.c.rawCapable() || f.c.kind == kDTO {
				return &TagError{Type: t, Field: f.name, Detail: fmt.Sprintf("%s cannot be a field of a raw struct", f.c.typ)}
			}
			lf := e.fields[i]
			f.off = lf.off
			if err := checkEncoding(f.c, lf.enc); err != nil {
				return &TagError{Type: t, Field: f.name, Detail: fmt.Sprintf("raw field %s is %s: %v", lf.name, lf.enc.text, err)}
			}
			if f.off+f.c.size > p.size {
				return &TagError{Type: t, Field: f.name, Detail: fmt.Sprintf("at %d, %d bytes do not fit in %d", f.off, f.c.size, p.size)}
			}
			p.align = max(p.align, f.c.align)
		}
		return b.sortRaw(p)
	}
	if d.raw == yes {
		return typeErr(t, "raw=true needs the members of the raw layout (raw(n){...})")
	}
	// Inferred: raw when every field is a fixed-size value (spec §5.1), laid out as CoreCLR does on a 64-bit host
	// with sequential layout (spec §5.2).
	if len(p.fields) == 0 {
		p.raw, p.size, p.align = true, 1, 1
		return nil
	}
	end, align := 0, 1
	for i := range p.fields {
		c := p.fields[i].c
		if !c.rawCapable() {
			if c.size > 0 {
				p.rawErr = fmt.Sprintf("field %s (%s) has no raw layout of its own", p.fields[i].name, c.typ)
			}
			return nil
		}
	}
	for i := range p.fields {
		f := &p.fields[i]
		if f.c.align == 0 {
			return &TagError{Type: t, Field: f.name, Detail: fmt.Sprintf("a raw struct holding %s needs its offsets: declare them in NinoLayout (raw(n){...})", f.c.typ)}
		}
		f.off = roundUp(end, f.c.align)
		end = f.off + f.c.size
		align = max(align, f.c.align)
	}
	p.raw, p.size, p.align = true, roundUp(end, align), align
	return nil
}

func (b *builder) sortRaw(p *plan) error {
	slices.SortStableFunc(p.fields, func(a, b field) int { return cmp.Compare(a.off, b.off) })
	for i := 1; i < len(p.fields); i++ {
		prev := &p.fields[i-1]
		if p.fields[i].off < prev.off+prev.c.size {
			return &TagError{Type: p.typ, Field: p.fields[i].name, Detail: "overlaps " + prev.name}
		}
	}
	return nil
}

func roundUp(n, a int) int { return (n + a - 1) / a * a }

// checkEncoding checks that the codec c writes what the layout's encoding e says.
func checkEncoding(c *codec, e *encoding) error {
	mismatch := func() error { return fmt.Errorf("the Go type %s does not write %s", c.typ, e.text) }
	switch e.kind {
	case "raw":
		switch {
		case c.quirk:
			return nil // .NET publishes these wrong (spec §10, item 1)
		case c.kind == kRawStruct:
			if e.size < 0 {
				if c.size == 1 && len(c.plan.fields) == 0 {
					return nil
				}
				return mismatch()
			}
			if e.size != c.size {
				return fmt.Errorf("%d bytes, the Go type takes %d", e.size, c.size)
			}
			return nil
		case c.rawCapable() && c.kind != kDTO:
			if e.size != c.size {
				return fmt.Errorf("%d bytes, the Go type takes %d", e.size, c.size)
			}
			return nil
		}
	case "string":
		if (c.kind == kString || c.kind == kStringPtr) && !c.utf8 {
			return nil
		}
	case "utf8":
		if (c.kind == kString || c.kind == kStringPtr) && c.utf8 {
			return nil
		}
	case "object":
		if c.kind == kIface {
			return nil
		}
	case "type":
		switch c.kind {
		case kClass, kIface:
			return nil
		case kStruct:
			if !c.plan.entry {
				return nil
			}
		}
	case "nullable":
		if c.kind == kNullable && len(e.args) == 1 {
			return checkEncoding(c.elem, e.args[0])
		}
	case "seq":
		if len(e.args) != 1 {
			return mismatch()
		}
		switch c.kind {
		case kBytes:
			if e.args[0].kind == "raw" && e.args[0].size == 1 {
				return nil
			}
		case kSlice, kPrimSlice, kSet:
			ec := c.elem
			if c.kind == kSet {
				ec = c.key
			}
			return checkEncoding(ec, e.args[0])
		case kStride:
			if e.args[0].kind == "raw" && e.args[0].size == c.size {
				return nil
			}
			return fmt.Errorf("a stride of %d bytes, the Go type takes %d", e.args[0].size, c.size)
		}
	case "map":
		if len(e.args) != 2 {
			return mismatch()
		}
		switch {
		case c.kind == kMap:
			if err := checkEncoding(c.key, e.args[0]); err != nil {
				return err
			}
			return checkEncoding(c.elem, e.args[1])
		case c.kind == kSlice && c.elem.kind == kStruct && c.elem.plan.entry:
			fs := c.elem.plan.fields
			if err := checkEncoding(fs[0].c, e.args[0]); err != nil {
				return err
			}
			return checkEncoding(fs[1].c, e.args[1])
		}
	case "pair", "tuple":
		if c.kind == kStruct && c.plan.format == formatPositional && len(c.plan.fields) == len(e.args) {
			for i := range e.args {
				if err := checkEncoding(c.plan.fields[i].c, e.args[i]); err != nil {
					return err
				}
			}
			return nil
		}
	case "datetimeoffset":
		if c.kind == kDTO {
			return nil
		}
	case "bigint":
		if c.kind == kBigInt {
			return nil
		}
	case "version":
		if c.kind == kVersion {
			return nil
		}
	default:
		return fmt.Errorf("the encoding %s is not supported by struct tags", e.kind)
	}
	return mismatch()
}

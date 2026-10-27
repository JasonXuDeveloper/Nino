package nino

import (
	"cmp"
	"fmt"
	"math/big"
	"reflect"
	"slices"
	"unsafe"
)

// Struct tags (tagged.go): the builder, which makes the codec of each Go type a plan reaches.

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
		c.kind, c.min, c.utf8 = kString, 1, variant == variantUTF8
		if c.utf8 {
			c.read = func(r *Reader, v reflect.Value) { v.SetString(r.UTF8()) }
			c.write = func(w *Writer, v reflect.Value) { w.UTF8(v.String()) }
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
		c.read = func(r *Reader, v reflect.Value) { v.Set(reflect.ValueOf(r.I128())) }
		c.write = func(w *Writer, v reflect.Value) { w.I128(v.Interface().(Int128)) }
	case kUInt128:
		c.read = func(r *Reader, v reflect.Value) { v.Set(reflect.ValueOf(r.U128())) }
		c.write = func(w *Writer, v reflect.Value) { w.U128(v.Interface().(UInt128)) }
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
		c.kind, c.min, c.utf8 = kStringPtr, 1, variant == variantUTF8
		utf8 := c.utf8
		c.read = func(r *Reader, v reflect.Value) {
			var s string
			var ok bool
			if utf8 {
				s, ok = r.NullableUTF8()
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
				w.NullableUTF8(s, ok)
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
	if f, ok := blockSlices[et]; ok && b.fast {
		// A slice of bools, dates, decimals, Guids or 128-bit integers on the unsafe path: one block, as generated
		// code reads and writes it (ReadBools, ReadBlock), with no reflect.Value an element.
		c.read, c.write = f()
		return nil
	}
	elemSize := int(et.Size())
	if b.fast && e.kind == kString && !e.utf8 {
		// A slice of strings (of any type whose kind is string: the same layout) in the default encoding, on the
		// unsafe path: read and written as generated code does (ReadSlice and WriteStrings), with no reflect.Value
		// an element. Strings cannot nest, so it counts no depth.
		c.read = func(r *Reader, v reflect.Value) {
			n := r.Length(1)
			if n < 0 {
				v.SetZero()
				return
			}
			capacity := r.SliceCap(n, elemSize)
			s := reflect.MakeSlice(t, capacity, capacity)
			p := unsafe.Slice((*string)(s.UnsafePointer()), capacity)
			for i := 0; i < n; i++ {
				if i == len(p) {
					grown := reflect.MakeSlice(t, min(n, 2*len(p)), min(n, 2*len(p)))
					reflect.Copy(grown, s)
					s = grown
					p = unsafe.Slice((*string)(s.UnsafePointer()), s.Len())
				}
				p[i] = r.String()
				if r.err != nil {
					return
				}
			}
			v.Set(s)
		}
		c.write = func(w *Writer, v reflect.Value) {
			if v.IsNil() {
				w.Null()
				return
			}
			WriteStrings(w, unsafe.Slice((*string)(v.UnsafePointer()), v.Len()))
		}
		return nil
	}
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
	if f, ok := fastMaps[t]; ok && b.fast && !c.enter {
		c.read, c.write = f.codecs(pairMin)
		return nil
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
		// The pairs copied out once (two slices, no allocation per pair), then written in key order.
		n := v.Len()
		keys := reflect.MakeSlice(reflect.SliceOf(kt), n, n)
		var vals reflect.Value
		if vc != nil {
			vals = reflect.MakeSlice(reflect.SliceOf(vt), n, n)
		}
		it := v.MapRange()
		for i := 0; i < n && it.Next(); i++ {
			keys.Index(i).SetIterKey(it)
			if vc != nil {
				vals.Index(i).SetIterValue(it)
			}
		}
		order := keyOrder(keys)
		for _, i := range order {
			if w.err != nil {
				break
			}
			kc.write(w, keys.Index(i))
			if vc != nil {
				vc.write(w, vals.Index(i))
			}
		}
		if c.enter {
			w.ExitStruct()
		}
	}
	return nil
}

// blockSlices holds, for the element types whose slices generated code reads and writes as one block other than
// numbers (bool and the BlockValue types), a read and a write on the unsafe path. A slice type with such elements
// has their layout whatever its name, so the codecs set and take it as a []T.
var blockSlices = map[reflect.Type]func() (func(*Reader, reflect.Value), func(*Writer, reflect.Value)){
	reflect.TypeFor[bool](): func() (func(*Reader, reflect.Value), func(*Writer, reflect.Value)) {
		return blockSlice(ReadBools, WriteBools)
	},
	reflect.TypeFor[DateTime](): blockValues[DateTime],
	reflect.TypeFor[DateOnly](): blockValues[DateOnly],
	reflect.TypeFor[TimeOnly](): blockValues[TimeOnly],
	reflect.TypeFor[Decimal]():  blockValues[Decimal],
	reflect.TypeFor[Guid]():     blockValues[Guid],
	reflect.TypeFor[Int128]():   blockValues[Int128],
	reflect.TypeFor[UInt128]():  blockValues[UInt128],
}

func blockValues[T BlockValue]() (func(*Reader, reflect.Value), func(*Writer, reflect.Value)) {
	return blockSlice(ReadBlock[T], WriteBlock[T])
}

func blockSlice[T any](read func(*Reader) []T, write func(*Writer, []T)) (func(*Reader, reflect.Value), func(*Writer, reflect.Value)) {
	return func(r *Reader, v reflect.Value) {
			s := read(r)
			if r.err == nil {
				*(*[]T)(unsafe.Pointer(v.UnsafeAddr())) = s
			}
		}, func(w *Writer, v reflect.Value) {
			if v.IsNil() {
				w.Null()
				return
			}
			write(w, unsafe.Slice((*T)(v.UnsafePointer()), v.Len()))
		}
}

// fastMaps holds, for the map types of built-in key and value types a struct most often has, a read and a write
// on the unsafe path: as generated code does ([ReadMap], and the pairs written in key order as [keyOrder] gives
// them), with no reflect.Value a key or value.
var fastMaps = map[reflect.Type]fastMap{}

type fastMap struct {
	codecs func(pairMin int) (func(*Reader, reflect.Value), func(*Writer, reflect.Value))
}

func init() {
	addFastMaps((*Reader).I32, (*Writer).I32)
	addFastMaps((*Reader).I64, (*Writer).I64)
	addFastMaps((*Reader).String, (*Writer).String)
}

// addFastMaps adds the maps of keys K (read and written by rk and wk) to values int32, int64 and string.
func addFastMaps[K cmp.Ordered](rk func(*Reader) K, wk func(*Writer, K)) {
	addFastMap(rk, wk, (*Reader).I32, (*Writer).I32)
	addFastMap(rk, wk, (*Reader).I64, (*Writer).I64)
	addFastMap(rk, wk, (*Reader).String, (*Writer).String)
}

func addFastMap[K cmp.Ordered, V any](rk func(*Reader) K, wk func(*Writer, K), rv func(*Reader) V, wv func(*Writer, V)) {
	fastMaps[reflect.TypeFor[map[K]V]()] = fastMap{codecs: func(pairMin int) (func(*Reader, reflect.Value), func(*Writer, reflect.Value)) {
		read := func(r *Reader, v reflect.Value) {
			m := ReadMap(r, pairMin, rk, rv)
			if r.err == nil {
				v.Set(reflect.ValueOf(m))
			}
		}
		write := func(w *Writer, v reflect.Value) {
			p := v.UnsafePointer()
			m := *(*map[K]V)(unsafe.Pointer(&p))
			if m == nil {
				w.Null()
				return
			}
			w.Length(len(m))
			var room [16]Entry[K, V]
			e := room[:0]
			if len(m) > len(room) {
				e = make([]Entry[K, V], 0, len(m))
			}
			for k, x := range m {
				e = append(e, Entry[K, V]{k, x})
			}
			slices.SortFunc(e, func(a, b Entry[K, V]) int { return cmp.Compare(a.Key, b.Key) })
			for _, x := range e {
				wk(w, x.Key)
				wv(w, x.Value)
			}
		}
		return read, write
	}}
}

// keyOrder returns the indexes of keys (a slice) in key order when the keys have one (numbers, strings, bools),
// so that writing a map gives the same bytes every time; else in the order given (Go's map order).
func keyOrder(keys reflect.Value) []int {
	n := keys.Len()
	order := make([]int, n)
	for i := range order {
		order[i] = i
	}
	// Each key read once (not through reflect at each comparison), then the indexes sorted by them.
	switch keys.Type().Elem().Kind() {
	case reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		sortBy(order, keys, reflect.Value.Int)
	case reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		sortBy(order, keys, reflect.Value.Uint)
	case reflect.Float32, reflect.Float64:
		sortBy(order, keys, reflect.Value.Float)
	case reflect.String:
		sortBy(order, keys, reflect.Value.String)
	case reflect.Bool:
		sortBy(order, keys, func(v reflect.Value) int {
			if v.Bool() {
				return 1
			}
			return 0
		})
	}
	return order
}

// sortBy sorts order (the indexes of keys) by the key each gives: each key read once, into the stack for maps of
// up to 32 keys.
func sortBy[K cmp.Ordered](order []int, keys reflect.Value, key func(reflect.Value) K) {
	var room [32]K
	k := room[:0]
	if len(order) > len(room) {
		k = make([]K, 0, len(order))
	}
	for i := range order {
		k = append(k, key(keys.Index(i)))
	}
	slices.SortFunc(order, func(a, b int) int { return cmp.Compare(k[a], k[b]) })
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

package nino

import (
	"fmt"
	"math/big"
	"reflect"
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
		// Aligned to 16 in the canonical layout (spec §5.2), as .NET 8 and later lay it out.
		return b.leaf(c, kInt128, 16, 16, true)
	case typeUInt128:
		return b.leaf(c, kUInt128, 16, 16, true)
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
			return typeErr(t, "header=TypeId but no type id: give the layout an id or implement NinoTypeID")
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
	if p.header == headerPresence || p.header == headerTypeID {
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
	case headerPresence:
		return "Presence"
	case headerTypeID:
		return "TypeId"
	}
	return "auto"
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

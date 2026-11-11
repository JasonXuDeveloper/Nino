package nino

import (
	"cmp"
	"reflect"
	"slices"
	"unsafe"
)

// The builder's codecs of slices, arrays and maps: blocks where generated code takes one, the most common maps on the
// unsafe path, and map pairs written in key order.

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
				w.NullLength()
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
				w.NullLength()
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
		// unsafe path: read and written as generated code does (ReadStrings and WriteStrings), with no reflect.Value
		// an element. Strings cannot nest, so it counts no depth.
		c.read, c.write = blockSlice(ReadStrings, WriteStrings)
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
			w.NullLength()
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
			w.NullLength()
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
		nan := false
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
			// A NaN key adds an entry every time (see repeatedNaN): a second one is a repeat, as in .NET.
			if m.Len() != i+1 || !k.Equal(k) && nan {
				r.Fail(CodeDuplicateKey, "")
				break
			}
			nan = nan || !k.Equal(k)
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
			w.NullLength()
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
				w.NullLength()
				return
			}
			write(w, unsafe.Slice((*T)(v.UnsafePointer()), v.Len()))
		}
}

// fastMaps holds, for the map types of built-in key and value types a struct most often has, a read and a write
// on the unsafe path: as generated code does ([readMap], and the pairs written in key order as [keyOrder] gives
// them), with no reflect.Value a key or value.
var fastMaps = map[reflect.Type]fastMap{}

type fastMap struct {
	codecs func(pairMin int) (func(*Reader, reflect.Value), func(*Writer, reflect.Value))
}

func init() {
	addFastMaps((*Reader).I32, (*Writer).I32)
	addFastMaps((*Reader).I64, (*Writer).I64)
	addFastMaps((*Reader).String, (*Writer).String)
	primitiveFastMap[int32, int32]()
	primitiveFastMap[int32, int64]()
	primitiveFastMap[int64, int32]()
	primitiveFastMap[int64, int64]()
}

// primitiveFastMap makes the fast map of integer keys and values read its pairs as one block ([ReadPrimitiveMap])
// and write them, in key order, as one block ([WritePrimitiveEntries]).
func primitiveFastMap[K, V Primitive]() {
	fastMaps[reflect.TypeFor[map[K]V]()] = fastMap{codecs: func(int) (func(*Reader, reflect.Value), func(*Writer, reflect.Value)) {
		read := func(r *Reader, v reflect.Value) {
			m := ReadPrimitiveMap[K, V](r)
			if r.err == nil {
				v.Set(reflect.ValueOf(m))
			}
		}
		write := func(w *Writer, v reflect.Value) {
			var room [16]Entry[K, V]
			WritePrimitiveEntries(w, sortedEntries(mapAt[K, V](v), room[:0]))
		}
		return read, write
	}}
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
			m := readMap(r, pairMin, rk, rv)
			if r.err == nil {
				v.Set(reflect.ValueOf(m))
			}
		}
		write := func(w *Writer, v reflect.Value) {
			var room [16]Entry[K, V]
			writeEntries(w, sortedEntries(mapAt[K, V](v), room[:0]), wk, wv)
		}
		return read, write
	}}
}

// mapAt returns the map v holds (a map[K]V), on the unsafe path.
func mapAt[K comparable, V any](v reflect.Value) map[K]V {
	p := v.UnsafePointer()
	return *(*map[K]V)(unsafe.Pointer(&p))
}

// sortedEntries returns the pairs of m in key order (nil for a nil map), in room when they fit.
func sortedEntries[K cmp.Ordered, V any](m map[K]V, room []Entry[K, V]) []Entry[K, V] {
	if m == nil {
		return nil
	}
	e := room[:0]
	if len(m) > cap(room) {
		e = make([]Entry[K, V], 0, len(m))
	}
	for k, x := range m {
		e = append(e, Entry[K, V]{k, x})
	}
	slices.SortFunc(e, func(a, b Entry[K, V]) int { return cmp.Compare(a.Key, b.Key) })
	return e
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

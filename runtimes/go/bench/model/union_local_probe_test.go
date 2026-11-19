package model

import (
	"fmt"
	"reflect"
	"testing"
	"unsafe"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

type localClassSlab[T any] struct {
	slots []T
	n     int
}

func (s *localClassSlab[T]) slot(r *nino.Reader) *T {
	if len(s.slots) == 0 {
		var z T
		s.n = r.SliceCap(min(8, max(1, s.n*2)), int(unsafe.Sizeof(z)))
		s.slots = make([]T, s.n)
	}
	p := &s.slots[0]
	s.slots = s.slots[1:]
	return p
}

func readUnionItemSlab(r *nino.Reader, s *localClassSlab[UnionItem]) *UnionItem {
	if !r.Present() {
		return nil
	}
	value := s.slot(r)
	if depth, ok := r.EnterBody(); ok {
		readUnionItemBody(r, value)
		r.ExitBody(depth)
	} else {
		nino.ReadClassInto(r, value, &unionItemClass)
	}
	return value
}

func readUnionValueFromSlab(r *nino.Reader, s *localClassSlab[UnionItem]) UnionValue {
	id := r.TypeID()
	if id == nino.NullTypeID {
		return nil
	}
	if !r.EnterStruct() {
		return nil
	}
	var v UnionValue
	switch id {
	case 546808484:
		v = UnionValueInt32{Value: r.I32()}
	case 337757159:
		v = UnionValueInt64{Value: r.I64()}
	case -65934901:
		if value := r.StringPtr(); value != nil {
			v = UnionValueString{Value: value}
		} else {
			r.Fail(nino.CodeInvalidData, "a union case without a value")
		}
	case 791437675:
		v = UnionValueUnionPoint{Value: readUnionPoint(r)}
	case 635318394:
		if value := readUnionItemSlab(r, s); value != nil {
			v = value
		} else {
			r.Fail(nino.CodeInvalidData, "a union case without a value")
		}
	default:
		r.UnknownType(id)
	}
	r.ExitStruct()
	if r.Failed() {
		return nil
	}
	return v
}

const localBoxWindow = 16

type localBoxBatch[T any] struct {
	values  [localBoxWindow]T
	indices [localBoxWindow]int
	n       int
}

func (b *localBoxBatch[T]) add(i int, value T) {
	b.indices[b.n] = i
	b.values[b.n] = value
	b.n++
}

func flushLocalBoxes[T any, I any](dest []I, b *localBoxBatch[T]) {
	if b.n == 0 {
		return
	}
	if b.n == 1 {
		dest[b.indices[0]] = any(b.values[0]).(I)
		b.n = 0
		return
	}
	box := reflect.ValueOf(b.values)
	for i := range b.n {
		dest[b.indices[i]] = box.Index(i).Interface().(I)
	}
	b.n = 0
}

func readUnionArrayClassSlab(r *nino.Reader) []UnionValue {
	n := r.Length(4)
	if n < 0 {
		return nil
	}
	var z UnionValue
	out := make([]UnionValue, 0, r.SliceCap(n, int(unsafe.Sizeof(z))))
	var slab localClassSlab[UnionItem]
	for range n {
		value := readUnionValueFromSlab(r, &slab)
		if r.Failed() {
			return nil
		}
		out = append(out, value)
	}
	return out
}

func readUnionArrayFresh(r *nino.Reader) []UnionValue {
	n := r.Length(4)
	if n < 0 {
		return nil
	}
	var z UnionValue
	out := make([]UnionValue, 0, r.SliceCap(n, int(unsafe.Sizeof(z))))
	for range n {
		value := ReadUnionValue(r)
		if r.Failed() {
			return nil
		}
		out = append(out, value)
	}
	return out
}

func readUnionArrayBoxSlabs(r *nino.Reader) []UnionValue {
	n := r.Length(4)
	if n < 0 {
		return nil
	}
	var z UnionValue
	out := make([]UnionValue, 0, r.SliceCap(n, int(unsafe.Sizeof(z))))
	var integers localBoxBatch[UnionValueInt32]
	var longs localBoxBatch[UnionValueInt64]
	var points localBoxBatch[UnionValueUnionPoint]
	var classes localClassSlab[UnionItem]
	for i := range n {
		id := r.TypeID()
		var value UnionValue
		if id != nino.NullTypeID {
			if !r.EnterStruct() {
				return nil
			}
			switch id {
			case 546808484:
				integers.add(i, UnionValueInt32{Value: r.I32()})
			case 337757159:
				longs.add(i, UnionValueInt64{Value: r.I64()})
			case -65934901:
				if text := r.StringPtr(); text != nil {
					value = UnionValueString{Value: text}
				} else {
					r.Fail(nino.CodeInvalidData, "a union case without a value")
				}
			case 791437675:
				points.add(i, UnionValueUnionPoint{Value: readUnionPoint(r)})
			case 635318394:
				if object := readUnionItemSlab(r, &classes); object != nil {
					value = object
				} else {
					r.Fail(nino.CodeInvalidData, "a union case without a value")
				}
			default:
				r.UnknownType(id)
			}
			r.ExitStruct()
		}
		if r.Failed() {
			return nil
		}
		out = append(out, value)
		if (i+1)%localBoxWindow == 0 || i+1 == n {
			flushLocalBoxes(out, &integers)
			flushLocalBoxes(out, &longs)
			flushLocalBoxes(out, &points)
		}
	}
	return out
}

func readUnionArrayDenseSlabs(r *nino.Reader) []UnionValue {
	n := r.Length(4)
	if n < 0 {
		return nil
	}
	var z UnionValue
	out := make([]UnionValue, 0, r.SliceCap(n, int(unsafe.Sizeof(z))))
	var integers localBoxBatch[UnionValueInt32]
	var longs localBoxBatch[UnionValueInt64]
	var points localBoxBatch[UnionValueUnionPoint]
	var classes localClassSlab[UnionItem]
	for i := range n {
		id := r.TypeID()
		var value UnionValue
		if id != nino.NullTypeID {
			if !r.EnterStruct() {
				return nil
			}
			switch id {
			case 546808484:
				integers.add(i, UnionValueInt32{Value: r.I32()})
			case 337757159:
				longs.add(i, UnionValueInt64{Value: r.I64()})
			case -65934901:
				if text := r.StringPtr(); text != nil {
					value = UnionValueString{Value: text}
				} else {
					r.Fail(nino.CodeInvalidData, "a union case without a value")
				}
			case 791437675:
				points.add(i, UnionValueUnionPoint{Value: readUnionPoint(r)})
			case 635318394:
				if object := readUnionItemSlab(r, &classes); object != nil {
					value = object
				} else {
					r.Fail(nino.CodeInvalidData, "a union case without a value")
				}
			default:
				r.UnknownType(id)
			}
			r.ExitStruct()
		}
		if r.Failed() {
			return nil
		}
		out = append(out, value)
		if integers.n == localBoxWindow {
			flushLocalBoxes(out, &integers)
		}
		if longs.n == localBoxWindow {
			flushLocalBoxes(out, &longs)
		}
		if points.n == localBoxWindow {
			flushLocalBoxes(out, &points)
		}
	}
	flushLocalBoxes(out, &integers)
	flushLocalBoxes(out, &longs)
	flushLocalBoxes(out, &points)
	return out
}

func readTaggedUnionArrayBoxSlabs(r *nino.Reader) []UnionTagged {
	n := r.Length(4)
	if n < 0 {
		return nil
	}
	var z UnionTagged
	out := make([]UnionTagged, 0, r.SliceCap(n, int(unsafe.Sizeof(z))))
	var integers localBoxBatch[UnionTaggedInt32]
	var shorts localBoxBatch[UnionTaggedInt16]
	for i := range n {
		var value UnionTagged
		id := r.TypeID()
		switch id {
		case nino.NullTypeID:
		case 546808484:
			integers.add(i, UnionTaggedInt32{Value: r.I32()})
		case 1083077026:
			shorts.add(i, UnionTaggedInt16{Value: r.I16()})
		case -65934901:
			if text := r.StringPtr(); text != nil {
				value = UnionTaggedString{Value: text}
			} else {
				r.Fail(nino.CodeInvalidData, "a union case without a value")
			}
		default:
			r.UnknownType(id)
		}
		if r.Failed() {
			return nil
		}
		out = append(out, value)
		if (i+1)%localBoxWindow == 0 || i+1 == n {
			flushLocalBoxes(out, &integers)
			flushLocalBoxes(out, &shorts)
		}
	}
	return out
}

func readTaggedUnionArrayDenseSlabs(r *nino.Reader) []UnionTagged {
	n := r.Length(4)
	if n < 0 {
		return nil
	}
	var z UnionTagged
	out := make([]UnionTagged, 0, r.SliceCap(n, int(unsafe.Sizeof(z))))
	var integers localBoxBatch[UnionTaggedInt32]
	var shorts localBoxBatch[UnionTaggedInt16]
	for i := range n {
		var value UnionTagged
		id := r.TypeID()
		switch id {
		case nino.NullTypeID:
		case 546808484:
			integers.add(i, UnionTaggedInt32{Value: r.I32()})
		case 1083077026:
			shorts.add(i, UnionTaggedInt16{Value: r.I16()})
		case -65934901:
			if text := r.StringPtr(); text != nil {
				value = UnionTaggedString{Value: text}
			} else {
				r.Fail(nino.CodeInvalidData, "a union case without a value")
			}
		default:
			r.UnknownType(id)
		}
		if r.Failed() {
			return nil
		}
		out = append(out, value)
		if integers.n == localBoxWindow {
			flushLocalBoxes(out, &integers)
		}
		if shorts.n == localBoxWindow {
			flushLocalBoxes(out, &shorts)
		}
	}
	flushLocalBoxes(out, &integers)
	flushLocalBoxes(out, &shorts)
	return out
}

func localUnionCases(n int) []UnionValue {
	values := make([]UnionValue, n)
	for i := range values {
		switch i % 5 {
		case 0:
			values[i] = UnionValueInt32{Value: int32(i * 7)}
		case 1:
			values[i] = UnionValueInt64{Value: int64(i) * 1_000_000_007}
		case 2:
			text := fmt.Sprintf("value %d", i)
			values[i] = UnionValueString{Value: &text}
		case 3:
			values[i] = UnionValueUnionPoint{Value: UnionPoint{X: int32(i), Y: int32(-i), Z: float32(i) * 0.5}}
		case 4:
			text := fmt.Sprintf("item %d", i)
			values[i] = &UnionItem{Id: int32(i), Name: &text, Score: float64(i) * 1.25}
		}
	}
	return values
}

func localTaggedUnionCases(n int) []UnionTagged {
	values := make([]UnionTagged, n)
	for i := range values {
		switch i % 3 {
		case 0:
			values[i] = UnionTaggedInt32{Value: int32(i * 7)}
		case 1:
			values[i] = UnionTaggedInt16{Value: int16(i - 500)}
		case 2:
			text := fmt.Sprintf("tag %d", i)
			values[i] = UnionTaggedString{Value: &text}
		}
	}
	return values
}

func checkLocalUnion[T any](t *testing.T, want T, write func(*nino.Writer, T), read func(*nino.Reader) T) {
	bytes, err := nino.Serialize(want, write)
	if err != nil {
		t.Fatal(err)
	}
	got, err := nino.DeserializeExact(bytes, read)
	if err != nil || !reflect.DeepEqual(got, want) {
		t.Fatalf("round trip mismatch: %v", err)
	}
	for i := range bytes {
		if _, err := nino.DeserializeExact(bytes[:i], read); err == nil {
			t.Fatalf("accepted truncated prefix %d", i)
		}
	}
}

func TestLocalUnionStorage(t *testing.T) {
	for _, n := range []int{0, 1, 2, 15, 16, 17, 1000} {
		values := localUnionCases(n)
		checkLocalUnion(t, values, WriteCaseUnionArray, readUnionArrayClassSlab)
		checkLocalUnion(t, values, WriteCaseUnionArray, readUnionArrayDenseSlabs)
		checkLocalUnion(t, localTaggedUnionCases(n), WriteCaseTaggedUnionArray, readTaggedUnionArrayDenseSlabs)
		checkLocalUnion(t, values, WriteCaseUnionArray, readUnionArrayBoxSlabs)
		checkLocalUnion(t, localTaggedUnionCases(n), WriteCaseTaggedUnionArray, readTaggedUnionArrayBoxSlabs)
	}
}

var localUnionSink []UnionValue
var localTaggedSink []UnionTagged

func BenchmarkLocalUnionStorage(b *testing.B) {
	bytes, err := nino.Serialize(localUnionCases(1000), WriteCaseUnionArray)
	if err != nil {
		b.Fatal(err)
	}
	for _, tc := range []struct {
		name string
		read func(*nino.Reader) []UnionValue
	}{
		{"Baseline", ReadCaseUnionArray},
		{"ClassSlab", readUnionArrayClassSlab},
		{"Fresh", readUnionArrayFresh},
		{"DenseSlabs", readUnionArrayDenseSlabs},
		{"AllSlabs", readUnionArrayBoxSlabs},
	} {
		b.Run(tc.name, func(b *testing.B) {
			b.ReportAllocs()
			for range b.N {
				value, err := nino.DeserializeExact(bytes, tc.read)
				if err != nil {
					b.Fatal(err)
				}
				localUnionSink = value
			}
		})
	}
}

func BenchmarkLocalTaggedUnionStorage(b *testing.B) {
	bytes, err := nino.Serialize(localTaggedUnionCases(1000), WriteCaseTaggedUnionArray)
	if err != nil {
		b.Fatal(err)
	}
	for _, tc := range []struct {
		name string
		read func(*nino.Reader) []UnionTagged
	}{
		{"Baseline", ReadCaseTaggedUnionArray},
		{"AllSlabs", readTaggedUnionArrayBoxSlabs},
		{"DenseSlabs", readTaggedUnionArrayDenseSlabs},
	} {
		b.Run(tc.name, func(b *testing.B) {
			b.ReportAllocs()
			for range b.N {
				value, err := nino.DeserializeExact(bytes, tc.read)
				if err != nil {
					b.Fatal(err)
				}
				localTaggedSink = value
			}
		})
	}
}

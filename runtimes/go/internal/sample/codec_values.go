package sample

import (
	"encoding/binary"
	"math"
	"unsafe"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

var le = binary.LittleEndian

// Class descriptors, set in init: a recursive type's functions refer to its descriptor.
var (
	integersClass          nino.Class[Integers]
	floatsClass            nino.Class[Floats]
	scalarsClass           nino.Class[Scalars]
	datesClass             nino.Class[Dates]
	enumsClass             nino.Class[Enums]
	bigValuesClass         nino.Class[BigValues]
	stringsClass           nino.Class[Strings]
	stringCollectionsClass nino.Class[StringCollections]
	bytesClass             nino.Class[Bytes]
	pointClass             nino.Class[Point]
	structsClass           nino.Class[Structs]
	outerClass             nino.Class[Outer]
	middleClass            nino.Class[Middle]
	innerClass             nino.Class[Inner]
	collectionsClass       nino.Class[Collections]
	nullablesClass         nino.Class[Nullables]
	emptyClass             nino.Class[Empty]
	emptiesClass           nino.Class[Empties]
)

func init() {
	integersClass = nino.Class[Integers]{Read: readIntegersBody, Write: writeIntegersBody, Deferrable: true}
	floatsClass = nino.Class[Floats]{Read: readFloatsBody, Write: writeFloatsBody, Deferrable: true}
	scalarsClass = nino.Class[Scalars]{Read: readScalarsBody, Write: writeScalarsBody, Deferrable: true}
	datesClass = nino.Class[Dates]{Read: readDatesBody, Write: writeDatesBody, Deferrable: true}
	enumsClass = nino.Class[Enums]{Read: readEnumsBody, Write: writeEnumsBody, Deferrable: true}
	bigValuesClass = nino.Class[BigValues]{Read: readBigValuesBody, Write: writeBigValuesBody, Deferrable: true}
	stringsClass = nino.Class[Strings]{Read: readStringsBody, Write: writeStringsBody, Deferrable: true}
	stringCollectionsClass = nino.Class[StringCollections]{Read: readStringCollectionsBody, Write: writeStringCollectionsBody, Deferrable: true}
	bytesClass = nino.Class[Bytes]{Read: readBytesBody, Write: writeBytesBody, Deferrable: true}
	pointClass = nino.Class[Point]{Read: readPointBody, Write: writePointBody, Deferrable: true}
	structsClass = nino.Class[Structs]{Read: readStructsBody, Write: writeStructsBody, Deferrable: true}
	outerClass = nino.Class[Outer]{Read: readOuterBody, Write: writeOuterBody, Deferrable: true}
	middleClass = nino.Class[Middle]{Read: readMiddleBody, Write: writeMiddleBody, Deferrable: true}
	innerClass = nino.Class[Inner]{Read: readInnerBody, Write: writeInnerBody, Deferrable: true}
	collectionsClass = nino.Class[Collections]{Read: readCollectionsBody, Write: writeCollectionsBody, Deferrable: true}
	nullablesClass = nino.Class[Nullables]{Read: readNullablesBody, Write: writeNullablesBody, Deferrable: true}
	emptyClass = nino.Class[Empty]{Read: func(*nino.Reader, *Empty) {}, Write: func(*nino.Writer, *Empty) {}, Deferrable: true}
	emptiesClass = nino.Class[Empties]{Read: readEmptiesBody, Write: writeEmptiesBody, Deferrable: true}
}

// ---------------------------------------------------------------- Integers: one raw run of 62 bytes

func ReadIntegers(r *nino.Reader) *Integers       { return nino.ReadSealed(r, &integersClass) }
func WriteIntegers(w *nino.Writer, v *Integers)   { nino.WriteSealed(w, v, &integersClass) }
func ReadFloats(r *nino.Reader) *Floats           { return nino.ReadSealed(r, &floatsClass) }
func WriteFloats(w *nino.Writer, v *Floats)       { nino.WriteSealed(w, v, &floatsClass) }
func ReadScalars(r *nino.Reader) *Scalars         { return nino.ReadSealed(r, &scalarsClass) }
func WriteScalars(w *nino.Writer, v *Scalars)     { nino.WriteSealed(w, v, &scalarsClass) }
func ReadDates(r *nino.Reader) *Dates             { return nino.ReadSealed(r, &datesClass) }
func WriteDates(w *nino.Writer, v *Dates)         { nino.WriteSealed(w, v, &datesClass) }
func ReadEnums(r *nino.Reader) *Enums             { return nino.ReadSealed(r, &enumsClass) }
func WriteEnums(w *nino.Writer, v *Enums)         { nino.WriteSealed(w, v, &enumsClass) }
func ReadBigValues(r *nino.Reader) *BigValues     { return nino.ReadSealed(r, &bigValuesClass) }
func WriteBigValues(w *nino.Writer, v *BigValues) { nino.WriteSealed(w, v, &bigValuesClass) }
func ReadStrings(r *nino.Reader) *Strings         { return nino.ReadSealed(r, &stringsClass) }
func WriteStrings(w *nino.Writer, v *Strings)     { nino.WriteSealed(w, v, &stringsClass) }
func ReadStringCollections(r *nino.Reader) *StringCollections {
	return nino.ReadSealed(r, &stringCollectionsClass)
}
func WriteStringCollections(w *nino.Writer, v *StringCollections) {
	nino.WriteSealed(w, v, &stringCollectionsClass)
}
func ReadBytes(r *nino.Reader) *Bytes                 { return nino.ReadSealed(r, &bytesClass) }
func WriteBytes(w *nino.Writer, v *Bytes)             { nino.WriteSealed(w, v, &bytesClass) }
func ReadPoint(r *nino.Reader) *Point                 { return nino.ReadSealed(r, &pointClass) }
func WritePoint(w *nino.Writer, v *Point)             { nino.WriteSealed(w, v, &pointClass) }
func ReadStructs(r *nino.Reader) *Structs             { return nino.ReadSealed(r, &structsClass) }
func WriteStructs(w *nino.Writer, v *Structs)         { nino.WriteSealed(w, v, &structsClass) }
func ReadOuter(r *nino.Reader) *Outer                 { return nino.ReadSealed(r, &outerClass) }
func WriteOuter(w *nino.Writer, v *Outer)             { nino.WriteSealed(w, v, &outerClass) }
func ReadMiddle(r *nino.Reader) *Middle               { return nino.ReadSealed(r, &middleClass) }
func WriteMiddle(w *nino.Writer, v *Middle)           { nino.WriteSealed(w, v, &middleClass) }
func ReadInner(r *nino.Reader) *Inner                 { return nino.ReadSealed(r, &innerClass) }
func WriteInner(w *nino.Writer, v *Inner)             { nino.WriteSealed(w, v, &innerClass) }
func ReadCollections(r *nino.Reader) *Collections     { return nino.ReadSealed(r, &collectionsClass) }
func WriteCollections(w *nino.Writer, v *Collections) { nino.WriteSealed(w, v, &collectionsClass) }
func ReadNullables(r *nino.Reader) *Nullables         { return nino.ReadSealed(r, &nullablesClass) }
func WriteNullables(w *nino.Writer, v *Nullables)     { nino.WriteSealed(w, v, &nullablesClass) }
func ReadEmpty(r *nino.Reader) *Empty                 { return nino.ReadSealed(r, &emptyClass) }
func WriteEmpty(w *nino.Writer, v *Empty)             { nino.WriteSealed(w, v, &emptyClass) }
func ReadEmpties(r *nino.Reader) *Empties             { return nino.ReadSealed(r, &emptiesClass) }
func WriteEmpties(w *nino.Writer, v *Empties)         { nino.WriteSealed(w, v, &emptiesClass) }

func readIntegersBody(r *nino.Reader, v *Integers) {
	b := r.Take(62)
	if len(b) < 62 {
		return
	}
	v.I8 = int8(b[0])
	v.U8 = b[1]
	v.I16 = int16(le.Uint16(b[2:]))
	v.U16 = le.Uint16(b[4:])
	v.I32 = int32(le.Uint32(b[6:]))
	v.U32 = le.Uint32(b[10:])
	v.I64 = int64(le.Uint64(b[14:]))
	v.U64 = le.Uint64(b[22:])
	v.I128 = nino.Int128{Lo: le.Uint64(b[30:]), Hi: le.Uint64(b[38:])}
	v.U128 = nino.UInt128{Lo: le.Uint64(b[46:]), Hi: le.Uint64(b[54:])}
}

func writeIntegersBody(w *nino.Writer, v *Integers) {
	b := w.Extend(62)
	if len(b) < 62 {
		return
	}
	b[0] = byte(v.I8)
	b[1] = v.U8
	le.PutUint16(b[2:], uint16(v.I16))
	le.PutUint16(b[4:], v.U16)
	le.PutUint32(b[6:], uint32(v.I32))
	le.PutUint32(b[10:], v.U32)
	le.PutUint64(b[14:], uint64(v.I64))
	le.PutUint64(b[22:], v.U64)
	le.PutUint64(b[30:], v.I128.Lo)
	le.PutUint64(b[38:], v.I128.Hi)
	le.PutUint64(b[46:], v.U128.Lo)
	le.PutUint64(b[54:], v.U128.Hi)
}

func readFloatsBody(r *nino.Reader, v *Floats) {
	b := r.Take(14)
	if len(b) < 14 {
		return
	}
	v.F16 = nino.Half(le.Uint16(b))
	v.F32 = math.Float32frombits(le.Uint32(b[2:]))
	v.F64 = math.Float64frombits(le.Uint64(b[6:]))
}

func writeFloatsBody(w *nino.Writer, v *Floats) {
	b := w.Extend(14)
	if len(b) < 14 {
		return
	}
	le.PutUint16(b, uint16(v.F16))
	le.PutUint32(b[2:], math.Float32bits(v.F32))
	le.PutUint64(b[6:], math.Float64bits(v.F64))
}

// Values with invalid bit patterns (bool, decimal, DateOnly, TimeOnly, DateTime) are read one by one, each
// validated.
func readScalarsBody(r *nino.Reader, v *Scalars) {
	v.Flag = r.Bool()
	v.Char = r.Char()
	v.Decimal = r.Decimal()
	v.Guid = r.Guid()
	v.Span = r.TimeSpan()
	v.Date = r.DateOnly()
	v.Time = r.TimeOnly()
}

func writeScalarsBody(w *nino.Writer, v *Scalars) {
	w.Bool(v.Flag)
	w.Char(v.Char)
	w.Decimal(v.Decimal)
	w.Guid(v.Guid)
	w.TimeSpan(v.Span)
	w.DateOnly(v.Date)
	w.TimeOnly(v.Time)
}

func readDatesBody(r *nino.Reader, v *Dates) {
	v.Utc = r.DateTime()
	v.Local = r.DateTime()
	v.Unspecified = r.DateTime()
	v.Offset = r.DateTimeOffset()
}

func writeDatesBody(w *nino.Writer, v *Dates) {
	w.DateTime(v.Utc)
	w.DateTime(v.Local)
	w.DateTime(v.Unspecified)
	w.DateTimeOffset(v.Offset)
}

func readEnumsBody(r *nino.Reader, v *Enums) {
	if b := r.Take(39); len(b) == 39 {
		v.A = E8(b[0])
		v.B = EU8(b[1])
		v.C = E16(le.Uint16(b[2:]))
		v.D = EU16(le.Uint16(b[4:]))
		v.E = E32(le.Uint32(b[6:]))
		v.F = EU32(le.Uint32(b[10:]))
		v.G = E64(le.Uint64(b[14:]))
		v.H = EU64(le.Uint64(b[22:]))
		v.Flags = Permissions(b[30])
		v.Wide = WideFlags(le.Uint64(b[31:]))
	}
	v.Array = nino.ReadPrimitives[E32](r)
}

func writeEnumsBody(w *nino.Writer, v *Enums) {
	if b := w.Extend(39); len(b) == 39 {
		b[0] = byte(v.A)
		b[1] = byte(v.B)
		le.PutUint16(b[2:], uint16(v.C))
		le.PutUint16(b[4:], uint16(v.D))
		le.PutUint32(b[6:], uint32(v.E))
		le.PutUint32(b[10:], uint32(v.F))
		le.PutUint64(b[14:], uint64(v.G))
		le.PutUint64(b[22:], uint64(v.H))
		b[30] = byte(v.Flags)
		le.PutUint64(b[31:], uint64(v.Wide))
	}
	nino.WritePrimitives(w, v.Array)
}

func readBigValuesBody(r *nino.Reader, v *BigValues) {
	v.Big = r.BigInteger()
	v.Version = r.Version()
}

func writeBigValuesBody(w *nino.Writer, v *BigValues) {
	w.BigInteger(v.Big)
	w.Version(v.Version)
}

func readStringsBody(r *nino.Reader, v *Strings) {
	v.Text = r.StringPtr()
	v.Utf8 = r.UTF8Ptr()
}

func writeStringsBody(w *nino.Writer, v *Strings) {
	w.StringPtr(v.Text)
	w.UTF8Ptr(v.Utf8)
}

func readStringCollectionsBody(r *nino.Reader, v *StringCollections) {
	v.Array = readStringPtrs(r)
	v.List = readStringPtrs(r)
	v.Utf8A = r.UTF8Ptr()
	v.Plain = r.StringPtr()
	v.Utf8B = r.UTF8Ptr()
}

func writeStringCollectionsBody(w *nino.Writer, v *StringCollections) {
	nino.WriteStringPtrs(w, v.Array)
	nino.WriteStringPtrs(w, v.List)
	w.UTF8Ptr(v.Utf8A)
	w.StringPtr(v.Plain)
	w.UTF8Ptr(v.Utf8B)
}

func readBytesBody(r *nino.Reader, v *Bytes) {
	v.Array = r.ByteArray()
	v.List = r.ByteArray()
	v.Signed = nino.ReadPrimitives[int8](r)
}

func writeBytesBody(w *nino.Writer, v *Bytes) {
	w.ByteArray(v.Array)
	w.ByteArray(v.List)
	nino.WritePrimitives(w, v.Signed)
}

// ---------------------------------------------------------------- objects and raw structs

func readPointBody(r *nino.Reader, v *Point) {
	b := r.Take(8)
	if len(b) < 8 {
		return
	}
	v.X = int32(le.Uint32(b))
	v.Y = int32(le.Uint32(b[4:]))
}

func writePointBody(w *nino.Writer, v *Point) {
	b := w.Extend(8)
	if len(b) < 8 {
		return
	}
	le.PutUint32(b, uint32(v.X))
	le.PutUint32(b[4:], uint32(v.Y))
}

// Raw structs: decode and encode their canonical layout from and into exactly their bytes; the writer's bytes are
// cleared first, so padding is written as zeros. Padding is not checked when read (.NET ignores it).
//
// Where a raw struct's Go memory is its bytes (these constants, false on a 32-bit host that aligns an int64 to 4
// bytes), arrays of it are copied as one block on a little-endian host: read always, written when it has no padding.
const (
	asIsVec2   = unsafe.Sizeof(Vec2{}) == 8 && unsafe.Offsetof(Vec2{}.X) == 0 && unsafe.Offsetof(Vec2{}.Y) == 4
	asIsPadded = unsafe.Sizeof(Padded{}) == 16 && unsafe.Offsetof(Padded{}.A) == 0 && unsafe.Offsetof(Padded{}.B) == 8
	asIsMixed  = unsafe.Sizeof(Mixed{}) == 12 && unsafe.Offsetof(Mixed{}.A) == 0 && unsafe.Offsetof(Mixed{}.B) == 2 &&
		unsafe.Offsetof(Mixed{}.C) == 4 && unsafe.Offsetof(Mixed{}.D) == 8
)

func decodeVec2(_ *nino.Reader, b []byte) Vec2 {
	_ = b[7]
	return Vec2{math.Float32frombits(le.Uint32(b)), math.Float32frombits(le.Uint32(b[4:]))}
}

func encodeVec2(b []byte, v Vec2) {
	_ = b[7]
	le.PutUint32(b, math.Float32bits(v.X))
	le.PutUint32(b[4:], math.Float32bits(v.Y))
}

func decodePadded(_ *nino.Reader, b []byte) Padded {
	_ = b[15]
	return Padded{A: b[0], B: int64(le.Uint64(b[8:]))}
}

func encodePadded(b []byte, v Padded) {
	_ = b[15]
	b[0] = v.A
	le.PutUint64(b[8:], uint64(v.B))
}

func decodeMixed(_ *nino.Reader, b []byte) Mixed {
	_ = b[11]
	return Mixed{A: b[0], B: int16(le.Uint16(b[2:])), C: int32(le.Uint32(b[4:])), D: b[8]}
}

func encodeMixed(b []byte, v Mixed) {
	_ = b[11]
	b[0] = v.A
	le.PutUint16(b[2:], uint16(v.B))
	le.PutUint32(b[4:], uint32(v.C))
	b[8] = v.D
}

func decodePacked(_ *nino.Reader, b []byte) Packed {
	_ = b[10]
	return Packed{A: b[0], B: int64(le.Uint64(b[1:])), C: int16(le.Uint16(b[9:]))}
}

func encodePacked(b []byte, v Packed) {
	_ = b[10]
	b[0] = v.A
	le.PutUint64(b[1:], uint64(v.B))
	le.PutUint16(b[9:], uint16(v.C))
}

func decodeSized(_ *nino.Reader, b []byte) Sized {
	_ = b[15]
	return Sized{A: int32(le.Uint32(b))}
}

func encodeSized(b []byte, v Sized) {
	_ = b[15]
	le.PutUint32(b, uint32(v.A))
}

func decodeNested(r *nino.Reader, b []byte) Nested {
	_ = b[31]
	return Nested{
		Inner:    decodePadded(r, b[:16]),
		Position: decodeVec2(r, b[16:24]),
		Kind:     EU8(b[24]),
		// A bool inside a raw value is checked like any other (.NET copies it as it is).
		Flag: r.BoolAt(b[25]),
	}
}

func encodeNested(b []byte, v Nested) {
	_ = b[31]
	encodePadded(b[:16], v.Inner)
	encodeVec2(b[16:24], v.Position)
	b[24] = byte(v.Kind)
	if v.Flag {
		b[25] = 1
	}
}

func readLabel(r *nino.Reader) (v Label) {
	v.Name = r.StringPtr()
	v.Weight = r.I32()
	return v
}

func writeLabel(w *nino.Writer, v Label) {
	w.StringPtr(v.Name)
	w.I32(v.Weight)
}

func readStructsBody(r *nino.Reader, v *Structs) {
	v.Vec = readVec2(r)
	v.Padded = readPadded(r)
	v.Mixed = readMixed(r)
	v.Packed = readPacked(r)
	v.Sized = readSized(r)
	v.Nested = readNested(r)
	v.Label = readLabel(r)
	v.VecArray = nino.ReadRaw(r, 8, asIsVec2, nil, decodeVec2)
	v.PaddedList = nino.ReadRaw(r, 16, asIsPadded, nil, decodePadded)
	v.MixedArray = nino.ReadRaw(r, 12, asIsMixed, nil, decodeMixed)
	v.Labels = readLabels(r)
	v.RawTuple.Item1 = r.U8()
	v.RawTuple.Item2 = r.I64()
	v.Tuple.Item1 = r.I32()
	v.Tuple.Item2 = r.StringPtr()
	v.Pair.Key = r.StringPtr()
	v.Pair.Value = readVec2(r)
}

func writeStructsBody(w *nino.Writer, v *Structs) {
	writeVec2(w, v.Vec)
	writePadded(w, v.Padded)
	writeMixed(w, v.Mixed)
	writePacked(w, v.Packed)
	writeSized(w, v.Sized)
	writeNested(w, v.Nested)
	writeLabel(w, v.Label)
	nino.WriteRaw(w, v.VecArray, 8, asIsVec2, nil, nil, encodeVec2)
	writePaddeds(w, v.PaddedList)
	writeMixeds(w, v.MixedArray)
	writeLabels(w, v.Labels)
	w.U8(v.RawTuple.Item1)
	w.I64(v.RawTuple.Item2)
	w.I32(v.Tuple.Item1)
	w.StringPtr(v.Tuple.Item2)
	w.StringPtr(v.Pair.Key)
	writeVec2(w, v.Pair.Value)
}

func readOuterBody(r *nino.Reader, v *Outer) {
	v.Name = r.StringPtr()
	v.Point = ReadPoint(r)
	v.Middle = ReadMiddle(r)
}

func writeOuterBody(w *nino.Writer, v *Outer) {
	w.StringPtr(v.Name)
	WritePoint(w, v.Point)
	WriteMiddle(w, v.Middle)
}

func readMiddleBody(r *nino.Reader, v *Middle) {
	v.First = ReadPoint(r)
	v.Second = ReadPoint(r)
	v.Inner = ReadInner(r)
}

func writeMiddleBody(w *nino.Writer, v *Middle) {
	WritePoint(w, v.First)
	WritePoint(w, v.Second)
	WriteInner(w, v.Inner)
}

func readInnerBody(r *nino.Reader, v *Inner) {
	v.Value = r.I64()
	v.Text = r.StringPtr()
	v.Point = ReadPoint(r)
}

func writeInnerBody(w *nino.Writer, v *Inner) {
	w.I64(v.Value)
	w.StringPtr(v.Text)
	WritePoint(w, v.Point)
}

// ---------------------------------------------------------------- collections

// keyString reads a string dictionary key: null is invalid (.NET's dictionaries reject a null key).
func keyString(r *nino.Reader) string {
	s, ok := r.NullableString()
	if !ok && !r.Failed() {
		r.Fail(nino.CodeInvalidData, "null dictionary key")
	}
	return s
}

// readStringSet reads a HashSet<string> in the order of the data: a string (or null) read twice fails.
func readStringSet(r *nino.Reader) []*string { return nino.CheckUniqueStrings(r, readStringPtrs(r)) }

func readStringList(r *nino.Reader) []*string { return readStringPtrs(r) }

func writeStringList(w *nino.Writer, v []*string) { nino.WriteStringPtrs(w, v) }

// readGrid reads a 2-dimensional array's lengths (nil for null) and makes its items, which the caller reads.
func readGrid[T any](r *nino.Reader, minSize int) *Grid[T] {
	s, ok := r.ReadShape(2, minSize)
	if !ok {
		return nil
	}
	// ReadShape checked the element count against the bytes left.
	return &Grid[T]{Lengths: [2]int{s.Dims[0], s.Dims[1]}, Items: make([]T, s.Total)}
}

// writeGrid writes a 2-dimensional array's lengths and reports whether its items follow (not null, lengths that
// match them).
func writeGrid[T any](w *nino.Writer, g *Grid[T]) bool {
	if g == nil {
		w.NullLength()
		return false
	}
	if g.Lengths[0] < 0 || g.Lengths[1] < 0 || len(g.Items) != g.Lengths[0]*g.Lengths[1] {
		w.Fail(nino.CodeInvalidValue, "grid lengths do not match its items")
		return false
	}
	w.WriteShape(g.Lengths[0], g.Lengths[1])
	return true
}

func readIntGrid(r *nino.Reader) *Grid[int32] {
	g := readGrid[int32](r, 4)
	if g == nil {
		return nil
	}
	for i := range g.Items {
		g.Items[i] = r.I32()
		if r.Failed() {
			return nil
		}
	}
	return g
}

func writeIntGrid(w *nino.Writer, g *Grid[int32]) {
	if writeGrid(w, g) {
		for _, e := range g.Items {
			w.I32(e)
		}
	}
}

func readStringGrid(r *nino.Reader) *Grid[*string] {
	g := readGrid[*string](r, 1)
	if g == nil {
		return nil
	}
	for i := range g.Items {
		g.Items[i] = r.StringPtr()
		if r.Failed() {
			return nil
		}
	}
	return g
}

func writeStringGrid(w *nino.Writer, g *Grid[*string]) {
	if writeGrid(w, g) {
		for _, e := range g.Items {
			w.StringPtr(e)
		}
	}
}

func readCollectionsBody(r *nino.Reader, v *Collections) {
	v.Ints = nino.ReadPrimitives[int32](r)
	v.IntList = nino.ReadPrimitives[int32](r)
	v.IntSet = nino.CheckUniqueValues(r, nino.ReadPrimitives[int32](r))
	v.Longs = nino.ReadPrimitives[int64](r)
	v.Doubles = nino.ReadPrimitives[float64](r)
	v.Bools = nino.ReadBools(r)
	v.Chars = nino.ReadPrimitives[uint16](r)
	v.Strings = readStringList(r)
	v.StringSet = readStringSet(r)
	v.Points = readPoints(r)
	v.PointList = readPoints(r)
	v.IntMap = nino.ReadPrimitiveEntries[int32, int32](r)
	v.StringMap = readStringStringPtrEntries(r)
	v.PointMap = readStringPointEntries(r)
	v.ListMap = readI32StringListEntries(r)
	v.SortedInts = readI32StringPtrEntries(r)
	v.SortedStrings = readStringI32Entries(r)
	v.Nested = readInt32Lists(r)
	v.Jagged = readInt32Lists(r)
	v.Grid = readIntGrid(r)
	v.StringGrid = readStringGrid(r)
}

func writeCollectionsBody(w *nino.Writer, v *Collections) {
	nino.WritePrimitives(w, v.Ints)
	nino.WritePrimitives(w, v.IntList)
	nino.WritePrimitives(w, v.IntSet)
	nino.WritePrimitives(w, v.Longs)
	nino.WritePrimitives(w, v.Doubles)
	nino.WriteBools(w, v.Bools)
	nino.WritePrimitives(w, v.Chars)
	writeStringList(w, v.Strings)
	writeStringList(w, v.StringSet)
	writePoints(w, v.Points)
	writePoints(w, v.PointList)
	nino.WritePrimitiveEntries(w, v.IntMap)
	writeStringStringPtrEntries(w, v.StringMap)
	writeStringPointEntries(w, v.PointMap)
	writeI32StringListEntries(w, v.ListMap)
	writeI32StringPtrEntries(w, v.SortedInts)
	writeStringI32Entries(w, v.SortedStrings)
	writeInt32Lists(w, v.Nested)
	writeInt32Lists(w, v.Jagged)
	writeIntGrid(w, v.Grid)
	writeStringGrid(w, v.StringGrid)
}

// ---------------------------------------------------------------- nullables

// Nullable<T> members: a presence byte, then T.

func readNullableInt32(r *nino.Reader) *int32 {
	if !r.Present() {
		return nil
	}
	x := r.I32()
	return &x
}

func writeNullableInt32(w *nino.Writer, v *int32) {
	w.Present(v != nil)
	if v != nil {
		w.I32(*v)
	}
}

// Arrays and lists of a raw T? use the fixed stride (spec §6.2): the flag, padding up to T's alignment, then T
// (zeros when null).

func decodeStrideInt32(r *nino.Reader, b []byte) *int32 {
	_ = b[7]
	if !r.BoolAt(b[0]) {
		return nil
	}
	x := int32(le.Uint32(b[4:]))
	return &x
}

func encodeStrideInt32(b []byte, v *int32) {
	_ = b[7]
	if v != nil {
		b[0] = 1
		le.PutUint32(b[4:], uint32(*v))
	}
}

func decodeStrideInt64(r *nino.Reader, b []byte) *int64 {
	_ = b[15]
	if !r.BoolAt(b[0]) {
		return nil
	}
	x := int64(le.Uint64(b[8:]))
	return &x
}

func encodeStrideInt64(b []byte, v *int64) {
	_ = b[15]
	if v != nil {
		b[0] = 1
		le.PutUint64(b[8:], uint64(*v))
	}
}

func decodeStridePadded(r *nino.Reader, b []byte) *Padded {
	_ = b[23]
	if !r.BoolAt(b[0]) {
		return nil
	}
	x := decodePadded(r, b[8:24])
	return &x
}

func encodeStridePadded(b []byte, v *Padded) {
	_ = b[23]
	if v != nil {
		b[0] = 1
		encodePadded(b[8:24], *v)
	}
}

func readNullablesBody(r *nino.Reader, v *Nullables) {
	v.Int = readNullableInt32(r)
	if r.Present() {
		x := r.I64()
		v.Long = &x
	}
	if r.Present() {
		x := r.F64()
		v.Double = &x
	}
	if r.Present() {
		x := r.Bool()
		v.Bool = &x
	}
	if r.Present() {
		x := r.Guid()
		v.Guid = &x
	}
	if r.Present() {
		x := E16(r.I16())
		v.Enum = &x
	}
	if r.Present() {
		x := readVec2(r)
		v.Vec = &x
	}
	if r.Present() {
		x := readPadded(r)
		v.Padded = &x
	}
	if r.Present() {
		x := readLabel(r)
		v.Label = &x
	}
	v.IntArray = readStrideInt32s(r)
	v.LongList = readStrideInt64s(r)
	v.PaddedArray = readStridePaddeds(r)
	v.Map = readI32NullableInt32Entries(r)
}

func writeNullablesBody(w *nino.Writer, v *Nullables) {
	writeNullableInt32(w, v.Int)
	if w.Present(v.Long != nil); v.Long != nil {
		w.I64(*v.Long)
	}
	if w.Present(v.Double != nil); v.Double != nil {
		w.F64(*v.Double)
	}
	if w.Present(v.Bool != nil); v.Bool != nil {
		w.Bool(*v.Bool)
	}
	if w.Present(v.Guid != nil); v.Guid != nil {
		w.Guid(*v.Guid)
	}
	if w.Present(v.Enum != nil); v.Enum != nil {
		w.I16(int16(*v.Enum))
	}
	if w.Present(v.Vec != nil); v.Vec != nil {
		writeVec2(w, *v.Vec)
	}
	if w.Present(v.Padded != nil); v.Padded != nil {
		writePadded(w, *v.Padded)
	}
	if w.Present(v.Label != nil); v.Label != nil {
		writeLabel(w, *v.Label)
	}
	writeStrideInt32s(w, v.IntArray)
	writeStrideInt64s(w, v.LongList)
	writeStridePaddeds(w, v.PaddedArray)
	writeI32NullableInt32Entries(w, v.Map)
}

// ---------------------------------------------------------------- empties

// An empty struct is one byte (.NET gives it size 1); its value is not checked.
func readEmptyStruct(r *nino.Reader) EmptyStruct {
	r.Skip(1)
	return EmptyStruct{}
}

func writeEmptyStruct(w *nino.Writer, _ EmptyStruct) { w.U8(0) }

func readEmptiesBody(r *nino.Reader, v *Empties) {
	v.Class = ReadEmpty(r)
	v.Struct = readEmptyStruct(r)
	v.Structs = readEmptyStructs(r)
	v.Classes = readEmpties(r)
}

func writeEmptiesBody(w *nino.Writer, v *Empties) {
	WriteEmpty(w, v.Class)
	writeEmptyStruct(w, v.Struct)
	writeEmptyStructs(w, v.Structs)
	writeEmpties(w, v.Classes)
}

// Structs at the top level: no header.

func ReadVec2(r *nino.Reader) Vec2                   { return readVec2(r) }
func WriteVec2(w *nino.Writer, v Vec2)               { writeVec2(w, v) }
func ReadPadded(r *nino.Reader) Padded               { return readPadded(r) }
func WritePadded(w *nino.Writer, v Padded)           { writePadded(w, v) }
func ReadNested(r *nino.Reader) Nested               { return readNested(r) }
func WriteNested(w *nino.Writer, v Nested)           { writeNested(w, v) }
func ReadLabel(r *nino.Reader) Label                 { return readLabel(r) }
func WriteLabel(w *nino.Writer, v Label)             { writeLabel(w, v) }
func ReadEmptyStruct(r *nino.Reader) EmptyStruct     { return readEmptyStruct(r) }
func WriteEmptyStruct(w *nino.Writer, v EmptyStruct) { writeEmptyStruct(w, v) }

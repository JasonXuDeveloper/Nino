package sample

import (
	"encoding/binary"
	"math"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go"
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
	v.Utf8 = r.Utf8Ptr()
}

func writeStringsBody(w *nino.Writer, v *Strings) {
	w.StringPtr(v.Text)
	w.Utf8Ptr(v.Utf8)
}

func readStringCollectionsBody(r *nino.Reader, v *StringCollections) {
	v.Array = nino.ReadSlice(r, 4, (*nino.Reader).StringPtr)
	v.List = nino.ReadSlice(r, 4, (*nino.Reader).StringPtr)
	v.Utf8A = r.Utf8Ptr()
	v.Plain = r.StringPtr()
	v.Utf8B = r.Utf8Ptr()
}

func writeStringCollectionsBody(w *nino.Writer, v *StringCollections) {
	nino.WriteSlice(w, v.Array, (*nino.Writer).StringPtr)
	nino.WriteSlice(w, v.List, (*nino.Writer).StringPtr)
	w.Utf8Ptr(v.Utf8A)
	w.StringPtr(v.Plain)
	w.Utf8Ptr(v.Utf8B)
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

// readRaw reads one raw value of size bytes.
func readRaw[T any](r *nino.Reader, size int, decode func(*nino.Reader, []byte) T) (v T) {
	if b := r.Take(size); len(b) == size {
		v = decode(r, b)
	}
	return v
}

// writeRaw writes one raw value of size bytes.
func writeRaw[T any](w *nino.Writer, v T, size int, encode func([]byte, T)) {
	if b := w.ExtendZero(size); len(b) == size {
		encode(b, v)
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
	v.Vec = readRaw(r, 8, decodeVec2)
	v.Padded = readRaw(r, 16, decodePadded)
	v.Mixed = readRaw(r, 12, decodeMixed)
	v.Packed = readRaw(r, 11, decodePacked)
	v.Sized = readRaw(r, 16, decodeSized)
	v.Nested = readRaw(r, 32, decodeNested)
	v.Label = readLabel(r)
	v.VecArray = nino.ReadFixed(r, 8, decodeVec2)
	v.PaddedList = nino.ReadFixed(r, 16, decodePadded)
	v.MixedArray = nino.ReadFixed(r, 12, decodeMixed)
	v.Labels = nino.ReadSlice(r, 8, readLabel)
	v.RawTuple.Item1 = r.U8()
	v.RawTuple.Item2 = r.I64()
	v.Tuple.Item1 = r.I32()
	v.Tuple.Item2 = r.StringPtr()
	v.Pair.Key = r.StringPtr()
	v.Pair.Value = readRaw(r, 8, decodeVec2)
}

func writeStructsBody(w *nino.Writer, v *Structs) {
	writeRaw(w, v.Vec, 8, encodeVec2)
	writeRaw(w, v.Padded, 16, encodePadded)
	writeRaw(w, v.Mixed, 12, encodeMixed)
	writeRaw(w, v.Packed, 11, encodePacked)
	writeRaw(w, v.Sized, 16, encodeSized)
	writeRaw(w, v.Nested, 32, encodeNested)
	writeLabel(w, v.Label)
	nino.WriteFixed(w, v.VecArray, 8, encodeVec2)
	nino.WriteFixed(w, v.PaddedList, 16, encodePadded)
	nino.WriteFixed(w, v.MixedArray, 12, encodeMixed)
	nino.WriteSlice(w, v.Labels, writeLabel)
	w.U8(v.RawTuple.Item1)
	w.I64(v.RawTuple.Item2)
	w.I32(v.Tuple.Item1)
	w.StringPtr(v.Tuple.Item2)
	w.StringPtr(v.Pair.Key)
	writeRaw(w, v.Pair.Value, 8, encodeVec2)
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
func readStringSet(r *nino.Reader) []*string {
	v := nino.ReadSlice(r, 4, (*nino.Reader).StringPtr)
	if v == nil {
		return nil
	}
	seen := make(map[string]struct{}, len(v))
	null := false
	for _, s := range v {
		dup := false
		if s == nil {
			dup, null = null, true
		} else if _, dup = seen[*s]; !dup {
			seen[*s] = struct{}{}
		}
		if dup {
			r.Fail(nino.CodeDuplicateKey, "")
			return nil
		}
	}
	return v
}

func readStringList(r *nino.Reader) []*string { return nino.ReadSlice(r, 4, (*nino.Reader).StringPtr) }

func writeStringList(w *nino.Writer, v []*string) { nino.WriteSlice(w, v, (*nino.Writer).StringPtr) }

func readGrid[T any](r *nino.Reader, minSize int, read func(*nino.Reader) T) *Grid[T] {
	s, ok := r.ReadShape(2, minSize)
	if !ok {
		return nil
	}
	// ReadShape checked the element count against the bytes left.
	g := &Grid[T]{Lengths: [2]int{s.Dims[0], s.Dims[1]}, Items: make([]T, s.Total)}
	for i := range g.Items {
		g.Items[i] = read(r)
		if r.Failed() {
			return nil
		}
	}
	return g
}

func writeGrid[T any](w *nino.Writer, g *Grid[T], write func(*nino.Writer, T)) {
	if g == nil {
		w.Null()
		return
	}
	if g.Lengths[0] < 0 || g.Lengths[1] < 0 || len(g.Items) != g.Lengths[0]*g.Lengths[1] {
		w.Fail(nino.CodeInvalidValue, "grid lengths do not match its items")
		return
	}
	w.WriteShape(g.Lengths[0], g.Lengths[1])
	for _, e := range g.Items {
		write(w, e)
	}
}

func readCollectionsBody(r *nino.Reader, v *Collections) {
	v.Ints = nino.ReadPrimitives[int32](r)
	v.IntList = nino.ReadPrimitives[int32](r)
	v.IntSet = nino.ReadUnique(r, 4, (*nino.Reader).I32)
	v.Longs = nino.ReadPrimitives[int64](r)
	v.Doubles = nino.ReadPrimitives[float64](r)
	v.Bools = nino.ReadBools(r)
	v.Chars = nino.ReadPrimitives[uint16](r)
	v.Strings = readStringList(r)
	v.StringSet = readStringSet(r)
	v.Points = nino.ReadSlice(r, 1, ReadPoint)
	v.PointList = nino.ReadSlice(r, 1, ReadPoint)
	v.IntMap = nino.ReadUniqueEntries(r, 8, (*nino.Reader).I32, (*nino.Reader).I32)
	v.StringMap = nino.ReadUniqueEntries(r, 8, keyString, (*nino.Reader).StringPtr)
	v.PointMap = nino.ReadUniqueEntries(r, 5, keyString, ReadPoint)
	v.ListMap = nino.ReadUniqueEntries(r, 8, (*nino.Reader).I32, readStringList)
	v.SortedInts = nino.ReadUniqueEntries(r, 8, (*nino.Reader).I32, (*nino.Reader).StringPtr)
	v.SortedStrings = nino.ReadUniqueEntries(r, 8, keyString, (*nino.Reader).I32)
	v.Nested = nino.ReadSlice(r, 4, nino.ReadPrimitives[int32])
	v.Jagged = nino.ReadSlice(r, 4, nino.ReadPrimitives[int32])
	v.Grid = readGrid(r, 4, (*nino.Reader).I32)
	v.StringGrid = readGrid(r, 4, (*nino.Reader).StringPtr)
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
	nino.WriteSlice(w, v.Points, WritePoint)
	nino.WriteSlice(w, v.PointList, WritePoint)
	nino.WriteEntries(w, v.IntMap, (*nino.Writer).I32, (*nino.Writer).I32)
	nino.WriteEntries(w, v.StringMap, (*nino.Writer).String, (*nino.Writer).StringPtr)
	nino.WriteEntries(w, v.PointMap, (*nino.Writer).String, WritePoint)
	nino.WriteEntries(w, v.ListMap, (*nino.Writer).I32, writeStringList)
	nino.WriteEntries(w, v.SortedInts, (*nino.Writer).I32, (*nino.Writer).StringPtr)
	nino.WriteEntries(w, v.SortedStrings, (*nino.Writer).String, (*nino.Writer).I32)
	nino.WriteSlice(w, v.Nested, nino.WritePrimitives[int32])
	nino.WriteSlice(w, v.Jagged, nino.WritePrimitives[int32])
	writeGrid(w, v.Grid, (*nino.Writer).I32)
	writeGrid(w, v.StringGrid, (*nino.Writer).StringPtr)
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
		x := readRaw(r, 8, decodeVec2)
		v.Vec = &x
	}
	if r.Present() {
		x := readRaw(r, 16, decodePadded)
		v.Padded = &x
	}
	if r.Present() {
		x := readLabel(r)
		v.Label = &x
	}
	v.IntArray = nino.ReadFixed(r, 8, decodeStrideInt32)
	v.LongList = nino.ReadFixed(r, 16, decodeStrideInt64)
	v.PaddedArray = nino.ReadFixed(r, 24, decodeStridePadded)
	v.Map = nino.ReadUniqueEntries(r, 5, (*nino.Reader).I32, readNullableInt32)
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
		writeRaw(w, *v.Vec, 8, encodeVec2)
	}
	if w.Present(v.Padded != nil); v.Padded != nil {
		writeRaw(w, *v.Padded, 16, encodePadded)
	}
	if w.Present(v.Label != nil); v.Label != nil {
		writeLabel(w, *v.Label)
	}
	nino.WriteFixed(w, v.IntArray, 8, encodeStrideInt32)
	nino.WriteFixed(w, v.LongList, 16, encodeStrideInt64)
	nino.WriteFixed(w, v.PaddedArray, 24, encodeStridePadded)
	nino.WriteEntries(w, v.Map, (*nino.Writer).I32, writeNullableInt32)
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
	v.Structs = nino.ReadSlice(r, 1, readEmptyStruct)
	v.Classes = nino.ReadSlice(r, 1, ReadEmpty)
}

func writeEmptiesBody(w *nino.Writer, v *Empties) {
	WriteEmpty(w, v.Class)
	writeEmptyStruct(w, v.Struct)
	nino.WriteSlice(w, v.Structs, writeEmptyStruct)
	nino.WriteSlice(w, v.Classes, WriteEmpty)
}

// Structs at the top level: no header.

func ReadVec2(r *nino.Reader) Vec2                   { return readRaw(r, 8, decodeVec2) }
func WriteVec2(w *nino.Writer, v Vec2)               { writeRaw(w, v, 8, encodeVec2) }
func ReadPadded(r *nino.Reader) Padded               { return readRaw(r, 16, decodePadded) }
func WritePadded(w *nino.Writer, v Padded)           { writeRaw(w, v, 16, encodePadded) }
func ReadNested(r *nino.Reader) Nested               { return readRaw(r, 32, decodeNested) }
func WriteNested(w *nino.Writer, v Nested)           { writeRaw(w, v, 32, encodeNested) }
func ReadLabel(r *nino.Reader) Label                 { return readLabel(r) }
func WriteLabel(w *nino.Writer, v Label)             { writeLabel(w, v) }
func ReadEmptyStruct(r *nino.Reader) EmptyStruct     { return readEmptyStruct(r) }
func WriteEmptyStruct(w *nino.Writer, v EmptyStruct) { writeEmptyStruct(w, v) }

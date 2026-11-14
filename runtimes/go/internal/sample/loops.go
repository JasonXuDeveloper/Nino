package sample

import (
	"unsafe"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// The collection loops of the codecs, written out as nino-gen's Go template writes them (read_loop, write_loop):
// each element's code called directly, never through a function value (optimize-runtimes, "Dispatch outside
// .NET"); dictionaries checked once with nino.CheckUniqueKeys; fixed-size elements from one Take or ExtendZero.

func readAnimals(r *nino.Reader) []Animal {
	n := r.Length(4)
	if n < 0 {
		return nil
	}
	var e Animal
	v := make([]Animal, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = ReadAnimal(r)
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func readEmptyStructs(r *nino.Reader) []EmptyStruct {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	var e EmptyStruct
	v := make([]EmptyStruct, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = readEmptyStruct(r)
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func readEmpties(r *nino.Reader) []*Empty {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	var e *Empty
	v := make([]*Empty, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = ReadEmpty(r)
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func readI32I32Entries(r *nino.Reader) []nino.Entry[int32, int32] {
	n := r.Length(8)
	if n < 0 {
		return nil
	}
	var e nino.Entry[int32, int32]
	v := make([]nino.Entry[int32, int32], 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		k := r.I32()
		e = nino.Entry[int32, int32]{Key: k, Value: r.I32()}
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return nino.CheckUniqueKeys(r, v)
}

func readI32NullableInt32Entries(r *nino.Reader) []nino.Entry[int32, *int32] {
	return nino.CheckUniqueKeys(r, readI32NullableInt32Pairs(r))
}

// readI32NullableInt32Pairs reads a list of pairs: the entries of a dictionary without the check of its keys.
func readI32NullableInt32Pairs(r *nino.Reader) []nino.Entry[int32, *int32] {
	n := r.Length(5)
	if n < 0 {
		return nil
	}
	var e nino.Entry[int32, *int32]
	v := make([]nino.Entry[int32, *int32], 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		k := r.I32()
		e = nino.Entry[int32, *int32]{Key: k, Value: readNullableInt32(r)}
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func readI32NullableInt64Entries(r *nino.Reader) []nino.Entry[int32, *int64] {
	n := r.Length(5)
	if n < 0 {
		return nil
	}
	var e nino.Entry[int32, *int64]
	v := make([]nino.Entry[int32, *int64], 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		k := r.I32()
		e = nino.Entry[int32, *int64]{Key: k, Value: readNullableInt64(r)}
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return nino.CheckUniqueKeys(r, v)
}

func readI32StringListEntries(r *nino.Reader) []nino.Entry[int32, []*string] {
	n := r.Length(8)
	if n < 0 {
		return nil
	}
	var e nino.Entry[int32, []*string]
	v := make([]nino.Entry[int32, []*string], 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		k := r.I32()
		e = nino.Entry[int32, []*string]{Key: k, Value: readStringList(r)}
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return nino.CheckUniqueKeys(r, v)
}

func readI32StringPtrEntries(r *nino.Reader) []nino.Entry[int32, *string] {
	n := r.Length(5)
	if n < 0 {
		return nil
	}
	var e nino.Entry[int32, *string]
	v := make([]nino.Entry[int32, *string], 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		k := r.I32()
		e = nino.Entry[int32, *string]{Key: k, Value: r.StringPtr()}
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return nino.CheckUniqueKeys(r, v)
}

func readKeyedV1s(r *nino.Reader) []*KeyedV1 {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	var e *KeyedV1
	v := make([]*KeyedV1, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = ReadKeyedV1(r)
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func readLabels(r *nino.Reader) []Label {
	n := r.Length(5)
	if n < 0 {
		return nil
	}
	var e Label
	v := make([]Label, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = readLabel(r)
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func readPaddeds(r *nino.Reader) []Padded {
	n := r.Length(16)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 16)
	if len(b) != n*16 {
		return nil
	}
	var e Padded
	v := make([]Padded, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for len(b) >= 16 {
		e = decodePadded(r, b[:16:16])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[16:]
	}
	return v
}

func readPoints(r *nino.Reader) []*Point {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	var e *Point
	v := make([]*Point, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = ReadPoint(r)
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func readInt32Lists(r *nino.Reader) [][]int32 {
	n := r.Length(4)
	if n < 0 {
		return nil
	}
	var e []int32
	v := make([][]int32, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = nino.ReadPrimitives[int32](r)
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func readStrideInt32Lists(r *nino.Reader) [][]*int32 {
	n := r.Length(4)
	if n < 0 {
		return nil
	}
	var e []*int32
	v := make([][]*int32, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = readStrideInt32s(r)
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func readShapes(r *nino.Reader) []Shape {
	n := r.Length(4)
	if n < 0 {
		return nil
	}
	var e Shape
	v := make([]Shape, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = ReadShape(r)
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func readStrideInt32s(r *nino.Reader) []*int32 {
	n := r.Length(8)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 8)
	if len(b) != n*8 {
		return nil
	}
	var e *int32
	v := make([]*int32, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for len(b) >= 8 {
		e = decodeStrideInt32(r, b[:8:8])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[8:]
	}
	return v
}

func readStrideInt64s(r *nino.Reader) []*int64 {
	n := r.Length(16)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 16)
	if len(b) != n*16 {
		return nil
	}
	var e *int64
	v := make([]*int64, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for len(b) >= 16 {
		e = decodeStrideInt64(r, b[:16:16])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[16:]
	}
	return v
}

func readStridePaddeds(r *nino.Reader) []*Padded {
	n := r.Length(24)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 24)
	if len(b) != n*24 {
		return nil
	}
	var e *Padded
	v := make([]*Padded, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for len(b) >= 24 {
		e = decodeStridePadded(r, b[:24:24])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[24:]
	}
	return v
}

func readStringAnimalEntries(r *nino.Reader) []nino.Entry[string, Animal] {
	n := r.Length(5)
	if n < 0 {
		return nil
	}
	var e nino.Entry[string, Animal]
	v := make([]nino.Entry[string, Animal], 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		k := keyString(r)
		e = nino.Entry[string, Animal]{Key: k, Value: ReadAnimal(r)}
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return nino.CheckUniqueKeys(r, v)
}

func readStringDictTreeEntries(r *nino.Reader) []nino.Entry[string, DictTree] {
	n := r.Length(5)
	if n < 0 {
		return nil
	}
	var e nino.Entry[string, DictTree]
	v := make([]nino.Entry[string, DictTree], 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		k := keyString(r)
		e = nino.Entry[string, DictTree]{Key: k, Value: readDictTree(r)}
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return nino.CheckUniqueKeys(r, v)
}

func readStringI32Entries(r *nino.Reader) []nino.Entry[string, int32] {
	n := r.Length(5)
	if n < 0 {
		return nil
	}
	var e nino.Entry[string, int32]
	v := make([]nino.Entry[string, int32], 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		k := keyString(r)
		e = nino.Entry[string, int32]{Key: k, Value: r.I32()}
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return nino.CheckUniqueKeys(r, v)
}

func readStringPointEntries(r *nino.Reader) []nino.Entry[string, *Point] {
	n := r.Length(2)
	if n < 0 {
		return nil
	}
	var e nino.Entry[string, *Point]
	v := make([]nino.Entry[string, *Point], 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		k := keyString(r)
		e = nino.Entry[string, *Point]{Key: k, Value: ReadPoint(r)}
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return nino.CheckUniqueKeys(r, v)
}

func readStringPtrs(r *nino.Reader) []*string {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	var e *string
	v := make([]*string, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = r.StringPtr()
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func readStringStringPtrEntries(r *nino.Reader) []nino.Entry[string, *string] {
	n := r.Length(2)
	if n < 0 {
		return nil
	}
	var e nino.Entry[string, *string]
	v := make([]nino.Entry[string, *string], 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		k := keyString(r)
		e = nino.Entry[string, *string]{Key: k, Value: r.StringPtr()}
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return nino.CheckUniqueKeys(r, v)
}

func readTreeNodes(r *nino.Reader) []*TreeNode {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	var e *TreeNode
	v := make([]*TreeNode, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = ReadTreeNode(r)
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func readTrees(r *nino.Reader) []Tree {
	n := r.Length(4)
	if n < 0 {
		return nil
	}
	var e Tree
	v := make([]Tree, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = readTree(r)
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func writeAnimals(w *nino.Writer, v []Animal) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		WriteAnimal(w, e)
	}
}

func writeEmptyStructs(w *nino.Writer, v []EmptyStruct) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		writeEmptyStruct(w, e)
	}
}

func writeEmpties(w *nino.Writer, v []*Empty) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		WriteEmpty(w, e)
	}
}

func writeI32I32Entries(w *nino.Writer, v []nino.Entry[int32, int32]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		w.I32(e.Key)
		w.I32(e.Value)
	}
}

func writeI32NullableInt32Entries(w *nino.Writer, v []nino.Entry[int32, *int32]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		w.I32(e.Key)
		writeNullableInt32(w, e.Value)
	}
}

func writeI32NullableInt64Entries(w *nino.Writer, v []nino.Entry[int32, *int64]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		w.I32(e.Key)
		writeNullableInt64(w, e.Value)
	}
}

func writeI32StringListEntries(w *nino.Writer, v []nino.Entry[int32, []*string]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		w.I32(e.Key)
		writeStringList(w, e.Value)
	}
}

func writeI32StringPtrEntries(w *nino.Writer, v []nino.Entry[int32, *string]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		w.I32(e.Key)
		w.StringPtr(e.Value)
	}
}

func writeKeyedV1s(w *nino.Writer, v []*KeyedV1) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		WriteKeyedV1(w, e)
	}
}

func writeLabels(w *nino.Writer, v []Label) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		writeLabel(w, e)
	}
}

func writeMixeds(w *nino.Writer, v []Mixed) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 12)
	if len(b) != len(v)*12 {
		return
	}
	for _, e := range v {
		encodeMixed(b[:12:12], e)
		b = b[12:]
	}
}

func writePaddeds(w *nino.Writer, v []Padded) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 16)
	if len(b) != len(v)*16 {
		return
	}
	for _, e := range v {
		encodePadded(b[:16:16], e)
		b = b[16:]
	}
}

func writePoints(w *nino.Writer, v []*Point) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		WritePoint(w, e)
	}
}

func writeInt32Lists(w *nino.Writer, v [][]int32) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		nino.WritePrimitives(w, e)
	}
}

func writeStrideInt32Lists(w *nino.Writer, v [][]*int32) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		writeStrideInt32s(w, e)
	}
}

func writeShapes(w *nino.Writer, v []Shape) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		WriteShape(w, e)
	}
}

func writeStrideInt32s(w *nino.Writer, v []*int32) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 8)
	if len(b) != len(v)*8 {
		return
	}
	for _, e := range v {
		encodeStrideInt32(b[:8:8], e)
		b = b[8:]
	}
}

func writeStrideInt64s(w *nino.Writer, v []*int64) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 16)
	if len(b) != len(v)*16 {
		return
	}
	for _, e := range v {
		encodeStrideInt64(b[:16:16], e)
		b = b[16:]
	}
}

func writeStridePaddeds(w *nino.Writer, v []*Padded) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 24)
	if len(b) != len(v)*24 {
		return
	}
	for _, e := range v {
		encodeStridePadded(b[:24:24], e)
		b = b[24:]
	}
}

func writeStringAnimalEntries(w *nino.Writer, v []nino.Entry[string, Animal]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		w.String(e.Key)
		WriteAnimal(w, e.Value)
	}
}

func writeStringDictTreeEntries(w *nino.Writer, v []nino.Entry[string, DictTree]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		w.String(e.Key)
		writeDictTree(w, e.Value)
	}
}

func writeStringI32Entries(w *nino.Writer, v []nino.Entry[string, int32]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		w.String(e.Key)
		w.I32(e.Value)
	}
}

func writeStringPointEntries(w *nino.Writer, v []nino.Entry[string, *Point]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		w.String(e.Key)
		WritePoint(w, e.Value)
	}
}

func writeStringStringPtrEntries(w *nino.Writer, v []nino.Entry[string, *string]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		w.String(e.Key)
		w.StringPtr(e.Value)
	}
}

func writeTreeNodes(w *nino.Writer, v []*TreeNode) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		WriteTreeNode(w, e)
	}
}

func writeTrees(w *nino.Writer, v []Tree) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		writeTree(w, e)
	}
}

// readMixed and writeMixed are one Mixed (raw, 12 bytes), from one Take or ExtendZero.
func readMixed(r *nino.Reader) (v Mixed) {
	if b := r.Take(12); len(b) == 12 {
		v = decodeMixed(r, b)
	}
	return v
}

func writeMixed(w *nino.Writer, v Mixed) {
	if b := w.ExtendZero(12); len(b) == 12 {
		encodeMixed(b, v)
	}
}

// readNested and writeNested are one Nested (raw, 32 bytes), from one Take or ExtendZero.
func readNested(r *nino.Reader) (v Nested) {
	if b := r.Take(32); len(b) == 32 {
		v = decodeNested(r, b)
	}
	return v
}

func writeNested(w *nino.Writer, v Nested) {
	if b := w.ExtendZero(32); len(b) == 32 {
		encodeNested(b, v)
	}
}

// readPacked and writePacked are one Packed (raw, 11 bytes), from one Take or ExtendZero.
func readPacked(r *nino.Reader) (v Packed) {
	if b := r.Take(11); len(b) == 11 {
		v = decodePacked(r, b)
	}
	return v
}

func writePacked(w *nino.Writer, v Packed) {
	if b := w.ExtendZero(11); len(b) == 11 {
		encodePacked(b, v)
	}
}

// readPadded and writePadded are one Padded (raw, 16 bytes), from one Take or ExtendZero.
func readPadded(r *nino.Reader) (v Padded) {
	if b := r.Take(16); len(b) == 16 {
		v = decodePadded(r, b)
	}
	return v
}

func writePadded(w *nino.Writer, v Padded) {
	if b := w.ExtendZero(16); len(b) == 16 {
		encodePadded(b, v)
	}
}

// readSized and writeSized are one Sized (raw, 16 bytes), from one Take or ExtendZero.
func readSized(r *nino.Reader) (v Sized) {
	if b := r.Take(16); len(b) == 16 {
		v = decodeSized(r, b)
	}
	return v
}

func writeSized(w *nino.Writer, v Sized) {
	if b := w.ExtendZero(16); len(b) == 16 {
		encodeSized(b, v)
	}
}

// readVec2 and writeVec2 are one Vec2 (raw, 8 bytes), from one Take or ExtendZero.
func readVec2(r *nino.Reader) (v Vec2) {
	if b := r.Take(8); len(b) == 8 {
		v = decodeVec2(r, b)
	}
	return v
}

func writeVec2(w *nino.Writer, v Vec2) {
	if b := w.ExtendZero(8); len(b) == 8 {
		encodeVec2(b, v)
	}
}

// The loops and single values of NinoNullable<T>: fixed-size elements, 2 x size(T) bytes each.

func readNinoNullableUint8s(r *nino.Reader) []*uint8 {
	n := r.Length(2)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 2)
	if len(b) != n*2 {
		return nil
	}
	var e *uint8
	v := make([]*uint8, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for len(b) >= 2 {
		e = decodeNinoNullableUint8(r, b[:2:2])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[2:]
	}
	return v
}

func writeNinoNullableUint8s(w *nino.Writer, v []*uint8) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 2)
	if len(b) != len(v)*2 {
		return
	}
	for _, e := range v {
		encodeNinoNullableUint8(b[:2:2], e)
		b = b[2:]
	}
}

func readNinoNullableInt32s(r *nino.Reader) []*int32 {
	n := r.Length(8)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 8)
	if len(b) != n*8 {
		return nil
	}
	var e *int32
	v := make([]*int32, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for len(b) >= 8 {
		e = decodeNinoNullableInt32(r, b[:8:8])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[8:]
	}
	return v
}

func writeNinoNullableInt32s(w *nino.Writer, v []*int32) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 8)
	if len(b) != len(v)*8 {
		return
	}
	for _, e := range v {
		encodeNinoNullableInt32(b[:8:8], e)
		b = b[8:]
	}
}

func readNinoNullableInt64s(r *nino.Reader) []*int64 {
	n := r.Length(16)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 16)
	if len(b) != n*16 {
		return nil
	}
	var e *int64
	v := make([]*int64, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for len(b) >= 16 {
		e = decodeNinoNullableInt64(r, b[:16:16])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[16:]
	}
	return v
}

func writeNinoNullableInt64s(w *nino.Writer, v []*int64) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 16)
	if len(b) != len(v)*16 {
		return
	}
	for _, e := range v {
		encodeNinoNullableInt64(b[:16:16], e)
		b = b[16:]
	}
}

func readNinoNullableDecimals(r *nino.Reader) []*nino.Decimal {
	n := r.Length(32)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 32)
	if len(b) != n*32 {
		return nil
	}
	var e *nino.Decimal
	v := make([]*nino.Decimal, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for len(b) >= 32 {
		e = decodeNinoNullableDecimal(r, b[:32:32])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[32:]
	}
	return v
}

func writeNinoNullableDecimals(w *nino.Writer, v []*nino.Decimal) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 32)
	if len(b) != len(v)*32 {
		return
	}
	for _, e := range v {
		encodeNinoNullableDecimal(b[:32:32], e)
		b = b[32:]
	}
}

func readNinoNullableGuids(r *nino.Reader) []*nino.Guid {
	n := r.Length(32)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 32)
	if len(b) != n*32 {
		return nil
	}
	var e *nino.Guid
	v := make([]*nino.Guid, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for len(b) >= 32 {
		e = decodeNinoNullableGuid(r, b[:32:32])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[32:]
	}
	return v
}

func writeNinoNullableGuids(w *nino.Writer, v []*nino.Guid) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 32)
	if len(b) != len(v)*32 {
		return
	}
	for _, e := range v {
		encodeNinoNullableGuid(b[:32:32], e)
		b = b[32:]
	}
}

func readNinoNullablePaddeds(r *nino.Reader) []*Padded {
	n := r.Length(32)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 32)
	if len(b) != n*32 {
		return nil
	}
	var e *Padded
	v := make([]*Padded, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for len(b) >= 32 {
		e = decodeNinoNullablePadded(r, b[:32:32])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[32:]
	}
	return v
}

func writeNinoNullablePaddeds(w *nino.Writer, v []*Padded) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 32)
	if len(b) != len(v)*32 {
		return
	}
	for _, e := range v {
		encodeNinoNullablePadded(b[:32:32], e)
		b = b[32:]
	}
}

func readNinoNullableFieldsSlice(r *nino.Reader) []NinoNullableFields {
	n := r.Length(48)
	if n < 0 {
		return nil
	}
	b := r.Take(n * 48)
	if len(b) != n*48 {
		return nil
	}
	var e NinoNullableFields
	v := make([]NinoNullableFields, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for len(b) >= 48 {
		e = decodeNinoNullableFields(r, b[:48:48])
		if r.Failed() {
			return nil
		}
		v = append(v, e)
		b = b[48:]
	}
	return v
}

func writeNinoNullableFieldsSlice(w *nino.Writer, v []NinoNullableFields) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	b := w.ExtendZero(len(v) * 48)
	if len(b) != len(v)*48 {
		return
	}
	for _, e := range v {
		encodeNinoNullableFields(b[:48:48], e)
		b = b[48:]
	}
}

func readNinoNullableInt64(r *nino.Reader) (v *int64) {
	if b := r.Take(16); len(b) == 16 {
		v = decodeNinoNullableInt64(r, b)
	}
	return v
}

func writeNinoNullableInt64(w *nino.Writer, v *int64) {
	if b := w.ExtendZero(16); len(b) == 16 {
		encodeNinoNullableInt64(b, v)
	}
}

func readNinoNullableDecimal(r *nino.Reader) (v *nino.Decimal) {
	if b := r.Take(32); len(b) == 32 {
		v = decodeNinoNullableDecimal(r, b)
	}
	return v
}

func writeNinoNullableDecimal(w *nino.Writer, v *nino.Decimal) {
	if b := w.ExtendZero(32); len(b) == 32 {
		encodeNinoNullableDecimal(b, v)
	}
}

func readNinoNullableGuid(r *nino.Reader) (v *nino.Guid) {
	if b := r.Take(32); len(b) == 32 {
		v = decodeNinoNullableGuid(r, b)
	}
	return v
}

func writeNinoNullableGuid(w *nino.Writer, v *nino.Guid) {
	if b := w.ExtendZero(32); len(b) == 32 {
		encodeNinoNullableGuid(b, v)
	}
}

func readNinoNullablePadded(r *nino.Reader) (v *Padded) {
	if b := r.Take(32); len(b) == 32 {
		v = decodeNinoNullablePadded(r, b)
	}
	return v
}

func writeNinoNullablePadded(w *nino.Writer, v *Padded) {
	if b := w.ExtendZero(32); len(b) == 32 {
		encodeNinoNullablePadded(b, v)
	}
}

func readNinoNullableFields(r *nino.Reader) (v NinoNullableFields) {
	if b := r.Take(48); len(b) == 48 {
		v = decodeNinoNullableFields(r, b)
	}
	return v
}

func writeNinoNullableFields(w *nino.Writer, v NinoNullableFields) {
	if b := w.ExtendZero(48); len(b) == 48 {
		encodeNinoNullableFields(b, v)
	}
}

func readI32NinoNullableDecimalEntries(r *nino.Reader) []nino.Entry[int32, *nino.Decimal] {
	n := r.Length(36)
	if n < 0 {
		return nil
	}
	var e nino.Entry[int32, *nino.Decimal]
	v := make([]nino.Entry[int32, *nino.Decimal], 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		k := r.I32()
		e = nino.Entry[int32, *nino.Decimal]{Key: k, Value: readNinoNullableDecimal(r)}
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return nino.CheckUniqueKeys(r, v)
}

func writeI32NinoNullableDecimalEntries(w *nino.Writer, v []nino.Entry[int32, *nino.Decimal]) {
	if v == nil {
		w.NullLength()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		w.I32(e.Key)
		writeNinoNullableDecimal(w, e.Value)
	}
}

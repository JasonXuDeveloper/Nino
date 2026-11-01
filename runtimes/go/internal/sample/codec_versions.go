package sample

import nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"

// Version-tolerant bodies: a region; each member is read only while the region has bytes left. Keyed bodies: a
// region of {key, length, value} entries, in any order, each member at most once.

// The keys of the keyed members (FNV-1a of their names, as the layouts publish them).
const (
	keyKeyedV1Health   int32 = -1717475487 // 0x99A16761, also a former key of KeyedV2.Hp
	keyName            int32 = 814068706   // 0x3085B3E2
	keyKeyedV1Items    int32 = -592928685  // 0xDCA8A053
	keyScore           int32 = 72003917    // 0x044AB14D
	keyKeyedV2Hp       int32 = -1586416211 // 0xA17135AD
	keyKeyedV2Level    int32 = -358083429  // 0xEAA8149B
	keyKeyedStructA    int32 = 1792377636  // 0x6AD58324
	keyKeyedStructB    int32 = -589499809  // 0xDCDCF25F
	keyKeyedChainValue int32 = -1015864514 // 0xC373233E
	keyKeyedChainNext  int32 = -911863494  // 0xC9A6113A
)

var (
	tolerantV1Class     nino.Class[TolerantV1]
	tolerantV2Class     nino.Class[TolerantV2]
	tolerantEmptyClass  nino.Class[TolerantEmpty]
	tolerantBaseClass   nino.Class[TolerantBase]
	tolerantLeafClass   nino.Class[TolerantLeaf]
	tolerantHolderClass nino.Class[TolerantHolder]
	keyedV1Class        nino.Class[KeyedV1]
	keyedV2Class        nino.Class[KeyedV2]
	keyedHolderClass    nino.Class[KeyedHolder]
)

func init() {
	tolerantV1Class = nino.Class[TolerantV1]{Read: readTolerantV1Body, Write: writeTolerantV1Body, Deferrable: true}
	tolerantV2Class = nino.Class[TolerantV2]{Read: readTolerantV2Body, Write: writeTolerantV2Body, Deferrable: true}
	tolerantEmptyClass = nino.Class[TolerantEmpty]{Read: readTolerantEmptyBody, Write: writeTolerantEmptyBody, Deferrable: true}
	tolerantBaseClass = nino.Class[TolerantBase]{Read: readTolerantBaseBody, Write: writeTolerantBaseBody, Deferrable: true}
	tolerantLeafClass = nino.Class[TolerantLeaf]{Read: readTolerantLeafBody, Write: writeTolerantLeafBody, Deferrable: true}
	tolerantHolderClass = nino.Class[TolerantHolder]{Read: readTolerantHolderBody, Write: writeTolerantHolderBody, Deferrable: true}
	keyedV1Class = nino.Class[KeyedV1]{Read: readKeyedV1Body, Write: writeKeyedV1Body, Deferrable: true}
	keyedV2Class = nino.Class[KeyedV2]{Read: readKeyedV2Body, Write: writeKeyedV2Body, Deferrable: true}
	keyedHolderClass = nino.Class[KeyedHolder]{Read: readKeyedHolderBody, Write: writeKeyedHolderBody, Deferrable: true}
}

func (*TolerantBase) NinoTypeID() int32 { return idTolerantBase }
func (*TolerantLeaf) NinoTypeID() int32 { return idTolerantLeaf }
func (*TolerantBase) tolerantBase()     {}
func (*TolerantLeaf) tolerantBase()     {}

func ReadTolerantV1(r *nino.Reader) *TolerantV1       { return nino.ReadSealed(r, &tolerantV1Class) }
func WriteTolerantV1(w *nino.Writer, v *TolerantV1)   { nino.WriteSealed(w, v, &tolerantV1Class) }
func ReadTolerantV2(r *nino.Reader) *TolerantV2       { return nino.ReadSealed(r, &tolerantV2Class) }
func WriteTolerantV2(w *nino.Writer, v *TolerantV2)   { nino.WriteSealed(w, v, &tolerantV2Class) }
func ReadTolerantEmpty(r *nino.Reader) *TolerantEmpty { return nino.ReadSealed(r, &tolerantEmptyClass) }
func WriteTolerantEmpty(w *nino.Writer, v *TolerantEmpty) {
	nino.WriteSealed(w, v, &tolerantEmptyClass)
}
func ReadTolerantHolder(r *nino.Reader) *TolerantHolder {
	return nino.ReadSealed(r, &tolerantHolderClass)
}
func WriteTolerantHolder(w *nino.Writer, v *TolerantHolder) {
	nino.WriteSealed(w, v, &tolerantHolderClass)
}
func ReadKeyedV1(r *nino.Reader) *KeyedV1             { return nino.ReadSealed(r, &keyedV1Class) }
func WriteKeyedV1(w *nino.Writer, v *KeyedV1)         { nino.WriteSealed(w, v, &keyedV1Class) }
func ReadKeyedV2(r *nino.Reader) *KeyedV2             { return nino.ReadSealed(r, &keyedV2Class) }
func WriteKeyedV2(w *nino.Writer, v *KeyedV2)         { nino.WriteSealed(w, v, &keyedV2Class) }
func ReadKeyedHolder(r *nino.Reader) *KeyedHolder     { return nino.ReadSealed(r, &keyedHolderClass) }
func WriteKeyedHolder(w *nino.Writer, v *KeyedHolder) { nino.WriteSealed(w, v, &keyedHolderClass) }

// ReadTolerantBaseType reads a slot declared as TolerantBase: a TolerantBase or a TolerantLeaf.
func ReadTolerantBaseType(r *nino.Reader) TolerantBaseType {
	switch id := r.TypeID(); id {
	case nino.NullTypeID:
		return nil
	case idTolerantBase:
		return nino.ReadClass(r, &tolerantBaseClass)
	case idTolerantLeaf:
		return nino.ReadClass(r, &tolerantLeafClass)
	default:
		r.UnknownType(id)
		return nil
	}
}

func WriteTolerantBaseType(w *nino.Writer, v TolerantBaseType) {
	switch v := v.(type) {
	case nil:
		w.TypeID(nino.NullTypeID)
	case *TolerantBase:
		nino.WriteTyped(w, idTolerantBase, v, &tolerantBaseClass)
	case *TolerantLeaf:
		nino.WriteTyped(w, idTolerantLeaf, v, &tolerantLeafClass)
	}
}

func readTolerantV1Body(r *nino.Reader, v *TolerantV1) {
	g := r.BeginRegion()
	if r.More() {
		v.A = r.I32()
	}
	if r.More() {
		v.B = r.StringPtr()
	}
	r.EndRegion(g)
}

func writeTolerantV1Body(w *nino.Writer, v *TolerantV1) {
	g := w.BeginRegion()
	w.I32(v.A)
	w.StringPtr(v.B)
	w.EndRegion(g)
}

func readTolerantV2Body(r *nino.Reader, v *TolerantV2) {
	g := r.BeginRegion()
	if r.More() {
		v.A = r.I32()
	}
	if r.More() {
		v.B = r.StringPtr()
	}
	if r.More() {
		v.C = r.I64()
	}
	if r.More() {
		v.D = nino.ReadPrimitives[int32](r)
	}
	r.EndRegion(g)
}

func writeTolerantV2Body(w *nino.Writer, v *TolerantV2) {
	g := w.BeginRegion()
	w.I32(v.A)
	w.StringPtr(v.B)
	w.I64(v.C)
	nino.WritePrimitives(w, v.D)
	w.EndRegion(g)
}

func readTolerantEmptyBody(r *nino.Reader, _ *TolerantEmpty) { r.EndRegion(r.BeginRegion()) }

func writeTolerantEmptyBody(w *nino.Writer, _ *TolerantEmpty) { w.EndRegion(w.BeginRegion()) }

func readTolerantBaseBody(r *nino.Reader, v *TolerantBase) {
	g := r.BeginRegion()
	if r.More() {
		v.A = r.I32()
	}
	r.EndRegion(g)
}

func writeTolerantBaseBody(w *nino.Writer, v *TolerantBase) {
	g := w.BeginRegion()
	w.I32(v.A)
	w.EndRegion(g)
}

func readTolerantLeafBody(r *nino.Reader, v *TolerantLeaf) {
	g := r.BeginRegion()
	if r.More() {
		v.A = r.I32()
	}
	if r.More() {
		v.B = r.I32()
	}
	r.EndRegion(g)
}

func writeTolerantLeafBody(w *nino.Writer, v *TolerantLeaf) {
	g := w.BeginRegion()
	w.I32(v.A)
	w.I32(v.B)
	w.EndRegion(g)
}

func readTolerantHolderBody(r *nino.Reader, v *TolerantHolder) {
	v.First = ReadTolerantV1(r)
	v.Polymorphic = ReadTolerantBaseType(r)
	v.Empty = ReadTolerantEmpty(r)
	v.After = r.I32()
}

func writeTolerantHolderBody(w *nino.Writer, v *TolerantHolder) {
	WriteTolerantV1(w, v.First)
	WriteTolerantBaseType(w, v.Polymorphic)
	WriteTolerantEmpty(w, v.Empty)
	w.I32(v.After)
}

func readKeyedV1Body(r *nino.Reader, v *KeyedV1) {
	g := r.BeginRegion()
	var seen uint64
	for r.More() {
		key := r.I32()
		e := r.BeginMember()
		switch key {
		case keyKeyedV1Health:
			if r.KeyedSeen(&seen, 0) {
				v.Health = r.I32()
			}
		case keyName:
			if r.KeyedSeen(&seen, 1) {
				v.Name = r.StringPtr()
			}
		case keyKeyedV1Items:
			if r.KeyedSeen(&seen, 2) {
				v.Items = nino.ReadPrimitives[int32](r)
			}
		case keyScore:
			if r.KeyedSeen(&seen, 3) {
				v.Score = r.I64()
			}
		default:
			r.SkipMember(e)
			continue
		}
		r.EndMember(e)
	}
	r.EndRegion(g)
}

func writeKeyedV1Body(w *nino.Writer, v *KeyedV1) {
	g := w.BeginRegion()
	e := w.BeginMember(keyKeyedV1Health)
	w.I32(v.Health)
	w.EndMember(e)
	e = w.BeginMember(keyName)
	w.StringPtr(v.Name)
	w.EndMember(e)
	e = w.BeginMember(keyKeyedV1Items)
	nino.WritePrimitives(w, v.Items)
	w.EndMember(e)
	e = w.BeginMember(keyScore)
	w.I64(v.Score)
	w.EndMember(e)
	w.EndRegion(g)
}

func readKeyedV2Body(r *nino.Reader, v *KeyedV2) {
	g := r.BeginRegion()
	var seen uint64
	for r.More() {
		key := r.I32()
		e := r.BeginMember()
		switch key {
		case keyScore:
			if r.KeyedSeen(&seen, 0) {
				v.Score = r.I64()
			}
		case keyName:
			if r.KeyedSeen(&seen, 1) {
				v.Name = r.StringPtr()
			}
		case keyKeyedV2Hp, keyKeyedV1Health: // Hp, formerly Health
			if r.KeyedSeen(&seen, 2) {
				v.Hp = r.I32()
			}
		case keyKeyedV2Level:
			if r.KeyedSeen(&seen, 3) {
				v.Level = r.I32()
			}
		default:
			r.SkipMember(e)
			continue
		}
		r.EndMember(e)
	}
	r.EndRegion(g)
}

func writeKeyedV2Body(w *nino.Writer, v *KeyedV2) {
	g := w.BeginRegion()
	e := w.BeginMember(keyScore)
	w.I64(v.Score)
	w.EndMember(e)
	e = w.BeginMember(keyName)
	w.StringPtr(v.Name)
	w.EndMember(e)
	e = w.BeginMember(keyKeyedV2Hp)
	w.I32(v.Hp)
	w.EndMember(e)
	e = w.BeginMember(keyKeyedV2Level)
	w.I32(v.Level)
	w.EndMember(e)
	w.EndRegion(g)
}

// A keyed struct: a region with no header, and no class body (it does not count toward the depth).
func readKeyedStruct(r *nino.Reader) (v KeyedStruct) {
	g := r.BeginRegion()
	var seen uint64
	for r.More() {
		key := r.I32()
		e := r.BeginMember()
		switch key {
		case keyKeyedStructA:
			if r.KeyedSeen(&seen, 0) {
				v.A = r.I32()
			}
		case keyKeyedStructB:
			if r.KeyedSeen(&seen, 1) {
				v.B = r.F64()
			}
		default:
			r.SkipMember(e)
			continue
		}
		r.EndMember(e)
	}
	r.EndRegion(g)
	return v
}

func writeKeyedStruct(w *nino.Writer, v KeyedStruct) {
	g := w.BeginRegion()
	e := w.BeginMember(keyKeyedStructA)
	w.I32(v.A)
	w.EndMember(e)
	e = w.BeginMember(keyKeyedStructB)
	w.F64(v.B)
	w.EndMember(e)
	w.EndRegion(g)
}

func readKeyedHolderBody(r *nino.Reader, v *KeyedHolder) {
	v.Keyed = ReadKeyedV1(r)
	v.Struct = readKeyedStruct(r)
	v.Array = readKeyedV1s(r)
}

func writeKeyedHolderBody(w *nino.Writer, v *KeyedHolder) {
	WriteKeyedV1(w, v.Keyed)
	writeKeyedStruct(w, v.Struct)
	writeKeyedV1s(w, v.Array)
}

func ReadKeyedStruct(r *nino.Reader) KeyedStruct     { return readKeyedStruct(r) }
func WriteKeyedStruct(w *nino.Writer, v KeyedStruct) { writeKeyedStruct(w, v) }

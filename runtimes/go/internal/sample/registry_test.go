package sample

import nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"

// codec reads and writes one root type of the corpus, its value boxed.
type codec struct {
	read  func(*nino.Reader) any
	write func(*nino.Writer, any)
}

func codecOf[T any](read func(*nino.Reader) T, write func(*nino.Writer, T)) codec {
	return codec{
		read: func(r *nino.Reader) any { return read(r) },
		write: func(w *nino.Writer, v any) {
			t, ok := v.(T)
			if !ok && v != nil {
				w.Fail(nino.CodeInvalidValue, "wrong type")
				return
			}
			write(w, t)
		},
	}
}

func targetOf[T any](read func(*nino.Reader) T, write func(*nino.Writer, T)) codec {
	c := NewTargetCodec(read, write)
	return codecOf(c.Read, c.Write)
}

// codecs are the codecs of the corpus's root types, by .NET name. Types not here (reference tracking) are not
// supported by this runtime.
var codecs = map[string]codec{
	"Conformance.Integers":          codecOf(ReadIntegers, WriteIntegers),
	"Conformance.Floats":            codecOf(ReadFloats, WriteFloats),
	"Conformance.Scalars":           codecOf(ReadScalars, WriteScalars),
	"Conformance.Dates":             codecOf(ReadDates, WriteDates),
	"Conformance.Enums":             codecOf(ReadEnums, WriteEnums),
	"Conformance.BigValues":         codecOf(ReadBigValues, WriteBigValues),
	"Conformance.Strings":           codecOf(ReadStrings, WriteStrings),
	"Conformance.StringCollections": codecOf(ReadStringCollections, WriteStringCollections),
	"Conformance.Bytes":             codecOf(ReadBytes, WriteBytes),
	"Conformance.Point":             codecOf(ReadPoint, WritePoint),
	"Conformance.Structs":           codecOf(ReadStructs, WriteStructs),
	"Conformance.Outer":             codecOf(ReadOuter, WriteOuter),
	"Conformance.Collections":       codecOf(ReadCollections, WriteCollections),
	"Conformance.Nullables":         codecOf(ReadNullables, WriteNullables),
	"Conformance.Empties":           codecOf(ReadEmpties, WriteEmpties),
	"Conformance.TolerantV1":        codecOf(ReadTolerantV1, WriteTolerantV1),
	"Conformance.TolerantV2":        codecOf(ReadTolerantV2, WriteTolerantV2),
	"Conformance.TolerantHolder":    codecOf(ReadTolerantHolder, WriteTolerantHolder),
	"Conformance.KeyedV1":           codecOf(ReadKeyedV1, WriteKeyedV1),
	"Conformance.KeyedV2":           codecOf(ReadKeyedV2, WriteKeyedV2),
	"Conformance.KeyedHolder":       codecOf(ReadKeyedHolder, WriteKeyedHolder),
	"Conformance.Animal":            codecOf(ReadAnimal, WriteAnimal),
	"Conformance.Zoo":               codecOf(ReadZoo, WriteZoo),
	"Conformance.Chain":             codecOf(ReadChain, WriteChain),
	"Conformance.TolerantChain":     codecOf(ReadTolerantChain, WriteTolerantChain),
	"Conformance.KeyedChain":        codecOf(ReadKeyedChain, WriteKeyedChain),
	"Conformance.TreeNode":          codecOf(ReadTreeNode, WriteTreeNode),
	"Conformance.Link":              codecOf(ReadLink, WriteLink),
	"Conformance.RecordChain":       codecOf(ReadRecordChain, WriteRecordChain),
	"Conformance.TolerantRoot":      codecOf(ReadTolerantRoot, WriteTolerantRoot),
	"Conformance.TreeHolder":        codecOf(ReadTreeHolder, WriteTreeHolder),
	"Conformance.Vec2":              codecOf(ReadVec2, WriteVec2),
	"Conformance.Padded":            codecOf(ReadPadded, WritePadded),
	"Conformance.Nested":            codecOf(ReadNested, WriteNested),
	"Conformance.Label":             codecOf(ReadLabel, WriteLabel),
	"Conformance.KeyedStruct":       codecOf(ReadKeyedStruct, WriteKeyedStruct),
	"Conformance.EmptyStruct":       codecOf(ReadEmptyStruct, WriteEmptyStruct),

	"Conformance.Targets.AnimalSlot":    targetOf(ReadAnimal, WriteAnimal),
	"Conformance.Targets.BigSlot":       targetOf(ReadBig, (*nino.Writer).BigInteger),
	"Conformance.Targets.BoolArray":     targetOf(nino.ReadBools, nino.WriteBools),
	"Conformance.Targets.BoolValue":     targetOf((*nino.Reader).Bool, (*nino.Writer).Bool),
	"Conformance.Targets.ByteArray":     targetOf((*nino.Reader).ByteArray, (*nino.Writer).ByteArray),
	"Conformance.Targets.DogSlot":       targetOf(ReadDogType, WriteDogType),
	"Conformance.Targets.Grid":          targetOf(ReadIntGrid, WriteIntGrid),
	"Conformance.Targets.IntArray":      targetOf(nino.ReadPrimitives[int32], nino.WritePrimitives[int32]),
	"Conformance.Targets.IntMap":        targetOf(ReadIntMap, WriteIntMap),
	"Conformance.Targets.IntSet":        targetOf(ReadIntSet, nino.WritePrimitives[int32]),
	"Conformance.Targets.LongList":      targetOf(nino.ReadPrimitives[int64], nino.WritePrimitives[int64]),
	"Conformance.Targets.MaybeInt":      targetOf(ReadMaybeInt, WriteMaybeInt),
	"Conformance.Targets.MaybeIntArray": targetOf(ReadMaybeIntArray, WriteMaybeIntArray),
	"Conformance.Targets.ObjectSlot":    targetOf(ReadObject, WriteObject),
	"Conformance.Targets.PaddedArray":   targetOf(ReadPaddedArray, WritePaddedArray),
	"Conformance.Targets.PointList":     targetOf(ReadPointList, WritePointList),
	"Conformance.Targets.ShapeSlot":     targetOf(ReadShape, WriteShape),
	"Conformance.Targets.SortedMap":     targetOf(ReadIntMap, WriteIntMap),
	"Conformance.Targets.StringList":    targetOf(ReadStringList, WriteStringList),
	"Conformance.Targets.StringMap":     targetOf(ReadStringIntMap, WriteStringIntMap),
	"Conformance.Targets.StringSet":     targetOf(ReadStringSet, WriteStringList),
	"Conformance.Targets.TextValue":     targetOf((*nino.Reader).StringPtr, (*nino.Writer).StringPtr),
	"Conformance.Targets.Utf8Value":     targetOf((*nino.Reader).UTF8Ptr, (*nino.Writer).UTF8Ptr),
	"Conformance.Targets.VersionSlot":   targetOf((*nino.Reader).Version, (*nino.Writer).Version),
}

package sample

// The type ids of the .NET types (FNV-1a of their names, as their layouts publish them).
const (
	idAnimal            int32 = -749123728  // Conformance.Animal, 0xD3594770
	idBigValues         int32 = -714180646  // Conformance.BigValues, 0xD56E77DA
	idBlueLink          int32 = -1446673464 // Conformance.BlueLink, 0xA9C583C8
	idBoxInt32          int32 = -794599344  // Conformance.Box<System.Int32>, 0xD0A36050
	idBoxPoints         int32 = -1116656767 // Conformance.Box<System.Collections.Generic.List<Conformance.Point>>, 0xBD712B81
	idBoxString         int32 = -110345065  // Conformance.Box<System.String>, 0xF96C4497
	idBoxVec2           int32 = -1580784064 // Conformance.Box<Conformance.Vec2>, 0xA1C72640
	idBytes             int32 = 1087255839  // Conformance.Bytes, 0x40CE351F
	idCat               int32 = -1384569984 // Conformance.Cat, 0xAD792380
	idChain             int32 = -2126005709 // Conformance.Chain, 0x8147BA33
	idCircle            int32 = -794369036  // Conformance.Circle, 0xD0A6E3F4
	idCollections       int32 = -20025893   // Conformance.Collections, 0xFECE6DDB
	idDates             int32 = 5206977     // Conformance.Dates, 0x004F73C1
	idDog               int32 = 1772922506  // Conformance.Dog, 0x69ACA68A
	idEmpties           int32 = -159944363  // Conformance.Empties, 0xF6777155
	idEmpty             int32 = 211121179   // Conformance.Empty, 0x0C95741B
	idEmptyStruct       int32 = 413335892   // Conformance.EmptyStruct, 0x18A30154
	idEnums             int32 = 1206893526  // Conformance.Enums, 0x47EFBBD6
	idFloats            int32 = 889504333   // Conformance.Floats, 0x3504C24D
	idIShape            int32 = 2093204672  // Conformance.IShape, 0x7CC3C4C0
	idInner             int32 = -1898071576 // Conformance.Inner, 0x8EDDB9E8
	idIntegers          int32 = 347943379   // Conformance.Integers, 0x14BD31D3
	idKeyedChain        int32 = -872576427  // Conformance.KeyedChain, 0xCBFD8A55
	idKeyedHolder       int32 = 2143134216  // Conformance.KeyedHolder, 0x7FBDA208
	idKeyedStruct       int32 = 1686252215  // Conformance.KeyedStruct, 0x64822AB7
	idKeyedV1           int32 = -1887022585 // Conformance.KeyedV1, 0x8F865207
	idKeyedV2           int32 = 494854860   // Conformance.KeyedV2, 0x1D7EE2CC
	idLabel             int32 = 877594808   // Conformance.Label, 0x344F08B8
	idLink              int32 = 134907422   // Conformance.Link, 0x080A861E
	idMiddle            int32 = -765756749  // Conformance.Middle, 0xD25B7AB3
	idMixed             int32 = 235403483   // Conformance.Mixed, 0x0E07F8DB
	idNested            int32 = 103626619   // Conformance.Nested, 0x062D377B
	idNullables         int32 = 958404412   // Conformance.Nullables, 0x3920173C
	idOuter             int32 = 1062789439  // Conformance.Outer, 0x3F58E13F
	idPacked            int32 = -282809690  // Conformance.Packed, 0xEF24AAA6
	idPadded            int32 = 717941110   // Conformance.Padded, 0x2ACAE976
	idPlain             int32 = 824449116   // Conformance.Plain, 0x3124185C
	idPoint             int32 = -1773443910 // Conformance.Point, 0x964B64BA
	idPuppy             int32 = -1808724502 // Conformance.Puppy, 0x94310DEA
	idRecordChain       int32 = 552666118   // Conformance.RecordChain, 0x20F10406
	idRedLink           int32 = -1617946819 // Conformance.RedLink, 0x9F90173D
	idScalars           int32 = -764251517  // Conformance.Scalars, 0xD2727283
	idSized             int32 = -1279208969 // Conformance.Sized, 0xB3C0D1F7
	idSquare            int32 = -2075505753 // Conformance.Square, 0x844A4BA7
	idStringCollections int32 = 1951192054  // Conformance.StringCollections, 0x744CD3F6
	idStrings           int32 = -843251688  // Conformance.Strings, 0xCDBD0018
	idStructs           int32 = 611746818   // Conformance.Structs, 0x24768402
	idTolerantBase      int32 = -1791558008 // Conformance.TolerantBase, 0x9536FE88
	idTolerantChain     int32 = 416993288   // Conformance.TolerantChain, 0x18DAD008
	idTolerantEmpty     int32 = -2052971616 // Conformance.TolerantEmpty, 0x85A223A0
	idTolerantHolder    int32 = 766088429   // Conformance.TolerantHolder, 0x2DA994ED
	idTolerantLeaf      int32 = 1803752715  // Conformance.TolerantLeaf, 0x6B83150B
	idTolerantRoot      int32 = -134826285  // Conformance.TolerantRoot, 0xF7F6B6D3
	idTolerantV1        int32 = 498962962   // Conformance.TolerantV1, 0x1DBD9212
	idTolerantV2        int32 = -138733655  // Conformance.TolerantV2, 0xF7BB17A9
	idTracked           int32 = -1904438432 // Conformance.Tracked, 0x8E7C9360
	idTrackedHolder     int32 = -92998560   // Conformance.TrackedHolder, 0xFA74F460
	idTrackedLeaf       int32 = 1730134150  // Conformance.TrackedLeaf, 0x671FC086
	idTreeHolder        int32 = -1576381312 // Conformance.TreeHolder, 0xA20A5480
	idTreeNode          int32 = 1980065472  // Conformance.TreeNode, 0x760566C0
	idVec2              int32 = 1393705508  // Conformance.Vec2, 0x53124224
	idZoo               int32 = 1854703100  // Conformance.Zoo, 0x6E8C85FC
)

// typeNames maps each id to its .NET name.
var typeNames = map[int32]string{
	idAnimal:            "Conformance.Animal",
	idBigValues:         "Conformance.BigValues",
	idBlueLink:          "Conformance.BlueLink",
	idBoxInt32:          "Conformance.Box<System.Int32>",
	idBoxPoints:         "Conformance.Box<System.Collections.Generic.List<Conformance.Point>>",
	idBoxString:         "Conformance.Box<System.String>",
	idBoxVec2:           "Conformance.Box<Conformance.Vec2>",
	idBytes:             "Conformance.Bytes",
	idCat:               "Conformance.Cat",
	idChain:             "Conformance.Chain",
	idCircle:            "Conformance.Circle",
	idCollections:       "Conformance.Collections",
	idDates:             "Conformance.Dates",
	idDog:               "Conformance.Dog",
	idEmpties:           "Conformance.Empties",
	idEmpty:             "Conformance.Empty",
	idEmptyStruct:       "Conformance.EmptyStruct",
	idEnums:             "Conformance.Enums",
	idFloats:            "Conformance.Floats",
	idIShape:            "Conformance.IShape",
	idInner:             "Conformance.Inner",
	idIntegers:          "Conformance.Integers",
	idKeyedChain:        "Conformance.KeyedChain",
	idKeyedHolder:       "Conformance.KeyedHolder",
	idKeyedStruct:       "Conformance.KeyedStruct",
	idKeyedV1:           "Conformance.KeyedV1",
	idKeyedV2:           "Conformance.KeyedV2",
	idLabel:             "Conformance.Label",
	idLink:              "Conformance.Link",
	idMiddle:            "Conformance.Middle",
	idMixed:             "Conformance.Mixed",
	idNested:            "Conformance.Nested",
	idNullables:         "Conformance.Nullables",
	idOuter:             "Conformance.Outer",
	idPacked:            "Conformance.Packed",
	idPadded:            "Conformance.Padded",
	idPlain:             "Conformance.Plain",
	idPoint:             "Conformance.Point",
	idPuppy:             "Conformance.Puppy",
	idRecordChain:       "Conformance.RecordChain",
	idRedLink:           "Conformance.RedLink",
	idScalars:           "Conformance.Scalars",
	idSized:             "Conformance.Sized",
	idSquare:            "Conformance.Square",
	idStringCollections: "Conformance.StringCollections",
	idStrings:           "Conformance.Strings",
	idStructs:           "Conformance.Structs",
	idTolerantBase:      "Conformance.TolerantBase",
	idTolerantChain:     "Conformance.TolerantChain",
	idTolerantEmpty:     "Conformance.TolerantEmpty",
	idTolerantHolder:    "Conformance.TolerantHolder",
	idTolerantLeaf:      "Conformance.TolerantLeaf",
	idTolerantRoot:      "Conformance.TolerantRoot",
	idTolerantV1:        "Conformance.TolerantV1",
	idTolerantV2:        "Conformance.TolerantV2",
	idTracked:           "Conformance.Tracked",
	idTrackedHolder:     "Conformance.TrackedHolder",
	idTrackedLeaf:       "Conformance.TrackedLeaf",
	idTreeHolder:        "Conformance.TreeHolder",
	idTreeNode:          "Conformance.TreeNode",
	idVec2:              "Conformance.Vec2",
	idZoo:               "Conformance.Zoo",
}

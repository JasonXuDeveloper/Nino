// Package sample holds Go counterparts of the types of the conformance corpus (namespace Conformance of
// conformance/Nino.Conformance/Types.cs), with codecs written by hand in the style a generator would emit: one
// read and one write function per type, raw runs read with one bounds check, polymorphic slots switching over the
// ids of the types they allow.
//
// Conventions: a sealed class is *T (nil for null); a string is *string (nil for null); a struct is T; a
// Nullable<T> is *T; a non-sealed class, an abstract class or an interface is a Go interface that the allowed
// types implement; a dictionary or a set is a slice in the order of the data (so writing it back gives the same
// bytes), checked for repeated keys when read.
package sample

import (
	"math/big"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go"
)

// ---------------------------------------------------------------- enums

type (
	E8          int8
	EU8         uint8
	E16         int16
	EU16        uint16
	E32         int32
	EU32        uint32
	E64         int64
	EU64        uint64
	Permissions uint8
	WideFlags   uint64
)

// ---------------------------------------------------------------- primitives

type Integers struct {
	I8   int8         `nino:"0" json:"i8" yaml:"i8"`
	U8   uint8        `nino:"1" json:"u8" yaml:"u8"`
	I16  int16        `nino:"2" json:"i16" yaml:"i16"`
	U16  uint16       `nino:"3" json:"u16" yaml:"u16"`
	I32  int32        `nino:"4" json:"i32" yaml:"i32"`
	U32  uint32       `nino:"5" json:"u32" yaml:"u32"`
	I64  int64        `nino:"6" json:"i64" yaml:"i64"`
	U64  uint64       `nino:"7" json:"u64" yaml:"u64"`
	I128 nino.Int128  `nino:"8" json:"i128" yaml:"i128"`
	U128 nino.UInt128 `nino:"9" json:"u128" yaml:"u128"`
}

type Floats struct {
	F16 nino.Half `nino:"0" json:"f16" yaml:"f16"`
	F32 float32   `nino:"1" json:"f32" yaml:"f32"`
	F64 float64   `nino:"2" json:"f64" yaml:"f64"`
}

type Scalars struct {
	Flag    bool          `nino:"0" json:"flag" yaml:"flag"`
	Char    uint16        `nino:"1" json:"char" yaml:"char"`
	Decimal nino.Decimal  `nino:"2" json:"decimal" yaml:"decimal"`
	Guid    nino.Guid     `nino:"3" json:"guid" yaml:"guid"`
	Span    nino.TimeSpan `nino:"4" json:"span" yaml:"span"`
	Date    nino.DateOnly `nino:"5" json:"date" yaml:"date"`
	Time    nino.TimeOnly `nino:"6" json:"time" yaml:"time"`
}

type Dates struct {
	Utc         nino.DateTime       `nino:"0" json:"utc" yaml:"utc"`
	Local       nino.DateTime       `nino:"1" json:"local" yaml:"local"`
	Unspecified nino.DateTime       `nino:"2" json:"unspecified" yaml:"unspecified"`
	Offset      nino.DateTimeOffset `nino:"3" json:"offset" yaml:"offset"`
}

type Enums struct {
	A     E8          `nino:"0" json:"a" yaml:"a"`
	B     EU8         `nino:"1" json:"b" yaml:"b"`
	C     E16         `nino:"2" json:"c" yaml:"c"`
	D     EU16        `nino:"3" json:"d" yaml:"d"`
	E     E32         `nino:"4" json:"e" yaml:"e"`
	F     EU32        `nino:"5" json:"f" yaml:"f"`
	G     E64         `nino:"6" json:"g" yaml:"g"`
	H     EU64        `nino:"7" json:"h" yaml:"h"`
	Flags Permissions `nino:"8" json:"flags" yaml:"flags"`
	Wide  WideFlags   `nino:"9" json:"wide" yaml:"wide"`
	Array []E32       `nino:"10" json:"array" yaml:"array"`
}

type BigValues struct {
	Big     *big.Int      `nino:"0" json:"big" yaml:"big"`
	Version *nino.Version `nino:"1" json:"version" yaml:"version"`
}

type Strings struct {
	Text *string `nino:"0" json:"text" yaml:"text"`
	Utf8 *string `nino:"1,utf8" json:"utf8" yaml:"utf8"`
}

type StringCollections struct {
	Array []*string `nino:"0" json:"array" yaml:"array"`
	List  []*string `nino:"1" json:"list" yaml:"list"`
	Utf8A *string   `nino:"2,utf8" json:"utf8A" yaml:"utf8A"`
	Plain *string   `nino:"3" json:"plain" yaml:"plain"`
	Utf8B *string   `nino:"4,utf8" json:"utf8B" yaml:"utf8B"`
}

// ByteList is a List<byte>: the same bytes as a byte[], but a list of numbers in the corpus's JSON.
type ByteList []byte

type Bytes struct {
	Array  []byte   `nino:"0" json:"array" yaml:"array"`
	List   ByteList `nino:"1" json:"list" yaml:"list"`
	Signed []int8   `nino:"2" json:"signed" yaml:"signed"`
}

// ---------------------------------------------------------------- objects and structs

type Point struct {
	X int32 `nino:"0" json:"x" yaml:"x"`
	Y int32 `nino:"1" json:"y" yaml:"y"`
}

// Vec2 is raw: 8 bytes.
type Vec2 struct {
	X float32 `nino:"0" json:"x" yaml:"x"`
	Y float32 `nino:"1" json:"y" yaml:"y"`
}

// Padded is raw: A at 0, B at 8, 16 bytes.
type Padded struct {
	A uint8 `nino:"0" json:"a" yaml:"a"`
	B int64 `nino:"1" json:"b" yaml:"b"`
}

// Mixed is raw: A at 0, B at 2, C at 4, D at 8, 12 bytes.
type Mixed struct {
	A uint8 `nino:"0" json:"a" yaml:"a"`
	B int16 `nino:"1" json:"b" yaml:"b"`
	C int32 `nino:"2" json:"c" yaml:"c"`
	D uint8 `nino:"3" json:"d" yaml:"d"`
}

// Packed is raw with Pack = 1: A at 0, B at 1, C at 9, 11 bytes.
type Packed struct {
	A uint8 `nino:"0" json:"a" yaml:"a"`
	B int64 `nino:"1" json:"b" yaml:"b"`
	C int16 `nino:"2" json:"c" yaml:"c"`
}

// Sized is raw with Size = 16: A at 0.
type Sized struct {
	A int32 `nino:"0" json:"a" yaml:"a"`
}

// Nested is raw: Inner at 0, Position at 16, Kind at 24, Flag at 25, 32 bytes.
type Nested struct {
	Inner    Padded `nino:"0" json:"inner" yaml:"inner"`
	Position Vec2   `nino:"1" json:"position" yaml:"position"`
	Kind     EU8    `nino:"2" json:"kind" yaml:"kind"`
	Flag     bool   `nino:"3" json:"flag" yaml:"flag"`
}

// Label is a struct that is not raw (it holds a string): written member by member.
type Label struct {
	Name   *string `nino:"0" json:"name" yaml:"name"`
	Weight int32   `nino:"1" json:"weight" yaml:"weight"`
}

// Tuple2 is a ValueTuple of two items.
type Tuple2[A, B any] struct {
	Item1 A `nino:"0" json:"item1" yaml:"item1"`
	Item2 B `nino:"1" json:"item2" yaml:"item2"`
}

type Structs struct {
	Vec        Vec2                      `nino:"0" json:"vec" yaml:"vec"`
	Padded     Padded                    `nino:"1" json:"padded" yaml:"padded"`
	Mixed      Mixed                     `nino:"2" json:"mixed" yaml:"mixed"`
	Packed     Packed                    `nino:"3" json:"packed" yaml:"packed"`
	Sized      Sized                     `nino:"4" json:"sized" yaml:"sized"`
	Nested     Nested                    `nino:"5" json:"nested" yaml:"nested"`
	Label      Label                     `nino:"6" json:"label" yaml:"label"`
	VecArray   []Vec2                    `nino:"7" json:"vecArray" yaml:"vecArray"`
	PaddedList []Padded                  `nino:"8" json:"paddedList" yaml:"paddedList"`
	MixedArray []Mixed                   `nino:"9" json:"mixedArray" yaml:"mixedArray"`
	Labels     []Label                   `nino:"10" json:"labels" yaml:"labels"`
	RawTuple   Tuple2[uint8, int64]      `nino:"11" json:"rawTuple" yaml:"rawTuple"`
	Tuple      Tuple2[int32, *string]    `nino:"12" json:"tuple" yaml:"tuple"`
	Pair       nino.Entry[*string, Vec2] `nino:"13" json:"pair" yaml:"pair"`
}

type Outer struct {
	Name   *string `nino:"0" json:"name" yaml:"name"`
	Point  *Point  `nino:"1" json:"point" yaml:"point"`
	Middle *Middle `nino:"2" json:"middle" yaml:"middle"`
}

type Middle struct {
	First  *Point `nino:"0" json:"first" yaml:"first"`
	Second *Point `nino:"1" json:"second" yaml:"second"`
	Inner  *Inner `nino:"2" json:"inner" yaml:"inner"`
}

type Inner struct {
	Value int64   `nino:"0" json:"value" yaml:"value"`
	Text  *string `nino:"1" json:"text" yaml:"text"`
	Point *Point  `nino:"2" json:"point" yaml:"point"`
}

// Grid is a two-dimensional array: its lengths and its items in row-major order.
type Grid[T any] struct {
	Lengths [2]int `json:"lengths" yaml:"lengths"`
	Items   []T    `json:"items" yaml:"items"`
}

type Collections struct {
	Ints          []int32                        `nino:"0" json:"ints" yaml:"ints"`
	IntList       []int32                        `nino:"1" json:"intList" yaml:"intList"`
	IntSet        []int32                        `nino:"2" json:"intSet" yaml:"intSet"`
	Longs         []int64                        `nino:"3" json:"longs" yaml:"longs"`
	Doubles       []float64                      `nino:"4" json:"doubles" yaml:"doubles"`
	Bools         []bool                         `nino:"5" json:"bools" yaml:"bools"`
	Chars         []uint16                       `nino:"6" json:"chars" yaml:"chars"`
	Strings       []*string                      `nino:"7" json:"strings" yaml:"strings"`
	StringSet     []*string                      `nino:"8" json:"stringSet" yaml:"stringSet"`
	Points        []*Point                       `nino:"9" json:"points" yaml:"points"`
	PointList     []*Point                       `nino:"10" json:"pointList" yaml:"pointList"`
	IntMap        []nino.Entry[int32, int32]     `nino:"11" json:"intMap" yaml:"intMap"`
	StringMap     []nino.Entry[string, *string]  `nino:"12" json:"stringMap" yaml:"stringMap"`
	PointMap      []nino.Entry[string, *Point]   `nino:"13" json:"pointMap" yaml:"pointMap"`
	ListMap       []nino.Entry[int32, []*string] `nino:"14" json:"listMap" yaml:"listMap"`
	SortedInts    []nino.Entry[int32, *string]   `nino:"15" json:"sortedInts" yaml:"sortedInts"`
	SortedStrings []nino.Entry[string, int32]    `nino:"16" json:"sortedStrings" yaml:"sortedStrings"`
	Nested        [][]int32                      `nino:"17" json:"nested" yaml:"nested"`
	Jagged        [][]int32                      `nino:"18" json:"jagged" yaml:"jagged"`
	Grid          *Grid[int32]                   `nino:"19" json:"grid" yaml:"grid"`
	StringGrid    *Grid[*string]                 `nino:"20" json:"stringGrid" yaml:"stringGrid"`
}

type Nullables struct {
	Int         *int32                      `nino:"0" json:"int" yaml:"int"`
	Long        *int64                      `nino:"1" json:"long" yaml:"long"`
	Double      *float64                    `nino:"2" json:"double" yaml:"double"`
	Bool        *bool                       `nino:"3" json:"bool" yaml:"bool"`
	Guid        *nino.Guid                  `nino:"4" json:"guid" yaml:"guid"`
	Enum        *E16                        `nino:"5" json:"enum" yaml:"enum"`
	Vec         *Vec2                       `nino:"6" json:"vec" yaml:"vec"`
	Padded      *Padded                     `nino:"7" json:"padded" yaml:"padded"`
	Label       *Label                      `nino:"8" json:"label" yaml:"label"`
	IntArray    []*int32                    `nino:"9" json:"intArray" yaml:"intArray"`
	LongList    []*int64                    `nino:"10" json:"longList" yaml:"longList"`
	PaddedArray []*Padded                   `nino:"11" json:"paddedArray" yaml:"paddedArray"`
	Map         []nino.Entry[int32, *int32] `nino:"12" json:"map" yaml:"map"`
}

// ---------------------------------------------------------------- version tolerant and keyed

type TolerantV1 struct {
	A int32   `nino:"0" json:"a" yaml:"a"`
	B *string `nino:"1" json:"b" yaml:"b"`
}

type TolerantV2 struct {
	A int32   `nino:"0" json:"a" yaml:"a"`
	B *string `nino:"1" json:"b" yaml:"b"`
	C int64   `nino:"2" json:"c" yaml:"c"`
	D []int32 `nino:"3" json:"d" yaml:"d"`
}

type TolerantEmpty struct{}

// TolerantBase is a non-sealed class: written behind a type id, as itself or as a TolerantLeaf.
type TolerantBase struct {
	A int32 `nino:"0" json:"a" yaml:"a"`
}

type TolerantLeaf struct {
	A int32 `nino:"0" json:"a" yaml:"a"`
	B int32 `nino:"1" json:"b" yaml:"b"`
}

// TolerantBaseType is a value of a slot declared as TolerantBase.
type TolerantBaseType interface {
	nino.Typed
	tolerantBase()
}

type TolerantHolder struct {
	First       *TolerantV1      `nino:"0" json:"first" yaml:"first"`
	Polymorphic TolerantBaseType `nino:"1" json:"polymorphic" yaml:"polymorphic"`
	Empty       *TolerantEmpty   `nino:"2" json:"empty" yaml:"empty"`
	After       int32            `nino:"3" json:"after" yaml:"after"`
}

type KeyedV1 struct {
	Health int32   `nino:"Health" json:"health" yaml:"health"`
	Name   *string `nino:"Name" json:"name" yaml:"name"`
	Items  []int32 `nino:"Items" json:"items" yaml:"items"`
	Score  int64   `nino:"Score" json:"score" yaml:"score"`
}

type KeyedV2 struct {
	Score int64   `nino:"Score" json:"score" yaml:"score"`
	Name  *string `nino:"Name" json:"name" yaml:"name"`
	Hp    int32   `nino:"Hp,former=Health" json:"hp" yaml:"hp"`
	Level int32   `nino:"Level" json:"level" yaml:"level"`
}

type KeyedStruct struct {
	A int32   `nino:"A" json:"a" yaml:"a"`
	B float64 `nino:"B" json:"b" yaml:"b"`
}

type KeyedHolder struct {
	Keyed  *KeyedV1    `nino:"0" json:"keyed" yaml:"keyed"`
	Struct KeyedStruct `nino:"1" json:"struct" yaml:"struct"`
	Array  []*KeyedV1  `nino:"2" json:"array" yaml:"array"`
}

// ---------------------------------------------------------------- polymorphism

// Animal is a value of a slot declared as the abstract class Animal.
type Animal interface {
	nino.Typed
	animal()
}

// DogType is a value of a slot declared as the non-sealed class Dog: a Dog or a Puppy.
type DogType interface {
	Animal
	dog()
}

type Dog struct {
	Name  *string `nino:"0" json:"name" yaml:"name"`
	Bones int32   `nino:"1" json:"bones" yaml:"bones"`
}

type Puppy struct {
	Name   *string `nino:"0" json:"name" yaml:"name"`
	Bones  int32   `nino:"1" json:"bones" yaml:"bones"`
	Sleepy bool    `nino:"2" json:"sleepy" yaml:"sleepy"`
}

type Cat struct {
	Name  *string `nino:"0" json:"name" yaml:"name"`
	Lives float32 `nino:"1" json:"lives" yaml:"lives"`
}

// BoxInt32 is Box<int>; the generic class's closed types each get a Go type.
type BoxInt32 struct {
	Name  *string `nino:"0" json:"name" yaml:"name"`
	Value int32   `nino:"1" json:"value" yaml:"value"`
}

// BoxString is Box<string>.
type BoxString struct {
	Name  *string `nino:"0" json:"name" yaml:"name"`
	Value *string `nino:"1" json:"value" yaml:"value"`
}

// BoxPoints is Box<List<Point>>.
type BoxPoints struct {
	Name  *string  `nino:"0" json:"name" yaml:"name"`
	Value []*Point `nino:"1" json:"value" yaml:"value"`
}

// BoxVec2 is Box<Vec2>.
type BoxVec2 struct {
	Name  *string `nino:"0" json:"name" yaml:"name"`
	Value Vec2    `nino:"1" json:"value" yaml:"value"`
}

// Shape is a value of a slot declared as the interface IShape.
type Shape interface {
	nino.Typed
	shape()
}

type Square struct {
	Area float64 `nino:"0" json:"area" yaml:"area"`
	Side float64 `nino:"1" json:"side" yaml:"side"`
}

type Circle struct {
	Area   float64 `nino:"0" json:"area" yaml:"area"`
	Radius float64 `nino:"1" json:"radius" yaml:"radius"`
}

// Plain is a non-sealed class without subtypes: still written behind a type id.
type Plain struct {
	Value int32 `nino:"0" json:"value" yaml:"value"`
}

type Zoo struct {
	Star     Animal                       `nino:"0" json:"star" yaml:"star"`
	Dog      DogType                      `nino:"1" json:"dog" yaml:"dog"`
	Animals  []Animal                     `nino:"2" json:"animals" yaml:"animals"`
	Cages    []Animal                     `nino:"3" json:"cages" yaml:"cages"`
	Shape    Shape                        `nino:"4" json:"shape" yaml:"shape"`
	Shapes   []Shape                      `nino:"5" json:"shapes" yaml:"shapes"`
	Plain    *Plain                       `nino:"6" json:"plain" yaml:"plain"`
	Anything nino.Typed                   `nino:"7" json:"anything" yaml:"anything"`
	ByName   []nino.Entry[string, Animal] `nino:"8" json:"byName" yaml:"byName"`
}

// ---------------------------------------------------------------- deep nesting

type Chain struct {
	Value int32  `nino:"0" json:"value" yaml:"value"`
	Next  *Chain `nino:"1" json:"next" yaml:"next"`
}

type TolerantChain struct {
	Value int32          `nino:"0" json:"value" yaml:"value"`
	Next  *TolerantChain `nino:"1" json:"next" yaml:"next"`
}

type KeyedChain struct {
	Value int32       `nino:"Value" json:"value" yaml:"value"`
	Next  *KeyedChain `nino:"Next" json:"next" yaml:"next"`
}

type TreeNode struct {
	Value    int32       `nino:"0" json:"value" yaml:"value"`
	Children []*TreeNode `nino:"1" json:"children" yaml:"children"`
}

// Link is a value of a slot declared as the abstract class Link.
type Link interface {
	nino.Typed
	link()
}

type RedLink struct {
	Value int32 `nino:"0" json:"value" yaml:"value"`
	Next  Link  `nino:"1" json:"next" yaml:"next"`
}

type BlueLink struct {
	Value int32   `nino:"0" json:"value" yaml:"value"`
	Next  Link    `nino:"1" json:"next" yaml:"next"`
	Note  *string `nino:"2" json:"note" yaml:"note"`
}

// RecordChain is a record: .NET builds it through its constructor, so its bodies are never deferred.
type RecordChain struct {
	Value int32        `nino:"0" json:"value" yaml:"value"`
	Next  *RecordChain `nino:"1" json:"next" yaml:"next"`
}

type TolerantRoot struct {
	Name  *string `nino:"0" json:"name" yaml:"name"`
	Chain *Chain  `nino:"1" json:"chain" yaml:"chain"`
	After int32   `nino:"2" json:"after" yaml:"after"`
}

// ---------------------------------------------------------------- self-nesting collections

// Tree is a List<Tree>.
type Tree []Tree

// DictTree is a Dictionary<string, DictTree>.
type DictTree []nino.Entry[string, DictTree]

type TreeHolder struct {
	Tree     Tree     `nino:"0" json:"tree" yaml:"tree"`
	DictTree DictTree `nino:"1" json:"dictTree" yaml:"dictTree"`
}

// ---------------------------------------------------------------- everything empty

type Empty struct{}

// EmptyStruct is raw, and written as the one byte .NET gives an empty struct.
type EmptyStruct struct{}

type Empties struct {
	Class   *Empty        `nino:"0" json:"class" yaml:"class"`
	Struct  EmptyStruct   `nino:"1" json:"struct" yaml:"struct"`
	Structs []EmptyStruct `nino:"2" json:"structs" yaml:"structs"`
	Classes []*Empty      `nino:"3" json:"classes" yaml:"classes"`
}

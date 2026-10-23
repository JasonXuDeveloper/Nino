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
	I8   int8
	U8   uint8
	I16  int16
	U16  uint16
	I32  int32
	U32  uint32
	I64  int64
	U64  uint64
	I128 nino.Int128
	U128 nino.UInt128
}

type Floats struct {
	F16 nino.Half
	F32 float32
	F64 float64
}

type Scalars struct {
	Flag    bool
	Char    uint16
	Decimal nino.Decimal
	Guid    nino.Guid
	Span    nino.TimeSpan
	Date    nino.DateOnly
	Time    nino.TimeOnly
}

type Dates struct {
	Utc         nino.DateTime
	Local       nino.DateTime
	Unspecified nino.DateTime
	Offset      nino.DateTimeOffset
}

type Enums struct {
	A     E8
	B     EU8
	C     E16
	D     EU16
	E     E32
	F     EU32
	G     E64
	H     EU64
	Flags Permissions
	Wide  WideFlags
	Array []E32
}

type BigValues struct {
	Big     *big.Int
	Version *nino.Version
}

type Strings struct {
	Text *string
	Utf8 *string
}

type StringCollections struct {
	Array []*string
	List  []*string
	Utf8A *string
	Plain *string
	Utf8B *string
}

// ByteList is a List<byte>: the same bytes as a byte[], but a list of numbers in the corpus's JSON.
type ByteList []byte

type Bytes struct {
	Array  []byte
	List   ByteList
	Signed []int8
}

// ---------------------------------------------------------------- objects and structs

type Point struct {
	X, Y int32
}

// Vec2 is raw: 8 bytes.
type Vec2 struct {
	X, Y float32
}

// Padded is raw: A at 0, B at 8, 16 bytes.
type Padded struct {
	A uint8
	B int64
}

// Mixed is raw: A at 0, B at 2, C at 4, D at 8, 12 bytes.
type Mixed struct {
	A uint8
	B int16
	C int32
	D uint8
}

// Packed is raw with Pack = 1: A at 0, B at 1, C at 9, 11 bytes.
type Packed struct {
	A uint8
	B int64
	C int16
}

// Sized is raw with Size = 16: A at 0.
type Sized struct {
	A int32
}

// Nested is raw: Inner at 0, Position at 16, Kind at 24, Flag at 25, 32 bytes.
type Nested struct {
	Inner    Padded
	Position Vec2
	Kind     EU8
	Flag     bool
}

// Label is a struct that is not raw (it holds a string): written member by member.
type Label struct {
	Name   *string
	Weight int32
}

// Tuple2 is a ValueTuple of two items.
type Tuple2[A, B any] struct {
	Item1 A
	Item2 B
}

type Structs struct {
	Vec        Vec2
	Padded     Padded
	Mixed      Mixed
	Packed     Packed
	Sized      Sized
	Nested     Nested
	Label      Label
	VecArray   []Vec2
	PaddedList []Padded
	MixedArray []Mixed
	Labels     []Label
	RawTuple   Tuple2[uint8, int64]
	Tuple      Tuple2[int32, *string]
	Pair       nino.Entry[*string, Vec2]
}

type Outer struct {
	Name   *string
	Point  *Point
	Middle *Middle
}

type Middle struct {
	First  *Point
	Second *Point
	Inner  *Inner
}

type Inner struct {
	Value int64
	Text  *string
	Point *Point
}

// Grid is a two-dimensional array: its lengths and its items in row-major order.
type Grid[T any] struct {
	Lengths [2]int
	Items   []T
}

type Collections struct {
	Ints          []int32
	IntList       []int32
	IntSet        []int32
	Longs         []int64
	Doubles       []float64
	Bools         []bool
	Chars         []uint16
	Strings       []*string
	StringSet     []*string
	Points        []*Point
	PointList     []*Point
	IntMap        []nino.Entry[int32, int32]
	StringMap     []nino.Entry[string, *string]
	PointMap      []nino.Entry[string, *Point]
	ListMap       []nino.Entry[int32, []*string]
	SortedInts    []nino.Entry[int32, *string]
	SortedStrings []nino.Entry[string, int32]
	Nested        [][]int32
	Jagged        [][]int32
	Grid          *Grid[int32]
	StringGrid    *Grid[*string]
}

type Nullables struct {
	Int         *int32
	Long        *int64
	Double      *float64
	Bool        *bool
	Guid        *nino.Guid
	Enum        *E16
	Vec         *Vec2
	Padded      *Padded
	Label       *Label
	IntArray    []*int32
	LongList    []*int64
	PaddedArray []*Padded
	Map         []nino.Entry[int32, *int32]
}

// ---------------------------------------------------------------- version tolerant and keyed

type TolerantV1 struct {
	A int32
	B *string
}

type TolerantV2 struct {
	A int32
	B *string
	C int64
	D []int32
}

type TolerantEmpty struct{}

// TolerantBase is a non-sealed class: written behind a type id, as itself or as a TolerantLeaf.
type TolerantBase struct {
	A int32
}

type TolerantLeaf struct {
	A int32
	B int32
}

// TolerantBaseType is a value of a slot declared as TolerantBase.
type TolerantBaseType interface {
	nino.Typed
	tolerantBase()
}

type TolerantHolder struct {
	First       *TolerantV1
	Polymorphic TolerantBaseType
	Empty       *TolerantEmpty
	After       int32
}

type KeyedV1 struct {
	Health int32
	Name   *string
	Items  []int32
	Score  int64
}

type KeyedV2 struct {
	Score int64
	Name  *string
	Hp    int32
	Level int32
}

type KeyedStruct struct {
	A int32
	B float64
}

type KeyedHolder struct {
	Keyed  *KeyedV1
	Struct KeyedStruct
	Array  []*KeyedV1
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
	Name  *string
	Bones int32
}

type Puppy struct {
	Name   *string
	Bones  int32
	Sleepy bool
}

type Cat struct {
	Name  *string
	Lives float32
}

// BoxInt32 is Box<int>; the generic class's closed types each get a Go type.
type BoxInt32 struct {
	Name  *string
	Value int32
}

// BoxString is Box<string>.
type BoxString struct {
	Name  *string
	Value *string
}

// BoxPoints is Box<List<Point>>.
type BoxPoints struct {
	Name  *string
	Value []*Point
}

// BoxVec2 is Box<Vec2>.
type BoxVec2 struct {
	Name  *string
	Value Vec2
}

// Shape is a value of a slot declared as the interface IShape.
type Shape interface {
	nino.Typed
	shape()
}

type Square struct {
	Area float64
	Side float64
}

type Circle struct {
	Area   float64
	Radius float64
}

// Plain is a non-sealed class without subtypes: still written behind a type id.
type Plain struct {
	Value int32
}

type Zoo struct {
	Star     Animal
	Dog      DogType
	Animals  []Animal
	Cages    []Animal
	Shape    Shape
	Shapes   []Shape
	Plain    *Plain
	Anything nino.Typed
	ByName   []nino.Entry[string, Animal]
}

// ---------------------------------------------------------------- deep nesting

type Chain struct {
	Value int32
	Next  *Chain
}

type TolerantChain struct {
	Value int32
	Next  *TolerantChain
}

type KeyedChain struct {
	Value int32
	Next  *KeyedChain
}

type TreeNode struct {
	Value    int32
	Children []*TreeNode
}

// Link is a value of a slot declared as the abstract class Link.
type Link interface {
	nino.Typed
	link()
}

type RedLink struct {
	Value int32
	Next  Link
}

type BlueLink struct {
	Value int32
	Next  Link
	Note  *string
}

// RecordChain is a record: .NET builds it through its constructor, so its bodies are never deferred.
type RecordChain struct {
	Value int32
	Next  *RecordChain
}

type TolerantRoot struct {
	Name  *string
	Chain *Chain
	After int32
}

// ---------------------------------------------------------------- self-nesting collections

// Tree is a List<Tree>.
type Tree []Tree

// DictTree is a Dictionary<string, DictTree>.
type DictTree []nino.Entry[string, DictTree]

type TreeHolder struct {
	Tree     Tree
	DictTree DictTree
}

// ---------------------------------------------------------------- everything empty

type Empty struct{}

// EmptyStruct is raw, and written as the one byte .NET gives an empty struct.
type EmptyStruct struct{}

type Empties struct {
	Class   *Empty
	Struct  EmptyStruct
	Structs []EmptyStruct
	Classes []*Empty
}

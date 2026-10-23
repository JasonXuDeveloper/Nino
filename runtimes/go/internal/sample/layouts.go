// Code in the style a generator emits: each type's NinoLayout returns the layout .NET publishes for it
// (NinoTypeLayout, from the corpus), so the struct tags are checked against it when the type's plan is built.

package sample

func (BigValues) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xD56E77DA; members=Big:System.Numerics.BigInteger:bigint;Version:System.Version:version"
}

func (BlueLink) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xA9C583C8; members=Value:int:raw(4);Next:Conformance.Link:type(Conformance.Link);Note:string:string"
}

func (Bytes) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x40CE351F; members=Array:byte[]:seq(raw(1));List:System.Collections.Generic.List<byte>:seq(raw(1));Signed:sbyte[]:seq(raw(1))"
}

func (Cat) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xAD792380; members=Name:string:string;Lives:float:raw(4)"
}

func (Chain) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x8147BA33; members=Value:int:raw(4);Next:Conformance.Chain:type(Conformance.Chain)"
}

func (Circle) NinoLayout() string {
	return "format=Positional; header=TypeId; trackReferences=false; raw=false; typeId=0xD0A6E3F4; members=Area:double:raw(8);Radius:double:raw(8)"
}

func (Collections) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xFECE6DDB; members=Ints:int[]:seq(raw(4));IntList:System.Collections.Generic.List<int>:seq(raw(4));IntSet:System.Collections.Generic.HashSet<int>:seq(raw(4));Longs:long[]:seq(raw(8));Doubles:double[]:seq(raw(8));Bools:bool[]:seq(raw(1));Chars:char[]:seq(raw(2));Strings:System.Collections.Generic.List<string>:seq(string);StringSet:System.Collections.Generic.HashSet<string>:seq(string);Points:Conformance.Point[]:seq(type(Conformance.Point));PointList:System.Collections.Generic.List<Conformance.Point>:seq(type(Conformance.Point));IntMap:System.Collections.Generic.Dictionary<int, int>:map(raw(4),raw(4));StringMap:System.Collections.Generic.Dictionary<string, string>:map(string,string);PointMap:System.Collections.Generic.Dictionary<string, Conformance.Point>:map(string,type(Conformance.Point));ListMap:System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>:map(raw(4),seq(string));SortedInts:System.Collections.Generic.SortedDictionary<int, string>:map(raw(4),string);SortedStrings:System.Collections.Generic.SortedDictionary<string, int>:map(string,raw(4));Nested:System.Collections.Generic.List<System.Collections.Generic.List<int>>:seq(seq(raw(4)));Jagged:int[][]:seq(seq(raw(4)));Grid:int[,]:grid(2,raw(4));StringGrid:string[,]:grid(2,string)"
}

func (Dates) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x004F73C1; members=Utc:System.DateTime:raw(8);Local:System.DateTime:raw(8);Unspecified:System.DateTime:raw(8);Offset:System.DateTimeOffset:datetimeoffset"
}

func (Dog) NinoLayout() string {
	return "format=Positional; header=TypeId; trackReferences=false; raw=false; typeId=0x69ACA68A; members=Name:string:string;Bones:int:raw(4)"
}

func (Empties) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xF6777155; members=Class:Conformance.Empty:type(Conformance.Empty);Struct:Conformance.EmptyStruct:raw;Structs:Conformance.EmptyStruct[]:seq(raw);Classes:System.Collections.Generic.List<Conformance.Empty>:seq(type(Conformance.Empty))"
}

func (Empty) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x0C95741B; members="
}

func (EmptyStruct) NinoLayout() string {
	return "format=Positional; header=None; trackReferences=false; raw=true; typeId=0x18A30154; members=raw"
}

func (Enums) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x47EFBBD6; members=A:Conformance.E8:raw(1);B:Conformance.EU8:raw(1);C:Conformance.E16:raw(2);D:Conformance.EU16:raw(2);E:Conformance.E32:raw(4);F:Conformance.EU32:raw(4);G:Conformance.E64:raw(8);H:Conformance.EU64:raw(8);Flags:Conformance.Permissions:raw(1);Wide:Conformance.WideFlags:raw(8);Array:Conformance.E32[]:seq(raw(4))"
}

func (Floats) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x3504C24D; members=F16:System.Half:raw(2);F32:float:raw(4);F64:double:raw(8)"
}

func (Inner) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x8EDDB9E8; members=Value:long:raw(8);Text:string:string;Point:Conformance.Point:type(Conformance.Point)"
}

func (Integers) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x14BD31D3; members=I8:sbyte:raw(1);U8:byte:raw(1);I16:short:raw(2);U16:ushort:raw(2);I32:int:raw(4);U32:uint:raw(4);I64:long:raw(8);U64:ulong:raw(8);I128:System.Int128:raw(16){_lower:ulong@0:raw(8),_upper:ulong@8:raw(8)};U128:System.UInt128:raw(16){_lower:ulong@0:raw(8),_upper:ulong@8:raw(8)}"
}

func (KeyedChain) NinoLayout() string {
	return "format=Keyed; header=NullFlag; trackReferences=false; raw=false; typeId=0xCBFD8A55; members=Value:int:raw(4):key=0xC373233E;Next:Conformance.KeyedChain:type(Conformance.KeyedChain):key=0xC9A6113A"
}

func (KeyedHolder) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x7FBDA208; members=Keyed:Conformance.KeyedV1:type(Conformance.KeyedV1);Struct:Conformance.KeyedStruct:type(Conformance.KeyedStruct);Array:Conformance.KeyedV1[]:seq(type(Conformance.KeyedV1))"
}

func (KeyedStruct) NinoLayout() string {
	return "format=Keyed; header=None; trackReferences=false; raw=false; typeId=0x64822AB7; members=A:int:raw(4):key=0x6AD58324;B:double:raw(8):key=0xDCDCF25F"
}

func (KeyedV1) NinoLayout() string {
	return "format=Keyed; header=NullFlag; trackReferences=false; raw=false; typeId=0x8F865207; members=Health:int:raw(4):key=0x99A16761;Name:string:string:key=0x3085B3E2;Items:System.Collections.Generic.List<int>:seq(raw(4)):key=0xDCA8A053;Score:long:raw(8):key=0x044AB14D"
}

func (KeyedV2) NinoLayout() string {
	return "format=Keyed; header=NullFlag; trackReferences=false; raw=false; typeId=0x1D7EE2CC; members=Score:long:raw(8):key=0x044AB14D;Name:string:string:key=0x3085B3E2;Hp:int:raw(4):key=0xA17135AD:former=0x99A16761;Level:int:raw(4):key=0xEAA8149B"
}

func (Label) NinoLayout() string {
	return "format=Positional; header=None; trackReferences=false; raw=false; typeId=0x344F08B8; members=Name:string:string;Weight:int:raw(4)"
}

func (Middle) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xD25B7AB3; members=First:Conformance.Point:type(Conformance.Point);Second:Conformance.Point:type(Conformance.Point);Inner:Conformance.Inner:type(Conformance.Inner)"
}

func (Mixed) NinoLayout() string {
	return "format=Positional; header=None; trackReferences=false; raw=true; typeId=0x0E07F8DB; members=raw(12){A:byte@0:raw(1),B:short@2:raw(2),C:int@4:raw(4),D:byte@8:raw(1)}"
}

func (Nested) NinoLayout() string {
	return "format=Positional; header=None; trackReferences=false; raw=true; typeId=0x062D377B; members=raw(32){Inner:Conformance.Padded@0:raw(16){A:byte@0:raw(1),B:long@8:raw(8)},Position:Conformance.Vec2@16:raw(8){X:float@0:raw(4),Y:float@4:raw(4)},Kind:Conformance.EU8@24:raw(1),Flag:bool@25:raw(1)}"
}

func (Nullables) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x3920173C; members=Int:System.Nullable<int>:nullable(raw(4));Long:System.Nullable<long>:nullable(raw(8));Double:System.Nullable<double>:nullable(raw(8));Bool:System.Nullable<bool>:nullable(raw(1));Guid:System.Nullable<System.Guid>:nullable(raw(16));Enum:System.Nullable<Conformance.E16>:nullable(raw(2));Vec:System.Nullable<Conformance.Vec2>:nullable(raw(8){X:float@0:raw(4),Y:float@4:raw(4)});Padded:System.Nullable<Conformance.Padded>:nullable(raw(16){A:byte@0:raw(1),B:long@8:raw(8)});Label:System.Nullable<Conformance.Label>:nullable(type(Conformance.Label));IntArray:System.Nullable<int>[]:seq(raw(8){hasValue:bool@0:raw(1),value:int@4:raw(4)});LongList:System.Collections.Generic.List<System.Nullable<long>>:seq(raw(16){hasValue:bool@0:raw(1),value:long@8:raw(8)});PaddedArray:System.Nullable<Conformance.Padded>[]:seq(raw(24){hasValue:bool@0:raw(1),value:Conformance.Padded@8:raw(16){A:byte@0:raw(1),B:long@8:raw(8)}});Map:System.Collections.Generic.Dictionary<int, System.Nullable<int>>:map(raw(4),nullable(raw(4)))"
}

func (Outer) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x3F58E13F; members=Name:string:string;Point:Conformance.Point:type(Conformance.Point);Middle:Conformance.Middle:type(Conformance.Middle)"
}

func (Packed) NinoLayout() string {
	return "format=Positional; header=None; trackReferences=false; raw=true; typeId=0xEF24AAA6; members=raw(11){A:byte@0:raw(1),B:long@1:raw(8),C:short@9:raw(2)}"
}

func (Padded) NinoLayout() string {
	return "format=Positional; header=None; trackReferences=false; raw=true; typeId=0x2ACAE976; members=raw(16){A:byte@0:raw(1),B:long@8:raw(8)}"
}

func (Plain) NinoLayout() string {
	return "format=Positional; header=TypeId; trackReferences=false; raw=false; typeId=0x3124185C; members=Value:int:raw(4)"
}

func (Point) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x964B64BA; members=X:int:raw(4);Y:int:raw(4)"
}

func (Puppy) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x94310DEA; members=Name:string:string;Bones:int:raw(4);Sleepy:bool:raw(1)"
}

func (RecordChain) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x20F10406; deferrable=false; members=Value:int:raw(4);Next:Conformance.RecordChain:type(Conformance.RecordChain)"
}

func (RedLink) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x9F90173D; members=Value:int:raw(4);Next:Conformance.Link:type(Conformance.Link)"
}

func (Scalars) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xD2727283; members=Flag:bool:raw(1);Char:char:raw(2);Decimal:decimal:raw(16);Guid:System.Guid:raw(16);Span:System.TimeSpan:raw(8);Date:System.DateOnly:raw(4);Time:System.TimeOnly:raw(8)"
}

func (Sized) NinoLayout() string {
	return "format=Positional; header=None; trackReferences=false; raw=true; typeId=0xB3C0D1F7; members=raw(16){A:int@0:raw(4)}"
}

func (Square) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x844A4BA7; members=Area:double:raw(8);Side:double:raw(8)"
}

func (StringCollections) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x744CD3F6; members=Array:string[]:seq(string);List:System.Collections.Generic.List<string>:seq(string);Utf8A:string:utf8;Plain:string:string;Utf8B:string:utf8"
}

func (Strings) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xCDBD0018; members=Text:string:string;Utf8:string:utf8"
}

func (Structs) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x24768402; members=Vec:Conformance.Vec2:raw(8){X:float@0:raw(4),Y:float@4:raw(4)};Padded:Conformance.Padded:raw(16){A:byte@0:raw(1),B:long@8:raw(8)};Mixed:Conformance.Mixed:raw(12){A:byte@0:raw(1),B:short@2:raw(2),C:int@4:raw(4),D:byte@8:raw(1)};Packed:Conformance.Packed:raw(11){A:byte@0:raw(1),B:long@1:raw(8),C:short@9:raw(2)};Sized:Conformance.Sized:raw(16){A:int@0:raw(4)};Nested:Conformance.Nested:raw(32){Inner:Conformance.Padded@0:raw(16){A:byte@0:raw(1),B:long@8:raw(8)},Position:Conformance.Vec2@16:raw(8){X:float@0:raw(4),Y:float@4:raw(4)},Kind:Conformance.EU8@24:raw(1),Flag:bool@25:raw(1)};Label:Conformance.Label:type(Conformance.Label);VecArray:Conformance.Vec2[]:seq(raw(8){X:float@0:raw(4),Y:float@4:raw(4)});PaddedList:System.Collections.Generic.List<Conformance.Padded>:seq(raw(16){A:byte@0:raw(1),B:long@8:raw(8)});MixedArray:Conformance.Mixed[]:seq(raw(12){A:byte@0:raw(1),B:short@2:raw(2),C:int@4:raw(4),D:byte@8:raw(1)});Labels:Conformance.Label[]:seq(type(Conformance.Label));RawTuple:System.ValueTuple<byte, long>:tuple(raw(1),raw(8));Tuple:System.ValueTuple<int, string>:tuple(raw(4),string);Pair:System.Collections.Generic.KeyValuePair<string, Conformance.Vec2>:pair(string,raw(8){X:float@0:raw(4),Y:float@4:raw(4)})"
}

func (TolerantBase) NinoLayout() string {
	return "format=VersionTolerant; header=TypeId; trackReferences=false; raw=false; typeId=0x9536FE88; members=A:int:raw(4)"
}

func (TolerantChain) NinoLayout() string {
	return "format=VersionTolerant; header=NullFlag; trackReferences=false; raw=false; typeId=0x18DAD008; members=Value:int:raw(4);Next:Conformance.TolerantChain:type(Conformance.TolerantChain)"
}

func (TolerantEmpty) NinoLayout() string {
	return "format=VersionTolerant; header=NullFlag; trackReferences=false; raw=false; typeId=0x85A223A0; members="
}

func (TolerantHolder) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x2DA994ED; members=First:Conformance.TolerantV1:type(Conformance.TolerantV1);Polymorphic:Conformance.TolerantBase:type(Conformance.TolerantBase);Empty:Conformance.TolerantEmpty:type(Conformance.TolerantEmpty);After:int:raw(4)"
}

func (TolerantLeaf) NinoLayout() string {
	return "format=VersionTolerant; header=NullFlag; trackReferences=false; raw=false; typeId=0x6B83150B; members=A:int:raw(4);B:int:raw(4)"
}

func (TolerantRoot) NinoLayout() string {
	return "format=VersionTolerant; header=NullFlag; trackReferences=false; raw=false; typeId=0xF7F6B6D3; members=Name:string:string;Chain:Conformance.Chain:type(Conformance.Chain);After:int:raw(4)"
}

func (TolerantV1) NinoLayout() string {
	return "format=VersionTolerant; header=NullFlag; trackReferences=false; raw=false; typeId=0x1DBD9212; members=A:int:raw(4);B:string:string"
}

func (TolerantV2) NinoLayout() string {
	return "format=VersionTolerant; header=NullFlag; trackReferences=false; raw=false; typeId=0xF7BB17A9; members=A:int:raw(4);B:string:string;C:long:raw(8);D:System.Collections.Generic.List<int>:seq(raw(4))"
}

func (TreeHolder) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xA20A5480; members=Tree:Conformance.Tree:seq(type(Conformance.Tree));DictTree:Conformance.DictTree:map(string,type(Conformance.DictTree))"
}

func (TreeNode) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x760566C0; members=Value:int:raw(4);Children:System.Collections.Generic.List<Conformance.TreeNode>:seq(type(Conformance.TreeNode))"
}

func (Vec2) NinoLayout() string {
	return "format=Positional; header=None; trackReferences=false; raw=true; typeId=0x53124224; members=raw(8){X:float@0:raw(4),Y:float@4:raw(4)}"
}

func (Zoo) NinoLayout() string {
	return "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x6E8C85FC; members=Star:Conformance.Animal:type(Conformance.Animal);Dog:Conformance.Dog:type(Conformance.Dog);Animals:System.Collections.Generic.List<Conformance.Animal>:seq(type(Conformance.Animal));Cages:Conformance.Animal[]:seq(type(Conformance.Animal));Shape:Conformance.IShape:type(Conformance.IShape);Shapes:System.Collections.Generic.List<Conformance.IShape>:seq(type(Conformance.IShape));Plain:Conformance.Plain:type(Conformance.Plain);Anything:object:object;ByName:System.Collections.Generic.Dictionary<string, Conformance.Animal>:map(string,type(Conformance.Animal))"
}

// BoxInt32 is Box<T> closed: .NET publishes the generic definition only (typeId 0, member type(T)).
func (BoxInt32) NinoLayout() string { return "format=Positional; header=NullFlag" }

// BoxString is Box<T> closed: .NET publishes the generic definition only (typeId 0, member type(T)).
func (BoxString) NinoLayout() string { return "format=Positional; header=NullFlag" }

// BoxPoints is Box<T> closed: .NET publishes the generic definition only (typeId 0, member type(T)).
func (BoxPoints) NinoLayout() string { return "format=Positional; header=NullFlag" }

// BoxVec2 is Box<T> closed: .NET publishes the generic definition only (typeId 0, member type(T)).
func (BoxVec2) NinoLayout() string { return "format=Positional; header=NullFlag" }

// Tuple2 is a ValueTuple: its items in order, never raw (spec §6.6).
func (Tuple2[A, B]) NinoLayout() string { return "raw=false" }

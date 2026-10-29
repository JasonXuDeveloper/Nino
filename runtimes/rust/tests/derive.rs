//! `#[derive(NinoRead, NinoWrite)]` against the conformance corpus: the corpus's plain, version-tolerant, keyed,
//! raw and polymorphic types declared with the derive, each checked at compile time against the layout the corpus
//! publishes for it, then every case of those types read and written back byte for byte, and every malicious case
//! and truncated prefix rejected.

#![allow(clippy::unwrap_used)]

use indexmap::{IndexMap, IndexSet};
use nino::{
    BigInteger, DateOnly, DateTime, DateTimeOffset, Decimal, Grid, Guid, Half, NinoRead, NinoWrite, RawNullables,
    TimeOnly, TimeSpan, Version, WideString,
};
use std::path::{Path, PathBuf};

/// Adds the serde derives (behind the `serde` feature) the templates add to generated types, with their naming
/// policy.
macro_rules! nino_types {
    ($($item:item)*) => {$(
        #[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize), serde(rename_all = "camelCase"))]
        $item
    )*};
}

nino_types! {

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x14BD31D3; deferrable=true; members=I8:sbyte:raw(1);U8:byte:raw(1);I16:short:raw(2);U16:ushort:raw(2);I32:int:raw(4);U32:uint:raw(4);I64:long:raw(8);U64:ulong:raw(8);I128:System.Int128:raw(16){_lower:ulong@0:raw(8),_upper:ulong@8:raw(8)};U128:System.UInt128:raw(16){_lower:ulong@0:raw(8),_upper:ulong@8:raw(8)}")]
pub struct Integers {
    pub i8: i8,
    pub u8: u8,
    pub i16: i16,
    pub u16: u16,
    pub i32: i32,
    pub u32: u32,
    pub i64: i64,
    pub u64: u64,
    pub i128: i128,
    pub u128: u128,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x3504C24D; deferrable=true; members=F16:System.Half:raw(2);F32:float:raw(4);F64:double:raw(8)")]
pub struct Floats {
    pub f16: Half,
    pub f32: f32,
    pub f64: f64,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xD2727283; deferrable=true; members=Flag:bool:raw(1);Char:char:raw(2);Decimal:decimal:raw(16);Guid:System.Guid:raw(16);Span:System.TimeSpan:raw(8);Date:System.DateOnly:raw(4);Time:System.TimeOnly:raw(8)")]
pub struct Scalars {
    pub flag: bool,
    /// A C# char: one UTF-16 code unit.
    pub char: u16,
    pub decimal: Decimal,
    pub guid: Guid,
    pub span: TimeSpan,
    pub date: DateOnly,
    pub time: TimeOnly,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x004F73C1; deferrable=true; members=Utc:System.DateTime:raw(8);Local:System.DateTime:raw(8);Unspecified:System.DateTime:raw(8);Offset:System.DateTimeOffset:datetimeoffset")]
pub struct Dates {
    pub utc: DateTime,
    pub local: DateTime,
    pub unspecified: DateTime,
    pub offset: DateTimeOffset,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x47EFBBD6; deferrable=true; members=A:Conformance.E8:raw(1);B:Conformance.EU8:raw(1);C:Conformance.E16:raw(2);D:Conformance.EU16:raw(2);E:Conformance.E32:raw(4);F:Conformance.EU32:raw(4);G:Conformance.E64:raw(8);H:Conformance.EU64:raw(8);Flags:Conformance.Permissions:raw(1);Wide:Conformance.WideFlags:raw(8);Array:Conformance.E32[]:seq(raw(4))")]
pub struct Enums {
    pub a: i8,
    pub b: u8,
    pub c: i16,
    pub d: u16,
    pub e: i32,
    pub f: u32,
    pub g: i64,
    pub h: u64,
    pub flags: u8,
    pub wide: u64,
    pub array: Option<Vec<i32>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xD56E77DA; deferrable=true; members=Big:System.Numerics.BigInteger:bigint;Version:System.Version:version")]
pub struct BigValues {
    pub big: BigInteger,
    pub version: Option<Version>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xCDBD0018; deferrable=true; members=Text:string:string;Utf8:string:utf8")]
pub struct Strings {
    /// Lone surrogates kept.
    pub text: Option<WideString>,
    #[nino(utf8)]
    pub utf8: Option<String>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x744CD3F6; deferrable=true; members=Array:string[]:seq(string);List:System.Collections.Generic.List<string>:seq(string);Utf8A:string:utf8;Plain:string:string;Utf8B:string:utf8")]
pub struct StringCollections {
    pub array: Option<Vec<Option<WideString>>>,
    pub list: Option<Vec<Option<String>>>,
    #[nino(utf8)]
    pub utf8_a: Option<String>,
    pub plain: Option<String>,
    #[nino(utf8)]
    pub utf8_b: Option<String>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x40CE351F; deferrable=true; members=Array:byte[]:seq(raw(1));List:System.Collections.Generic.List<byte>:seq(raw(1));Signed:sbyte[]:seq(raw(1))")]
pub struct Bytes {
    pub array: Option<Vec<u8>>,
    pub list: Option<Vec<u8>>,
    pub signed: Option<Vec<i8>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x964B64BA; deferrable=true; members=X:int:raw(4);Y:int:raw(4)")]
pub struct Point {
    pub x: i32,
    pub y: i32,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x8EDDB9E8; deferrable=true; members=Value:long:raw(8);Text:string:string;Point:Conformance.Point:type(Conformance.Point)")]
pub struct Inner {
    pub value: i64,
    pub text: Option<String>,
    pub point: Option<Box<Point>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xD25B7AB3; deferrable=true; members=First:Conformance.Point:type(Conformance.Point);Second:Conformance.Point:type(Conformance.Point);Inner:Conformance.Inner:type(Conformance.Inner)")]
pub struct Middle {
    pub first: Option<Box<Point>>,
    pub second: Option<Box<Point>>,
    pub inner: Option<Box<Inner>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x3F58E13F; deferrable=true; members=Name:string:string;Point:Conformance.Point:type(Conformance.Point);Middle:Conformance.Middle:type(Conformance.Middle)")]
pub struct Outer {
    pub name: Option<String>,
    pub point: Option<Box<Point>>,
    pub middle: Option<Box<Middle>>,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=None; trackReferences=false; raw=true; typeId=0x53124224; deferrable=false; members=raw(8){X:float@0:raw(4),Y:float@4:raw(4)}")]
pub struct Vec2 {
    pub x: f32,
    pub y: f32,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=None; trackReferences=false; raw=true; typeId=0x2ACAE976; deferrable=false; members=raw(16){A:byte@0:raw(1),B:long@8:raw(8)}")]
pub struct Padded {
    pub a: u8,
    pub b: i64,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=None; trackReferences=false; raw=true; typeId=0x0E07F8DB; deferrable=false; members=raw(12){A:byte@0:raw(1),B:short@2:raw(2),C:int@4:raw(4),D:byte@8:raw(1)}")]
pub struct Mixed {
    pub a: u8,
    pub b: i16,
    pub c: i32,
    pub d: u8,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=None; trackReferences=false; raw=true; typeId=0xEF24AAA6; deferrable=false; members=raw(11){A:byte@0:raw(1),B:long@1:raw(8),C:short@9:raw(2)}", pack = 1)]
pub struct Packed {
    pub a: u8,
    pub b: i64,
    pub c: i16,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=None; trackReferences=false; raw=true; typeId=0xB3C0D1F7; deferrable=false; members=raw(16){A:int@0:raw(4)}", size = 16)]
pub struct Sized {
    pub a: i32,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=None; trackReferences=false; raw=true; typeId=0x062D377B; deferrable=false; members=raw(32){Inner:Conformance.Padded@0:raw(16){A:byte@0:raw(1),B:long@8:raw(8)},Position:Conformance.Vec2@16:raw(8){X:float@0:raw(4),Y:float@4:raw(4)},Kind:Conformance.EU8@24:raw(1),Flag:bool@25:raw(1)}")]
pub struct Nested {
    pub inner: Padded,
    pub position: Vec2,
    pub kind: u8,
    pub flag: bool,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=None; trackReferences=false; raw=true; typeId=0x18A30154; deferrable=false; members=raw")]
pub struct EmptyStruct {}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=None; trackReferences=false; raw=false; typeId=0x344F08B8; deferrable=false; members=Name:string:string;Weight:int:raw(4)")]
pub struct Label {
    pub name: Option<String>,
    pub weight: i32,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x24768402; deferrable=true; members=Vec:Conformance.Vec2:raw(8){X:float@0:raw(4),Y:float@4:raw(4)};Padded:Conformance.Padded:raw(16){A:byte@0:raw(1),B:long@8:raw(8)};Mixed:Conformance.Mixed:raw(12){A:byte@0:raw(1),B:short@2:raw(2),C:int@4:raw(4),D:byte@8:raw(1)};Packed:Conformance.Packed:raw(11){A:byte@0:raw(1),B:long@1:raw(8),C:short@9:raw(2)};Sized:Conformance.Sized:raw(16){A:int@0:raw(4)};Nested:Conformance.Nested:raw(32){Inner:Conformance.Padded@0:raw(16){A:byte@0:raw(1),B:long@8:raw(8)},Position:Conformance.Vec2@16:raw(8){X:float@0:raw(4),Y:float@4:raw(4)},Kind:Conformance.EU8@24:raw(1),Flag:bool@25:raw(1)};Label:Conformance.Label:type(Conformance.Label);VecArray:Conformance.Vec2[]:seq(raw(8){X:float@0:raw(4),Y:float@4:raw(4)});PaddedList:System.Collections.Generic.List<Conformance.Padded>:seq(raw(16){A:byte@0:raw(1),B:long@8:raw(8)});MixedArray:Conformance.Mixed[]:seq(raw(12){A:byte@0:raw(1),B:short@2:raw(2),C:int@4:raw(4),D:byte@8:raw(1)});Labels:Conformance.Label[]:seq(type(Conformance.Label));RawTuple:System.ValueTuple<byte, long>:tuple(raw(1),raw(8));Tuple:System.ValueTuple<int, string>:tuple(raw(4),string);Pair:System.Collections.Generic.KeyValuePair<string, Conformance.Vec2>:pair(string,raw(8){X:float@0:raw(4),Y:float@4:raw(4)})")]
pub struct Structs {
    pub vec: Vec2,
    pub padded: Padded,
    pub mixed: Mixed,
    pub packed: Packed,
    pub sized: Sized,
    pub nested: Nested,
    pub label: Label,
    pub vec_array: Option<Vec<Vec2>>,
    pub padded_list: Option<Vec<Padded>>,
    pub mixed_array: Option<Vec<Mixed>>,
    pub labels: Option<Vec<Label>>,
    pub raw_tuple: (u8, i64),
    pub tuple: (i32, Option<String>),
    pub pair: (Option<String>, Vec2),
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x3920173C; deferrable=true; members=Int:System.Nullable<int>:nullable(raw(4));Long:System.Nullable<long>:nullable(raw(8));Double:System.Nullable<double>:nullable(raw(8));Bool:System.Nullable<bool>:nullable(raw(1));Guid:System.Nullable<System.Guid>:nullable(raw(16));Enum:System.Nullable<Conformance.E16>:nullable(raw(2));Vec:System.Nullable<Conformance.Vec2>:nullable(raw(8){X:float@0:raw(4),Y:float@4:raw(4)});Padded:System.Nullable<Conformance.Padded>:nullable(raw(16){A:byte@0:raw(1),B:long@8:raw(8)});Label:System.Nullable<Conformance.Label>:nullable(type(Conformance.Label));IntArray:System.Nullable<int>[]:seq(raw(8){hasValue:bool@0:raw(1),value:int@4:raw(4)});LongList:System.Collections.Generic.List<System.Nullable<long>>:seq(raw(16){hasValue:bool@0:raw(1),value:long@8:raw(8)});PaddedArray:System.Nullable<Conformance.Padded>[]:seq(raw(24){hasValue:bool@0:raw(1),value:Conformance.Padded@8:raw(16){A:byte@0:raw(1),B:long@8:raw(8)}});Map:System.Collections.Generic.Dictionary<int, System.Nullable<int>>:map(raw(4),nullable(raw(4)))")]
pub struct Nullables {
    pub int: Option<i32>,
    pub long: Option<i64>,
    pub double: Option<f64>,
    pub bool: Option<bool>,
    pub guid: Option<Guid>,
    pub r#enum: Option<i16>,
    pub vec: Option<Vec2>,
    pub padded: Option<Padded>,
    pub label: Option<Label>,
    pub int_array: Option<RawNullables<i32>>,
    pub long_list: Option<RawNullables<i64>>,
    pub padded_array: Option<RawNullables<Padded>>,
    pub map: Option<IndexMap<i32, Option<i32>>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xFECE6DDB; deferrable=true; members=Ints:int[]:seq(raw(4));IntList:System.Collections.Generic.List<int>:seq(raw(4));IntSet:System.Collections.Generic.HashSet<int>:seq(raw(4));Longs:long[]:seq(raw(8));Doubles:double[]:seq(raw(8));Bools:bool[]:seq(raw(1));Chars:char[]:seq(raw(2));Strings:System.Collections.Generic.List<string>:seq(string);StringSet:System.Collections.Generic.HashSet<string>:seq(string);Points:Conformance.Point[]:seq(type(Conformance.Point));PointList:System.Collections.Generic.List<Conformance.Point>:seq(type(Conformance.Point));IntMap:System.Collections.Generic.Dictionary<int, int>:map(raw(4),raw(4));StringMap:System.Collections.Generic.Dictionary<string, string>:map(string,string);PointMap:System.Collections.Generic.Dictionary<string, Conformance.Point>:map(string,type(Conformance.Point));ListMap:System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>:map(raw(4),seq(string));SortedInts:System.Collections.Generic.SortedDictionary<int, string>:map(raw(4),string);SortedStrings:System.Collections.Generic.SortedDictionary<string, int>:map(string,raw(4));Nested:System.Collections.Generic.List<System.Collections.Generic.List<int>>:seq(seq(raw(4)));Jagged:int[][]:seq(seq(raw(4)));Grid:int[,]:grid(2,raw(4));StringGrid:string[,]:grid(2,string)")]
pub struct Collections {
    pub ints: Option<Vec<i32>>,
    pub int_list: Option<Vec<i32>>,
    pub int_set: Option<IndexSet<i32>>,
    pub longs: Option<Vec<i64>>,
    pub doubles: Option<Vec<f64>>,
    pub bools: Option<Vec<bool>>,
    pub chars: Option<Vec<u16>>,
    pub strings: Option<Vec<Option<String>>>,
    pub string_set: Option<IndexSet<Option<String>>>,
    pub points: Option<Vec<Option<Box<Point>>>>,
    pub point_list: Option<Vec<Option<Box<Point>>>>,
    pub int_map: Option<IndexMap<i32, i32>>,
    pub string_map: Option<IndexMap<String, Option<String>>>,
    pub point_map: Option<IndexMap<String, Option<Box<Point>>>>,
    pub list_map: Option<IndexMap<i32, Option<Vec<Option<String>>>>>,
    pub sorted_ints: Option<IndexMap<i32, Option<String>>>,
    pub sorted_strings: Option<IndexMap<String, i32>>,
    pub nested: Option<Vec<Option<Vec<i32>>>>,
    pub jagged: Option<Vec<Option<Vec<i32>>>>,
    pub grid: Option<Grid<i32, 2>>,
    pub string_grid: Option<Grid<Option<String>, 2>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x0C95741B; deferrable=true; members=")]
pub struct Empty {}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xF6777155; deferrable=true; members=Class:Conformance.Empty:type(Conformance.Empty);Struct:Conformance.EmptyStruct:raw;Structs:Conformance.EmptyStruct[]:seq(raw);Classes:System.Collections.Generic.List<Conformance.Empty>:seq(type(Conformance.Empty))")]
pub struct Empties {
    pub class: Option<Box<Empty>>,
    pub r#struct: EmptyStruct,
    pub structs: Option<Vec<EmptyStruct>>,
    pub classes: Option<Vec<Option<Box<Empty>>>>,
}

// ------------------------------------------------------------------ version tolerance

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=VersionTolerant; header=NullFlag; trackReferences=false; raw=false; typeId=0x1DBD9212; deferrable=true; members=A:int:raw(4);B:string:string")]
pub struct TolerantV1 {
    pub a: i32,
    pub b: Option<String>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=VersionTolerant; header=NullFlag; trackReferences=false; raw=false; typeId=0xF7BB17A9; deferrable=true; members=A:int:raw(4);B:string:string;C:long:raw(8);D:System.Collections.Generic.List<int>:seq(raw(4))")]
pub struct TolerantV2 {
    pub a: i32,
    pub b: Option<String>,
    pub c: i64,
    pub d: Option<Vec<i32>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=VersionTolerant; header=NullFlag; trackReferences=false; raw=false; typeId=0x85A223A0; deferrable=true; members=")]
pub struct TolerantEmpty {}

/// Not sealed: written behind its type id even as itself.
#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=VersionTolerant; header=TypeId; trackReferences=false; raw=false; typeId=0x9536FE88; deferrable=true; members=A:int:raw(4)")]
pub struct TolerantBase {
    pub a: i32,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=VersionTolerant; header=NullFlag; trackReferences=false; raw=false; typeId=0x6B83150B; deferrable=true; members=A:int:raw(4);B:int:raw(4)")]
pub struct TolerantLeaf {
    pub a: i32,
    pub b: i32,
}

/// A `TolerantBase` slot: the base or its subtype.
#[derive(Debug, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
pub enum TolerantBaseRef {
    Base(Box<TolerantBase>),
    Leaf(Box<TolerantLeaf>),
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x2DA994ED; deferrable=true; members=First:Conformance.TolerantV1:type(Conformance.TolerantV1);Polymorphic:Conformance.TolerantBase:type(Conformance.TolerantBase);Empty:Conformance.TolerantEmpty:type(Conformance.TolerantEmpty);After:int:raw(4)")]
pub struct TolerantHolder {
    pub first: Option<Box<TolerantV1>>,
    pub polymorphic: Option<TolerantBaseRef>,
    pub empty: Option<Box<TolerantEmpty>>,
    pub after: i32,
}

// ------------------------------------------------------------------ keyed

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Keyed; header=NullFlag; trackReferences=false; raw=false; typeId=0x8F865207; deferrable=true; members=Health:int:raw(4):key=0x99A16761;Name:string:string:key=0x3085B3E2;Items:System.Collections.Generic.List<int>:seq(raw(4)):key=0xDCA8A053;Score:long:raw(8):key=0x044AB14D")]
pub struct KeyedV1 {
    pub health: i32,
    pub name: Option<String>,
    pub items: Option<Vec<i32>>,
    pub score: i64,
}

/// KeyedV1 reordered, Health renamed to Hp (its old key read too), Items removed, Level added.
#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Keyed; header=NullFlag; trackReferences=false; raw=false; typeId=0x1D7EE2CC; deferrable=true; members=Score:long:raw(8):key=0x044AB14D;Name:string:string:key=0x3085B3E2;Hp:int:raw(4):key=0xA17135AD:former=0x99A16761;Level:int:raw(4):key=0xEAA8149B")]
pub struct KeyedV2 {
    pub score: i64,
    pub name: Option<String>,
    #[nino(former = "Health")]
    pub hp: i32,
    pub level: i32,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Keyed; header=None; trackReferences=false; raw=false; typeId=0x64822AB7; deferrable=false; members=A:int:raw(4):key=0x6AD58324;B:double:raw(8):key=0xDCDCF25F")]
pub struct KeyedStruct {
    pub a: i32,
    pub b: f64,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x7FBDA208; deferrable=true; members=Keyed:Conformance.KeyedV1:type(Conformance.KeyedV1);Struct:Conformance.KeyedStruct:type(Conformance.KeyedStruct);Array:Conformance.KeyedV1[]:seq(type(Conformance.KeyedV1))")]
pub struct KeyedHolder {
    pub keyed: Option<Box<KeyedV1>>,
    pub r#struct: KeyedStruct,
    pub array: Option<Vec<Option<Box<KeyedV1>>>>,
}

// ------------------------------------------------------------------ deep nesting

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x8147BA33; deferrable=true; members=Value:int:raw(4);Next:Conformance.Chain:type(Conformance.Chain)")]
pub struct Chain {
    pub value: i32,
    pub next: Option<Box<Chain>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=VersionTolerant; header=NullFlag; trackReferences=false; raw=false; typeId=0x18DAD008; deferrable=true; members=Value:int:raw(4);Next:Conformance.TolerantChain:type(Conformance.TolerantChain)")]
pub struct TolerantChain {
    pub value: i32,
    pub next: Option<Box<TolerantChain>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Keyed; header=NullFlag; trackReferences=false; raw=false; typeId=0xCBFD8A55; deferrable=true; members=Value:int:raw(4):key=0xC373233E;Next:Conformance.KeyedChain:type(Conformance.KeyedChain):key=0xC9A6113A")]
pub struct KeyedChain {
    pub value: i32,
    pub next: Option<Box<KeyedChain>>,
}

/// A record: built by its constructor in .NET, so never deferred.
#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x20F10406; deferrable=false; members=Value:int:raw(4);Next:Conformance.RecordChain:type(Conformance.RecordChain)")]
pub struct RecordChain {
    pub value: i32,
    pub next: Option<Box<RecordChain>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=VersionTolerant; header=NullFlag; trackReferences=false; raw=false; typeId=0xF7F6B6D3; deferrable=true; members=Name:string:string;Chain:Conformance.Chain:type(Conformance.Chain);After:int:raw(4)")]
pub struct TolerantRoot {
    pub name: Option<String>,
    pub chain: Option<Box<Chain>>,
    pub after: i32,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x760566C0; deferrable=true; members=Value:int:raw(4);Children:System.Collections.Generic.List<Conformance.TreeNode>:seq(type(Conformance.TreeNode))")]
pub struct TreeNode {
    pub value: i32,
    pub children: Option<Vec<Option<Box<TreeNode>>>>,
}

/// The abstract `Link`: one of its sealed subtypes, by type id.
#[derive(Debug, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
pub enum LinkRef {
    Red(Box<RedLink>),
    Blue(Box<BlueLink>),
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x9F90173D; deferrable=true; members=Value:int:raw(4);Next:Conformance.Link:type(Conformance.Link)")]
pub struct RedLink {
    pub value: i32,
    pub next: Option<LinkRef>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xA9C583C8; deferrable=true; members=Value:int:raw(4);Next:Conformance.Link:type(Conformance.Link);Note:string:string")]
pub struct BlueLink {
    pub value: i32,
    pub next: Option<LinkRef>,
    pub note: Option<String>,
}

// ------------------------------------------------------------------ the malicious cases' roots

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x694D6EF8; deferrable=true; members=Values:int[]:seq(raw(4))")]
pub struct IntArray {
    pub values: Option<Vec<i32>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xCECF34D2; deferrable=true; members=Values:System.Collections.Generic.List<long>:seq(raw(8))")]
pub struct LongList {
    pub values: Option<Vec<i64>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x1805EDC3; deferrable=true; members=Values:Conformance.Padded[]:seq(raw(16){A:byte@0:raw(1),B:long@8:raw(8)})")]
pub struct PaddedArray {
    pub values: Option<Vec<Padded>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x30796D4D; deferrable=true; members=Values:System.Collections.Generic.List<string>:seq(string)")]
pub struct StringList {
    pub values: Option<Vec<Option<String>>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x2EA152CC; deferrable=true; members=Values:System.Collections.Generic.List<Conformance.Point>:seq(type(Conformance.Point))")]
pub struct PointList {
    pub values: Option<Vec<Option<Box<Point>>>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xEE43CD01; deferrable=true; members=Values:byte[]:seq(raw(1))")]
pub struct ByteArray {
    pub values: Option<Vec<u8>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xD32CFD85; deferrable=true; members=Values:System.Collections.Generic.Dictionary<int, int>:map(raw(4),raw(4))")]
pub struct IntMap {
    pub values: Option<IndexMap<i32, i32>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x4B1A1F3B; deferrable=true; members=Values:System.Collections.Generic.Dictionary<string, int>:map(string,raw(4))")]
pub struct StringMap {
    pub values: Option<IndexMap<String, i32>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xAA98C54B; deferrable=true; members=Values:System.Collections.Generic.HashSet<int>:seq(raw(4))")]
pub struct IntSet {
    pub values: Option<IndexSet<i32>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x73FC2C95; deferrable=true; members=Values:System.Collections.Generic.HashSet<string>:seq(string)")]
pub struct StringSet {
    pub values: Option<IndexSet<Option<String>>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x3F694A3D; deferrable=true; members=Values:System.Collections.Generic.SortedDictionary<int, int>:map(raw(4),raw(4))")]
pub struct SortedMap {
    pub values: Option<IndexMap<i32, i32>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xF8F3033A; deferrable=true; members=Values:int[,]:grid(2,raw(4))")]
pub struct GridValue {
    pub values: Option<Grid<i32, 2>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x825A7C6C; deferrable=true; members=Value:string:string")]
pub struct TextValue {
    pub value: Option<String>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x49EFED16; deferrable=true; members=Value:string:utf8")]
pub struct Utf8Value {
    #[nino(utf8)]
    pub value: Option<String>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x65FCB927; deferrable=true; members=Value:bool:raw(1)")]
pub struct BoolValue {
    pub value: bool,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x2C38911D; deferrable=true; members=Values:bool[]:seq(raw(1))")]
pub struct BoolArray {
    pub values: Option<Vec<bool>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x3E8CF8A3; deferrable=true; members=Value:System.Nullable<int>:nullable(raw(4))")]
pub struct MaybeInt {
    pub value: Option<i32>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x436A69B2; deferrable=true; members=Values:System.Nullable<int>[]:seq(raw(8){hasValue:bool@0:raw(1),value:int@4:raw(4)})")]
pub struct MaybeIntArray {
    pub values: Option<RawNullables<i32>>,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0xA6B6AFE2; deferrable=true; members=Value:System.Numerics.BigInteger:bigint")]
pub struct BigSlot {
    pub value: BigInteger,
}

#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x9D692494; deferrable=true; members=Value:System.Version:version")]
pub struct VersionSlot {
    pub value: Option<Version>,
}

}

// ---------------------------------------------------------------------------------------------- the test

/// What a case's type is read as.
trait Case {
    fn run(&self, bytes: &[u8]) -> Option<Result<Vec<u8>, nino::Error>>;
}

struct As<T>(std::marker::PhantomData<T>);

impl<T: NinoRead + NinoWrite> Case for As<T> {
    fn run(&self, bytes: &[u8]) -> Option<Result<Vec<u8>, nino::Error>> {
        Some(nino::deserialize_exact::<T>(bytes).and_then(|v| nino::serialize(&v)))
    }
}

fn of<T: NinoRead + NinoWrite + 'static>() -> Box<dyn Case> {
    Box::new(As::<T>(std::marker::PhantomData))
}

fn case_type(name: &str) -> Option<Box<dyn Case>> {
    Some(match name {
        "Conformance.Integers" => of::<Option<Box<Integers>>>(),
        "Conformance.Floats" => of::<Option<Box<Floats>>>(),
        "Conformance.Scalars" => of::<Option<Box<Scalars>>>(),
        "Conformance.Dates" => of::<Option<Box<Dates>>>(),
        "Conformance.Enums" => of::<Option<Box<Enums>>>(),
        "Conformance.BigValues" => of::<Option<Box<BigValues>>>(),
        "Conformance.Strings" => of::<Option<Box<Strings>>>(),
        "Conformance.StringCollections" => of::<Option<Box<StringCollections>>>(),
        "Conformance.Bytes" => of::<Option<Box<Bytes>>>(),
        "Conformance.Outer" => of::<Option<Box<Outer>>>(),
        "Conformance.Structs" => of::<Option<Box<Structs>>>(),
        "Conformance.Nullables" => of::<Option<Box<Nullables>>>(),
        "Conformance.Collections" => of::<Option<Box<Collections>>>(),
        "Conformance.Empties" => of::<Option<Box<Empties>>>(),
        "Conformance.TolerantV1" => of::<Option<Box<TolerantV1>>>(),
        "Conformance.TolerantV2" => of::<Option<Box<TolerantV2>>>(),
        "Conformance.TolerantHolder" => of::<Option<Box<TolerantHolder>>>(),
        "Conformance.KeyedV1" => of::<Option<Box<KeyedV1>>>(),
        "Conformance.KeyedV2" => of::<Option<Box<KeyedV2>>>(),
        "Conformance.KeyedHolder" => of::<Option<Box<KeyedHolder>>>(),
        "Conformance.Chain" => of::<Option<Box<Chain>>>(),
        "Conformance.TolerantChain" => of::<Option<Box<TolerantChain>>>(),
        "Conformance.KeyedChain" => of::<Option<Box<KeyedChain>>>(),
        "Conformance.RecordChain" => of::<Option<Box<RecordChain>>>(),
        "Conformance.TolerantRoot" => of::<Option<Box<TolerantRoot>>>(),
        "Conformance.TreeNode" => of::<Option<Box<TreeNode>>>(),
        "Conformance.Link" => of::<Option<LinkRef>>(),
        "Conformance.Targets.IntArray" => of::<Option<Box<IntArray>>>(),
        "Conformance.Targets.LongList" => of::<Option<Box<LongList>>>(),
        "Conformance.Targets.PaddedArray" => of::<Option<Box<PaddedArray>>>(),
        "Conformance.Targets.StringList" => of::<Option<Box<StringList>>>(),
        "Conformance.Targets.PointList" => of::<Option<Box<PointList>>>(),
        "Conformance.Targets.ByteArray" => of::<Option<Box<ByteArray>>>(),
        "Conformance.Targets.IntMap" => of::<Option<Box<IntMap>>>(),
        "Conformance.Targets.StringMap" => of::<Option<Box<StringMap>>>(),
        "Conformance.Targets.IntSet" => of::<Option<Box<IntSet>>>(),
        "Conformance.Targets.StringSet" => of::<Option<Box<StringSet>>>(),
        "Conformance.Targets.SortedMap" => of::<Option<Box<SortedMap>>>(),
        "Conformance.Targets.Grid" => of::<Option<Box<GridValue>>>(),
        "Conformance.Targets.TextValue" => of::<Option<Box<TextValue>>>(),
        "Conformance.Targets.Utf8Value" => of::<Option<Box<Utf8Value>>>(),
        "Conformance.Targets.BoolValue" => of::<Option<Box<BoolValue>>>(),
        "Conformance.Targets.BoolArray" => of::<Option<Box<BoolArray>>>(),
        "Conformance.Targets.MaybeInt" => of::<Option<Box<MaybeInt>>>(),
        "Conformance.Targets.MaybeIntArray" => of::<Option<Box<MaybeIntArray>>>(),
        "Conformance.Targets.BigSlot" => of::<Option<Box<BigSlot>>>(),
        "Conformance.Targets.VersionSlot" => of::<Option<Box<VersionSlot>>>(),
        _ => return None,
    })
}

fn corpus() -> Option<PathBuf> {
    if let Ok(dir) = std::env::var("NINO_CORPUS") {
        return Some(PathBuf::from(dir));
    }
    let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../conformance/corpus");
    dir.join("index.json").exists().then_some(dir)
}

fn json(path: &Path) -> serde_json::Value {
    let text = std::fs::read_to_string(path).unwrap();
    let mut de = serde_json::Deserializer::from_str(&text);
    de.disable_recursion_limit();
    serde::Deserialize::deserialize(&mut de).unwrap()
}

fn unhex(s: &str) -> Vec<u8> {
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
}

fn run() {
    let Some(dir) = corpus() else {
        assert!(std::env::var("NINO_CORPUS_REQUIRED").as_deref() != Ok("1"), "no corpus");
        eprintln!("derive: no corpus (set NINO_CORPUS); skipped");
        return;
    };
    let index = json(&dir.join("index.json"));
    let (mut failures, mut valid, mut malicious, mut prefixes) = (Vec::new(), 0, 0, 0);
    let mut types = std::collections::BTreeSet::new();
    for group in ["valid", "malicious"] {
        for name in index[group].as_array().unwrap() {
            let name = name.as_str().unwrap();
            let case = json(&dir.join(format!("{group}/{name}.json")));
            let Some(codec) = case_type(case["type"].as_str().unwrap()) else { continue };
            types.insert(case["type"].as_str().unwrap().to_string());
            let bytes = std::fs::read(dir.join(format!("{group}/{name}.bin"))).unwrap();
            let result = codec.run(&bytes).unwrap();
            if group == "valid" {
                let expected =
                    if case["roundTrip"] == true { bytes.clone() } else { unhex(case["reencoded"].as_str().unwrap()) };
                match result {
                    Ok(written) if written == expected => valid += 1,
                    Ok(_) => failures.push(format!("{name}: written back differently")),
                    Err(e) => failures.push(format!("{name}: {e:?}")),
                }
            } else if result.is_ok() && case["expected"] != "error-or-value" {
                failures.push(format!("{name}: accepted"));
            } else {
                malicious += 1;
            }
        }
    }
    for entry in json(&dir.join("malicious/truncations.json"))["cases"].as_array().unwrap() {
        let Some(codec) = case_type(entry["type"].as_str().unwrap()) else { continue };
        let name = entry["case"].as_str().unwrap();
        let bytes = std::fs::read(dir.join(format!("valid/{name}.bin"))).unwrap();
        for n in 0..bytes.len() {
            if codec.run(&bytes[..n]).unwrap().is_ok() {
                failures.push(format!("{name}: prefix of {n} bytes accepted"));
                break;
            }
            prefixes += 1;
        }
    }
    eprintln!("derive: {} types, {valid} valid and {malicious} malicious cases, {prefixes} prefixes", types.len());
    assert!(valid > 0, "no valid case ran");
    assert!(failures.is_empty(), "{} failures:\n{}", failures.len(), failures.join("\n"));
}

#[test]
fn derived_codecs_read_the_corpus() {
    std::thread::Builder::new().stack_size(256 << 20).spawn(run).unwrap().join().unwrap();
}

#[test]
fn derived_values() {
    let outer = Outer {
        name: Some("o".into()),
        point: Some(Box::new(Point { x: 1, y: 2 })),
        middle: Some(Box::new(Middle { first: None, second: None, inner: None })),
    };
    let bytes = nino::serialize(&Some(Box::new(outer.clone()))).unwrap();
    assert_eq!(nino::deserialize_exact::<Option<Box<Outer>>>(&bytes).unwrap().as_deref(), Some(&outer));
    // A version-1 keyed payload read as version 2: Health is read into Hp.
    let v1 = KeyedV1 { health: 7, name: Some("n".into()), items: Some(vec![1]), score: 9 };
    let v2: Option<Box<KeyedV2>> = nino::deserialize_exact(&nino::serialize(&Some(Box::new(v1))).unwrap()).unwrap();
    assert_eq!(v2.as_deref(), Some(&KeyedV2 { score: 9, name: Some("n".into()), hp: 7, level: 0 }));
    // Raw structs: the canonical layout, padding included.
    assert_eq!(<Padded as nino::Raw>::SIZE, 16);
    assert_eq!(<Packed as nino::Raw>::SIZE, 11);
    assert_eq!(<Nested as nino::Raw>::SIZE, 32);
    assert_eq!(nino::serialize(&Padded { a: 1, b: 2 }).unwrap(), [1, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0]);
    let mut bad = nino::serialize(&Padded { a: 1, b: 2 }).unwrap();
    bad[3] = 1;
    assert!(nino::deserialize::<Padded>(&bad).is_err());
    // Padding inside a member is checked by the member (bytes 1 and 7), the tail past Flag by Nested (26, 31).
    let nested = Nested { inner: Padded { a: 1, b: 2 }, position: Vec2 { x: 1.0, y: 2.0 }, kind: 3, flag: true };
    let good = nino::serialize(&nested).unwrap();
    assert_eq!(nino::deserialize_exact::<Nested>(&good).unwrap(), nested);
    for at in [1, 7, 26, 31] {
        let mut bad = good.clone();
        bad[at] = 1;
        assert!(nino::deserialize::<Nested>(&bad).is_err(), "padding byte {at}");
    }
    // A packed struct has no padding: every byte is a member's.
    let packed = Packed { a: 0xFF, b: -1, c: -1 };
    let bytes = nino::serialize(&packed).unwrap();
    assert_eq!(bytes, [0xFF; 11]);
    assert_eq!(nino::deserialize_exact::<Packed>(&bytes).unwrap(), packed);
    // A deep chain through the derive: deferred like the generated codecs.
    let mut chain = None;
    for v in (1..=1000).rev() {
        chain = Some(Box::new(TolerantChain { value: v, next: chain }));
    }
    let bytes = nino::serialize(&chain).unwrap();
    let back: Option<Box<TolerantChain>> = nino::deserialize_exact(&bytes).unwrap();
    assert_eq!(nino::serialize(&back).unwrap(), bytes);
    std::mem::forget(back);
    std::mem::forget(chain);
}

#[cfg(feature = "serde")]
#[test]
fn serde_names() {
    let v = TolerantHolder { first: None, polymorphic: None, empty: None, after: 3 };
    let text = serde_json::to_string(&v).unwrap();
    assert_eq!(text, r#"{"first":null,"polymorphic":null,"empty":null,"after":3}"#);
    let k = KeyedStruct { a: 1, b: 2.5 };
    let back: KeyedStruct = serde_json::from_str(&serde_json::to_string(&k).unwrap()).unwrap();
    assert_eq!(back, k);
    let s = StringCollections { utf8_a: Some("x".into()), ..Default::default() };
    assert!(serde_json::to_string(&s).unwrap().contains(r#""utf8A":"x""#));
}

/// A plain struct with a member written as UTF-8.
#[derive(Debug, Default, Clone, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(kind = "struct")]
pub struct VariablePlain {
    pub id: i32,
    #[nino(utf8)]
    pub name: String,
}

#[test]
fn utf8_members_take_one_byte_at_least() {
    // A string written as UTF-8 may be its header alone, or two bytes and its text: 7 bytes an element here, so a
    // minimum of 4 bytes for the string rejected the list's length.
    assert_eq!(<VariablePlain as NinoRead>::MIN_SIZE, 5);
    let v = vec![VariablePlain { id: 1, name: "a".into() }, VariablePlain { id: 2, name: String::new() }];
    let bytes = nino::serialize(&v).unwrap();
    assert_eq!(bytes.len(), 4 + 7 + 5);
    assert_eq!(nino::deserialize_exact::<Vec<VariablePlain>>(&bytes).unwrap(), v);
}

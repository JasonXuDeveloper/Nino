using System;
using System.Collections.Generic;
using System.Linq;

namespace Nino.Codegen.Model
{
    /// <summary>What a value is on the wire: the kinds of encodings the layouts name (docs/spec/wire-format.md).</summary>
    public enum ValueKind
    {
        /// <summary>A primitive or a BCL value of fixed size (<c>raw(n)</c>): <see cref="ValueModel.Primitive"/>.</summary>
        Primitive,

        /// <summary>An enum, as its underlying integer (<c>raw(n)</c>).</summary>
        Enum,

        /// <summary>A struct copied as raw memory in its canonical layout: <see cref="ValueModel.Struct"/>.</summary>
        RawStruct,

        /// <summary>A <c>T?</c> of a raw <c>T</c> inside raw memory: a flag, then <c>T</c> at its offset.</summary>
        RawNullable,

        /// <summary>A <c>ValueTuple</c> or <c>KeyValuePair</c> copied as raw memory: each item at its offset.</summary>
        RawTuple,

        /// <summary>A string, Latin-1 or UTF-16 (<c>string</c>).</summary>
        String,

        /// <summary>A <c>[NinoUtf8]</c> string (<c>utf8</c>).</summary>
        Utf8,

        /// <summary>A <c>[NinoType]</c> value behind its type id, of any type (<c>object</c>).</summary>
        Object,

        /// <summary>A <c>[NinoType]</c> (<c>type(T)</c>): <see cref="ValueModel.Type"/>.</summary>
        Type,

        /// <summary>
        /// <c>type(X)</c> of a type without a layout: a collection of itself (<c>class Tree : List&lt;Tree&gt;</c>, whose
        /// value is <see cref="CodegenModel.NamedCollections"/>), or a type parameter.
        /// </summary>
        Named,

        /// <summary>
        /// A type parameter of the generic type being described (<c>type(T)</c>): written as the type argument is,
        /// which the code reading the generic type is given (<see cref="ValueModel.Parameter"/>).
        /// </summary>
        Parameter,

        /// <summary>
        /// A generic <c>[NinoType]</c> with its type arguments (<c>type(Box&lt;int&gt;)</c>): <see cref="ValueModel.Type"/>
        /// is the generic definition, <see cref="ValueModel.Arguments"/> the values of its arguments.
        /// </summary>
        Generic,

        /// <summary>A presence byte, then the value (<c>nullable(E)</c>): <c>T?</c>, <c>Lazy&lt;T&gt;</c>, <c>Tuple</c>.</summary>
        Nullable,

        /// <summary>A count (-1 = null), then the elements (<c>seq(E)</c>).</summary>
        Sequence,

        /// <summary>A count (-1 = null), then each key and value (<c>map(K,V)</c>).</summary>
        Map,

        /// <summary>The items one after another (<c>tuple(...)</c>, <c>pair(K,V)</c>).</summary>
        Tuple,

        /// <summary>A multi-dimensional array (<c>grid(rank,E)</c>).</summary>
        Grid,

        /// <summary><c>BigInteger</c> (<c>bigint</c>).</summary>
        BigInteger,

        /// <summary><c>Version</c> (<c>version</c>).</summary>
        Version,

        /// <summary><c>DateTimeOffset</c> (<c>datetimeoffset</c>).</summary>
        DateTimeOffset,

        /// <summary>An encoding no generated code can express: <see cref="ValueModel.Problem"/> says why.</summary>
        Unsupported,
    }

    /// <summary>The fixed-size values of <see cref="ValueKind.Primitive"/>.</summary>
    public enum PrimitiveKind
    {
        Bool,
        I8,
        U8,
        I16,
        U16,
        I32,
        U32,
        I64,
        U64,
        F16,
        F32,
        F64,
        Char,
        Decimal,
        Guid,
        DateTime,
        TimeSpan,
        DateOnly,
        TimeOnly,
        I128,
        U128,
        /// <summary><c>nint</c>: 8 bytes on the wire.</summary>
        NInt,
        /// <summary><c>nuint</c>: 8 bytes on the wire.</summary>
        NUInt,
        Rune,
    }

    /// <summary>What a collection is in .NET, for languages that have a type for each.</summary>
    public enum CollectionKind
    {
        Array,
        List,
        HashSet,
        SortedSet,
        Dictionary,
        SortedDictionary,
        Other,
    }

    /// <summary>
    /// A value in the layout intermediate representation: the encoding a layout names for a member, resolved against
    /// the member's .NET type. Built once by <see cref="CodegenModel"/>; every target reads it, none changes it.
    /// </summary>
    public sealed class ValueModel
    {
        internal ValueModel(ValueKind kind, Type clr, string encoding)
        {
            Kind = kind;
            Clr = clr;
            Encoding = encoding;
        }

        public ValueKind Kind { get; }

        /// <summary>The .NET type of the value.</summary>
        internal Type Clr { get; }

        /// <summary>Its full name (<c>System.Int32</c>, <c>Conformance.Point</c>).</summary>
        public string ClrName => Clr.FullName ?? Clr.Name;

        /// <summary>The encoding as the layout writes it (<c>seq(raw(4))</c>).</summary>
        public string Encoding { get; }

        /// <summary>The bytes it always takes on the wire, or -1 when that depends on the value.</summary>
        public int Size { get; internal set; } = -1;

        public bool IsFixed => Size >= 0;

        public PrimitiveKind Primitive { get; internal set; }

        public EnumModel? Enum { get; internal set; }

        /// <summary>The <c>[NinoType]</c> of <see cref="ValueKind.Type"/>, the generic definition of <see cref="ValueKind.Generic"/>.</summary>
        public TypeModel? Type { get; internal set; }

        /// <summary>The type arguments of <see cref="ValueKind.Generic"/>, as .NET writes each of them.</summary>
        public List<ValueModel> Arguments { get; } = new();

        /// <summary>The position of a <see cref="ValueKind.Parameter"/> among its type's parameters.</summary>
        public int Parameter { get; internal set; } = -1;

        /// <summary>The name of a <see cref="ValueKind.Parameter"/> (<c>T</c>, <c>TKey</c>).</summary>
        public string? ParameterName { get; internal set; }

        /// <summary>
        /// Whether it is <paramref name="owner"/> (a generic definition) with the owner's own parameters, in order:
        /// a generic type holding itself, read with the codecs it was given.
        /// </summary>
        public bool IsSelf(TypeModel owner) =>
            Kind == ValueKind.Generic && Type == owner &&
            Arguments.Select((a, i) => a.Kind == ValueKind.Parameter && a.Parameter == i).All(x => x);

        /// <summary>Whether a type parameter is inside it: it is read with the codecs of the arguments.</summary>
        public bool IsOpen => Walk().Any(v => v.Kind == ValueKind.Parameter);

        /// <summary>The struct of <see cref="ValueKind.RawStruct"/>.</summary>
        public RawStructModel? Struct { get; internal set; }

        /// <summary>
        /// For a primitive whose layout also lists its fields (<c>Int128</c>: <c>_lower</c>, <c>_upper</c>), the same
        /// bytes as a raw struct of them, for a language without the primitive.
        /// </summary>
        public ValueModel? Fallback { get; internal set; }

        /// <summary>The flag's offset in a <see cref="ValueKind.RawNullable"/>.</summary>
        public int FlagOffset { get; internal set; }

        /// <summary>The value's offset in a <see cref="ValueKind.RawNullable"/>.</summary>
        public int ValueOffset { get; internal set; }

        /// <summary>The element (sequence, grid), the value of a nullable or raw nullable.</summary>
        public ValueModel? Element { get; internal set; }

        public ValueModel? Key { get; internal set; }

        public ValueModel? Value { get; internal set; }

        /// <summary>The items of a tuple, pair or raw tuple.</summary>
        public List<ValueModel> Items { get; } = new();

        /// <summary>The offsets of the items of a <see cref="ValueKind.RawTuple"/>.</summary>
        public List<int> Offsets { get; } = new();

        /// <summary>Whether a raw tuple or <see cref="ValueKind.Tuple"/> is a <c>KeyValuePair</c>.</summary>
        public bool IsPair { get; internal set; }

        public CollectionKind Collection { get; internal set; } = CollectionKind.Other;

        /// <summary>A <c>byte[]</c>: one block of bytes.</summary>
        public bool IsByteArray { get; internal set; }

        /// <summary>A <c>sbyte[]</c>.</summary>
        public bool IsSByteArray => Clr.IsArray && Clr.GetElementType()?.FullName == "System.SByte";

        public int Rank { get; internal set; }

        /// <summary>The type of <see cref="ValueKind.Named"/>.</summary>
        internal Type? NamedType { get; set; }

        public string? NamedTypeName => NamedType?.FullName;

        /// <summary>
        /// Why the value cannot be expressed (<see cref="ValueKind.Unsupported"/>), with <c>{lang}</c> where the
        /// target names its language (<c>System.Half (raw(2)) has no {lang} form</c>).
        /// </summary>
        public string? Problem { get; internal set; }

        /// <summary>A raw struct, tuple or nullable whose items fill it with no gap (no padding to zero).</summary>
        public bool Packed { get; internal set; } = true;

        /// <summary>
        /// A closed <see cref="ValueKind.Generic"/> whose definition is written behind a type id (<c>Shelf&lt;int&gt;</c>,
        /// <c>IHolder&lt;long&gt;</c>): the slot of that closed type, which allows the types assignable to it.
        /// </summary>
        public SlotModel? Slot { get; internal set; }

        /// <summary>
        /// The slots the codecs of a <see cref="ValueKind.Generic"/> value need, beside the codecs of its arguments:
        /// its definition's <see cref="TypeModel.SlotParameters"/> with its arguments, in order. Closed ones have a
        /// <see cref="Slot"/>; in a generic definition, open ones are among that definition's own slot parameters.
        /// Not among <see cref="Children"/>.
        /// </summary>
        public List<ValueModel> SlotArguments { get; } = new();

        /// <summary>
        /// Identifies the .NET type, type parameters by position (<c>Crate`1[!0]</c>): the same in every value of it
        /// within one generic definition.
        /// </summary>
        public string TypeKey => CodegenModel.TypeKey(Clr);

        /// <summary>The values directly inside this one (elements, items, keys, values, fields).</summary>
        public IEnumerable<ValueModel> Children()
        {
            if (Element != null)
                yield return Element;
            if (Key != null)
                yield return Key;
            if (Value != null)
                yield return Value;
            foreach (var item in Items)
                yield return item;
            foreach (var argument in Arguments)
                yield return argument;
            if (Struct != null)
                foreach (var field in Struct.Fields)
                    yield return field.Value;
        }

        /// <summary>This value and every value inside it.</summary>
        public IEnumerable<ValueModel> Walk() => new[] { this }.Concat(Children().SelectMany(c => c.Walk()));

        public override string ToString() => $"{Kind} {ClrName} ({Encoding})";
    }

    /// <summary>
    /// A closed generic type written behind a type id (<c>Shelf&lt;int&gt;</c>): each closed type of a generic class
    /// that is not sealed, abstract class or interface is a slot of its own, whose type ids are those of the concrete
    /// types assignable to it, generic (<c>ListShelf&lt;int&gt;</c>) or not (<c>IntShelf : Shelf&lt;int&gt;</c>).
    /// </summary>
    public sealed class SlotModel
    {
        internal SlotModel(Type clr, ValueModel value)
        {
            Clr = clr;
            Value = value;
            RuntimeName = TypeNames.Runtime(clr);
        }

        internal Type Clr { get; }

        /// <summary>The definition with its arguments (<see cref="ValueKind.Generic"/>).</summary>
        public ValueModel Value { get; }

        public TypeModel Definition => Value.Type!;

        /// <summary>The runtime-form name (<c>Conformance.Shelf&lt;System.Int32&gt;</c>).</summary>
        public string RuntimeName { get; }

        /// <summary>The concrete types with a layout of their own (not generic) assignable to it, by name.</summary>
        public List<TypeModel> Assignable { get; } = new();

        /// <summary>The closed generic types assignable to it (itself, when it can be created), by name.</summary>
        public List<ClosedTypeModel> ClosedAssignable { get; } = new();

        public override string ToString() => RuntimeName;
    }

    /// <summary>A struct copied as raw memory: its size and its fields at their offsets.</summary>
    public sealed class RawStructModel
    {
        internal RawStructModel(Type clr) => Clr = clr;

        internal Type Clr { get; }

        /// <summary>The full name; a closed generic struct's in the runtime form (<c>Pair&lt;System.Int32,System.Int32&gt;</c>).</summary>
        public string FullName => Clr.IsGenericType ? TypeNames.Runtime(Clr) : Clr.FullName ?? Clr.Name;

        /// <summary>The <c>[NinoType]</c> layout of the struct, when it has one (a BCL struct has none).</summary>
        public TypeModel? Layout { get; internal set; }

        public int Size { get; internal set; }

        public List<RawFieldModel> Fields { get; } = new();

        /// <summary>Whether the fields fill it with no gap.</summary>
        public bool Packed { get; internal set; }

        /// <summary>A struct without fields: one byte, as .NET gives it.</summary>
        public bool Empty => Fields.Count == 0;
    }

    /// <summary>A field of a raw struct: <c>name:type@offset:encoding</c>.</summary>
    public sealed class RawFieldModel
    {
        internal RawFieldModel(string name, int offset, ValueModel value)
        {
            Name = name;
            Offset = offset;
            Value = value;
        }

        public string Name { get; }

        public int Offset { get; }

        public ValueModel Value { get; }
    }

    /// <summary>An enum: its underlying type and its constants.</summary>
    public sealed class EnumModel
    {
        internal EnumModel(Type clr) => Clr = clr;

        internal Type Clr { get; }

        public string FullName => Clr.FullName ?? Clr.Name;

        /// <summary>The underlying integer, or null when it has none of the integer types.</summary>
        public PrimitiveKind? Underlying { get; internal set; }

        public bool Flags { get; internal set; }

        /// <summary>Whether a constant has the value 0.</summary>
        public bool HasZero { get; internal set; }

        /// <summary>The constants in declaration order, values as invariant decimal text.</summary>
        public List<EnumConstant> Constants { get; } = new();
    }

    public sealed class EnumConstant
    {
        internal EnumConstant(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }

        public string Value { get; }
    }
}

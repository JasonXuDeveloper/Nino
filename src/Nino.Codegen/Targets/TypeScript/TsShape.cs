using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    /// <summary>
    /// How a value is written in TypeScript (<see cref="Kind"/>: <c>prim</c>, <c>enum</c>, <c>rawstruct</c>,
    /// <c>rawnullable</c>, <c>fixedtuple</c>, <c>tuple</c>, <c>string</c>, <c>utf8</c>, <c>object</c>,
    /// <c>nullable</c>, <c>bytes</c>, <c>seq</c>, <c>map</c>, <c>type</c>, <c>param</c>: a type parameter,
    /// <c>generic</c>: a generic type with its arguments in <see cref="Items"/>): the template turns it into a type, a
    /// default, and code reading and writing it, alone or at an offset.
    /// </summary>
    public sealed class TsShape
    {
        internal TsShape(string kind) => Kind = kind;

        public string Kind { get; }

        /// <summary>The size of a value of fixed size, else -1: such a value is also read and written at an offset.</summary>
        public int Size { get; internal set; } = -1;

        public bool Fixed => Size >= 0;

        /// <summary>Whether writing it at an offset needs zeroed bytes: it skips padding, or absent values.</summary>
        public bool Zeroed { get; internal set; }

        /// <summary>The reader's and writer's method of a primitive (<c>i32</c>, <c>dateTime</c>...).</summary>
        public string? Primitive { get; internal set; }

        /// <summary>The name of a type parameter (<c>param</c>).</summary>
        public string? Parameter { get; internal set; }

        /// <summary>
        /// The codec it is read and written through: a type parameter's (<c>c.p0</c>), one the generic type's codec
        /// built (<c>c.d0</c>, <c>c.self</c>), or a closed generic type's (<c>codec$0</c>).
        /// </summary>
        public string? Field { get; internal set; }

        /// <summary>The generic definition whose codecs (<c>c</c>) the code of a value holding its parameters takes.</summary>
        public TypeModel? Context { get; internal set; }

        /// <summary><c>, c</c> after the arguments of the code of a value holding type parameters, else empty.</summary>
        public string CodecsArgument => Context == null ? "" : ", c";

        public EnumModel? Enum { get; internal set; }

        /// <summary>An enum's whose default needs a cast through its underlying type (no constant is 0).</summary>
        public bool DefaultCast { get; internal set; }

        public RawStructModel? Struct { get; internal set; }

        public TypeModel? Type { get; internal set; }

        /// <summary>A type's header: <c>none</c>, <c>null</c> (presence byte) or <c>typeid</c>.</summary>
        public string? Header { get; internal set; }

        /// <summary>The value of a nullable or raw nullable, the underlying integer of an enum.</summary>
        public TsShape? Inner { get; internal set; }

        public TsShape? Element { get; internal set; }

        /// <summary>A set (<c>Set</c>) rather than an array.</summary>
        public bool Set { get; internal set; }

        /// <summary>
        /// How a set's element or a map's key of this shape passes through <c>NinoKeyGuard</c> (the runtime's bound on
        /// hash flooding for the keys V8 hashes without a seed): <c>int</c> (an int32 value), <c>number</c>,
        /// <c>bigint</c>, <c>string</c> (V8 hashes an array index such as "42" by its value), <c>key</c> (a nullable
        /// number or bigint, checked by its type at run time), or null for keys it does not guard (objects).
        /// </summary>
        public string? Guard => Kind switch
        {
            "prim" => Primitive switch
            {
                "u8" or "i8" or "i16" or "u16" or "i32" => "int",
                "u32" or "f32" or "f64" => "number",
                "i64" or "u64" or "timeSpan" => "bigint",
                _ => null,
            },
            "string" or "utf8" => "string",
            "enum" => Inner?.Guard,
            "nullable" or "rawnullable" => Inner?.Guard == null ? null : "key",
            _ => null,
        };

        /// <summary>The typed array a sequence of fixed-size numbers is (<c>Int32Array</c>...), else null.</summary>
        public string? ArrayType { get; internal set; }

        /// <summary>
        /// The most elements of a typed array read and written one by one (those that fit in 64 bytes, which V8
        /// keeps on its heap); longer ones are copied in one block (<c>NinoReader.numbers</c>, <c>NinoWriter.numbers</c>).
        /// </summary>
        public int InlineLength { get; internal set; }

        public TsShape? Key { get; internal set; }

        public TsShape? Value { get; internal set; }

        public List<TsShape> Items { get; internal set; } = new();

        /// <summary>The slots a generic type's codec is built with, after the codecs of its arguments.</summary>
        public List<TsShape> Slots { get; internal set; } = new();

        public List<int> Offsets { get; internal set; } = new();

        public int FlagOffset { get; internal set; }

        public int ValueOffset { get; internal set; }

        /// <summary>Whether the value may hold a class body (a class, <c>object</c> or an interface) itself.</summary>
        internal bool Opens { get; set; }

        /// <summary>The non-raw structs the value holds directly: their bodies may open more.</summary>
        internal List<TypeModel> Reaches { get; set; } = new();

        /// <summary>Identifies the code of the shape: two shapes with the same key are read and written alike.</summary>
        internal string Identity => _key ??= Kind + "(" + string.Join(",", Parts()) + ")";

        private string? _key;

        private IEnumerable<string> Parts()
        {
            yield return Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
            yield return Zeroed ? "z" : "";
            yield return Primitive ?? "";
            yield return Parameter ?? "";
            yield return Context?.FullName ?? "";
            yield return Enum?.FullName ?? "";
            yield return Struct?.FullName ?? "";
            yield return Type?.FullName ?? "";
            yield return Header ?? "";
            yield return Set ? "set" : "";
            yield return ArrayType ?? "";
            yield return FlagOffset + "/" + ValueOffset;
            yield return string.Join("/", Offsets);
            foreach (var child in new[] { Inner, Element, Key, Value }.Concat(Items))
                yield return child?.Identity ?? "-";
            yield return string.Join("/", Slots.Select(slot => slot.Field));
        }
    }
}

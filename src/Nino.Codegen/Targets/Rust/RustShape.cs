using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    /// <summary>
    /// How a value is written in Rust (<see cref="Kind"/>: <c>prim</c>, <c>enum</c>, <c>rawstruct</c>, <c>tuple</c>,
    /// <c>string</c>, <c>keystring</c>, <c>utf8</c>, <c>keyutf8</c>, <c>object</c>, <c>struct</c>, <c>class</c>,
    /// <c>slot</c>, <c>named</c>, <c>nullable</c>, <c>seq</c>, <c>map</c>, <c>grid</c>, <c>bigint</c>,
    /// <c>version</c>, <c>datetimeoffset</c>, <c>param</c>: a type parameter, <c>generic</c> and <c>genericclass</c>:
    /// a generic struct or class with its arguments in <see cref="Items"/>).
    /// </summary>
    public sealed class RustShape
    {
        internal RustShape(string kind) => Kind = kind;

        public string Kind { get; }

        /// <summary>The <see cref="PrimitiveKind"/> of a primitive.</summary>
        public string? Primitive { get; internal set; }

        /// <summary>The name of a type parameter (<c>param</c>).</summary>
        public string? Parameter { get; internal set; }

        public int Size { get; internal set; } = -1;

        /// <summary>The alignment of a raw value (<c>nino::Raw::ALIGN</c>).</summary>
        public int Align { get; internal set; } = 1;

        /// <summary>Whether it is <c>Eq + Hash</c>: a set element or map key.</summary>
        public bool Hashable { get; internal set; }

        /// <summary>Whether reading it may defer a class body: read through <c>read_member</c>.</summary>
        public bool MayDefer { get; internal set; }

        /// <summary>A string as <c>nino::WideString</c>.</summary>
        public bool Wide { get; internal set; }

        public EnumModel? Enum { get; internal set; }

        public RawStructModel? Struct { get; internal set; }

        public TypeModel? Type { get; internal set; }

        public RustNamed? Named { get; internal set; }

        public RustShape? Inner { get; internal set; }

        public RustShape? Element { get; internal set; }

        public RustShape? Key { get; internal set; }

        public RustShape? Value { get; internal set; }

        public List<RustShape> Items { get; internal set; } = new();

        /// <summary>
        /// The enum of a slot of a closed generic type (<c>genslot</c>), or the type parameter standing for it in a
        /// generic definition.
        /// </summary>
        public string? RefName { get; internal set; }

        /// <summary>A generic type's slot arguments (<c>genslot</c> shapes), after its type arguments.</summary>
        public List<RustShape> Slots { get; internal set; } = new();

        /// <summary>A sequence's Rust type: <c>vec</c>, <c>set</c> (<c>IndexSet</c>) or <c>rawnullables</c>.</summary>
        public string? Mode { get; internal set; }

        public int Rank { get; internal set; }
    }
}

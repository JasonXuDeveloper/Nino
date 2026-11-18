using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;
using Nino.Core.Internal;

namespace Nino.Codegen.Targets
{
    /// <summary>
    /// How a value is written in Go (<see cref="Kind"/>: <c>prim</c>, <c>enum</c>, <c>rawstruct</c>,
    /// <c>emptystruct</c>, <c>rawnullable</c>, <c>rawtuple</c>, <c>string</c>, <c>utf8</c>, <c>keystring</c>,
    /// <c>keyutf8</c>, <c>object</c>, <c>struct</c>, <c>class</c>, <c>slot</c>, <c>named</c>, <c>nullable</c>,
    /// <c>seq</c>, <c>map</c>, <c>tuple</c>, <c>grid</c>, <c>bigint</c>, <c>version</c>, <c>datetimeoffset</c>,
    /// <c>param</c>: a type parameter, <c>generic</c>: a generic type with its arguments in <see cref="Items"/>).
    /// </summary>
    public sealed class GoShape
    {
        internal GoShape(string kind) => Kind = kind;

        public string Kind { get; }

        /// <summary>The reader's and writer's method of a primitive (<c>I32</c>, <c>Half</c>, <c>Decimal</c>...).</summary>
        public string? Primitive { get; internal set; }

        /// <summary>The bytes of a raw value, else -1.</summary>
        public int Size { get; internal set; } = -1;

        /// <summary>The fewest bytes a value takes: a collection's count is checked against it.</summary>
        public int MinSize { get; internal set; }

        /// <summary>An expression for the fewest bytes, where the arguments of a generic type decide them.</summary>
        internal string? MinExpression { get; set; }

        /// <summary><see cref="MinSize"/>, or the expression giving it.</summary>
        public string Min => MinExpression ?? MinSize.ToString(CultureInfo.InvariantCulture);

        /// <summary>The name of a type parameter (<c>param</c>).</summary>
        public string? Parameter { get; internal set; }

        /// <summary>
        /// The codec (<c>NinoArg</c>) it is read and written through: a type parameter's (<c>c.p0</c>), one its
        /// generic type's codec built (<c>c.d0</c>, <c>c.self</c>), or a closed generic type's (<c>codecV0</c>).
        /// </summary>
        public string? Field { get; internal set; }

        /// <summary>Whether arrays of it are one block (<c>nino.ReadPrimitives</c>).</summary>
        public bool Block { get; internal set; }

        /// <summary>Whether sequences of this value use nino.ReadBlock and nino.WriteBlock.</summary>
        public bool BlockValue { get; internal set; }

        /// <summary>Whether this value needs range validation when written outside raw memory.</summary>
        public bool CheckedWrite { get; internal set; }

        /// <summary>Whether the fixed encoder omits padding, unused slots or false booleans.</summary>
        public bool NeedsZero => Kind is "rawstruct" or "emptystruct" or "rawnullable" or "rawtuple" || Primitive == "Bool";

        /// <summary>Whether the key of each pair is checked for repeats.</summary>
        public bool UniqueKeys { get; internal set; }

        /// <summary>A raw struct that can be copied as memory, its arrays as one block; else null.</summary>
        public GoRawBlock? RawBlock { get; internal set; }

        /// <summary>Whether it is read in a run of fixed-size members: every bit pattern is a valid value.</summary>
        public bool Runnable { get; internal set; }

        public EnumModel? Enum { get; internal set; }

        public RawStructModel? Struct { get; internal set; }

        public TypeModel? Type { get; internal set; }

        public GoNamed? Named { get; internal set; }

        public GoShape? Inner { get; internal set; }

        public GoShape? Element { get; internal set; }

        public GoShape? Key { get; internal set; }

        public GoShape? Value { get; internal set; }

        public List<GoShape> Items { get; internal set; } = new();

        /// <summary>
        /// The slot codecs a generic type's codec is given (<see cref="ValueModel.SlotArguments"/>), after the codecs
        /// of its type arguments.
        /// </summary>
        public List<GoShape> Slots { get; internal set; } = new();

        /// <summary>The slot of a closed generic type written behind a type id (<c>genslot</c>), with its functions.</summary>
        public GoSlot? Slot { get; internal set; }

        public List<int> Offsets { get; internal set; } = new();

        /// <summary>A <c>KeyValuePair</c>: a <c>nino.Entry</c>.</summary>
        public bool Pair { get; internal set; }

        /// <summary>
        /// A sequence's way: <c>bytes</c>, <c>block</c>, <c>bools</c>, <c>values</c> (a block of dates, decimals,
        /// Guids or 128-bit integers), <c>ninonullables</c> (a block of <c>NinoNullable&lt;T&gt;</c> of a number),
        /// <c>nullableslots</c> (a block of <c>T?</c> of a number, a slot each),
        /// <c>fixed</c> (raw values at their stride),
        /// <c>unique</c> (a set of comparable values), <c>stringset</c>, <c>slice</c>; a map's: <c>block</c>
        /// (primitive keys and values, pairs copied as one block where their memory is their bytes; repeated keys
        /// fail), <c>fixed</c> (both shapes have a stride), <c>unique</c> (repeated keys fail) or <c>entries</c>.
        /// </summary>
        public string? Mode { get; internal set; }

        /// <summary>
        /// A <c>seq</c> that is a set of <c>T?</c> slots (<c>nullableslots</c> or <c>fixed</c>): read as the
        /// sequence is, then checked for an element read twice (<c>nino.CheckUniqueValues</c>, which compares a
        /// <c>nino.NullableSlot</c> as .NET's <c>Nullable&lt;T&gt;</c>).
        /// </summary>
        public bool UniqueSlots { get; internal set; }

        public int FlagOffset { get; internal set; }

        public int ValueOffset { get; internal set; }

        /// <summary>
        /// A <c>rawnullable</c> that is a <c>NinoNullable&lt;T&gt;</c> (<see cref="ValueModel.Clean"/>): a
        /// <c>nino.NinoNullable[T]</c>, where a <c>T?</c> is a <c>*T</c>.
        /// </summary>
        public bool Clean { get; internal set; }

        /// <summary>
        /// A <c>rawnullable</c> that is a <c>T?</c> as the element of a sequence or a grid (wire-format spec §6.2): a
        /// <c>nino.NullableSlot[T]</c>, where a single <c>T?</c> (a field of a raw struct, a tuple's item) is a
        /// <c>*T</c>.
        /// </summary>
        public bool SlotElement { get; internal set; }

        /// <summary>
        /// The runtime's value type that holds a <c>rawnullable</c> (<c>HasValue</c>, <c>Value</c>); null for one
        /// held as a <c>*T</c>.
        /// </summary>
        public string? Holder => Clean ? "nino.NinoNullable" : SlotElement ? "nino.NullableSlot" : null;

        public int Rank { get; internal set; }

        internal string Identity => _key ??= Kind + "(" + string.Join(",", Parts()) + ")";

        private string? _key;

        private IEnumerable<string> Parts()
        {
            yield return Size.ToString(CultureInfo.InvariantCulture);
            yield return Primitive ?? "";
            yield return Parameter ?? "";
            yield return Enum?.FullName ?? "";
            yield return Struct?.FullName ?? "";
            yield return Type?.FullName ?? "";
            yield return Named?.FullName ?? "";
            yield return (Mode ?? "") + (UniqueSlots || UniqueKeys ? "/unique" : "");
            yield return Pair ? "pair" : "";
            yield return FlagOffset + "/" + ValueOffset + "/" + Rank + (Clean ? "/clean" : "") + (SlotElement ? "/slot" : "");
            yield return string.Join("/", Offsets);
            yield return Slot?.Name ?? "";
            yield return Field ?? "";
            foreach (var child in new[] { Inner, Element, Key, Value }.Concat(Items).Concat(Slots))
                yield return child?.Identity ?? "-";
        }
    }
}

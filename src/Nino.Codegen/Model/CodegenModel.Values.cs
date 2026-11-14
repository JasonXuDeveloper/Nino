using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Nino.Codegen.Layouts;

namespace Nino.Codegen.Model
{
    public sealed partial class CodegenModel
    {
        /// <summary>The kind of the collection a type derives from (<c>class Bag : List&lt;int&gt;</c> is a list).</summary>
        private static CollectionKind CollectionOf(Type type, bool map)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                switch (Generic(t))
                {
                    case "System.Collections.Generic.List`1":
                        return CollectionKind.List;
                    case "System.Collections.Generic.HashSet`1":
                        return CollectionKind.HashSet;
                    case "System.Collections.Generic.SortedSet`1":
                        return CollectionKind.SortedSet;
                    case "System.Collections.Generic.Dictionary`2":
                        return CollectionKind.Dictionary;
                    case "System.Collections.Generic.SortedDictionary`2":
                    case "System.Collections.Generic.SortedList`2":
                        return CollectionKind.SortedDictionary;
                }
            }

            return map ? CollectionKind.Dictionary
                : Implements(type, "System.Collections.Generic.ISet`1") != null ? CollectionKind.HashSet
                : CollectionKind.List;
        }

        private static ValueModel Unsupported(Type clr, string encoding, string problem) =>
            new(ValueKind.Unsupported, clr, encoding) { Problem = problem };

        private static string NoForm(Type clr) => $"{clr.FullName} has no {{lang}} form";

        private static string NoForm(Type clr, string encoding) => $"{clr.FullName} ({encoding}) has no {{lang}} form";

        /// <summary>
        /// <c>System.Numerics.Vector&lt;T&gt;</c>: as wide as the CPU's vectors, so not serializable (wire-format spec §3).
        /// A current generator publishes no layout with one (NINO029); one from an older build is refused here.
        /// </summary>
        internal static bool IsHostSizedVector(Type type) => Generic(type) == "System.Numerics.Vector`1";

        private static ValueModel HostSizedVector(Type clr, string encoding) =>
            Unsupported(clr, encoding,
                "System.Numerics.Vector<T> is not serializable: 16, 32 or 64 bytes by the CPU (wire-format spec §3)");

        /// <summary>A member's encoding resolved against its type.</summary>
        private ValueModel Value(WireEncoding e, Type clr)
        {
            if (IsHostSizedVector(clr))
                return HostSizedVector(clr, e.Source);
            switch (e.Kind)
            {
                case "raw":
                    return Raw(e, clr);
                case "string":
                    return clr.FullName == "System.String"
                        ? new ValueModel(ValueKind.String, clr, e.Source)
                        : Unsupported(clr, e.Source, NoForm(clr));
                case "utf8":
                    return new ValueModel(ValueKind.Utf8, clr, e.Source);
                case "object":
                    return new ValueModel(ValueKind.Object, clr, e.Source);
                case "type":
                    if (Find(clr) is { } type)
                        return new ValueModel(ValueKind.Type, clr, e.Source) { Type = type };
                    if (clr.IsGenericParameter)
                        return new ValueModel(ValueKind.Parameter, clr, e.Source)
                        {
                            Parameter = clr.GenericParameterPosition, ParameterName = clr.Name,
                        };
                    if (clr.IsGenericType && !clr.IsGenericTypeDefinition &&
                        Find(clr.GetGenericTypeDefinition()) is { } generic)
                        return Generic(clr, generic, e.Source);
                    return new ValueModel(ValueKind.Named, clr, e.Source) { NamedType = clr };
                case "nullable":
                {
                    // T?, NinoNullable<T>, Lazy<T>: a presence byte, then T. Tuple<...>: a presence byte, then its items.
                    var definition = Generic(clr);
                    if (IsNullable(clr) || definition == "System.Lazy`1")
                        return new ValueModel(ValueKind.Nullable, clr, e.Source)
                        {
                            Element = Value(e.Arguments[0], clr.GetGenericArguments()[0]),
                        };
                    if (definition?.StartsWith("System.Tuple`", StringComparison.Ordinal) == true)
                        return new ValueModel(ValueKind.Nullable, clr, e.Source) { Element = Value(e.Arguments[0], clr) };
                    return Unsupported(clr, e.Source, NoForm(clr, e.Source));
                }
                case "seq":
                {
                    var elementType = clr.IsArray ? clr.GetElementType()
                        : Implements(clr, "System.Collections.Generic.IEnumerable`1")?.GetGenericArguments()[0];
                    if (elementType == null || clr.IsArray && clr.GetArrayRank() != 1)
                        return Unsupported(clr, e.Source, NoForm(clr));
                    var value = new ValueModel(ValueKind.Sequence, clr, e.Source)
                    {
                        Element = Value(e.Arguments[0], elementType),
                        IsByteArray = clr.IsArray && elementType.FullName == "System.Byte",
                        Collection = clr.IsArray ? CollectionKind.Array
                            : Generic(clr) == "System.Collections.Generic.List`1" ? CollectionKind.List
                            : Generic(clr) == "System.Collections.Generic.SortedSet`1" ? CollectionKind.SortedSet
                            : Implements(clr, "System.Collections.Generic.ISet`1") != null ? CollectionKind.HashSet
                            : CollectionKind.Other,
                    };
                    NameCollection(value);
                    return value;
                }
                case "map":
                {
                    var pair = (Implements(clr, "System.Collections.Generic.IDictionary`2") ??
                                Implements(clr, "System.Collections.Generic.IReadOnlyDictionary`2"))
                        ?.GetGenericArguments();
                    if (pair == null)
                        return Unsupported(clr, e.Source, NoForm(clr));
                    string? definition = Generic(clr);
                    var value = new ValueModel(ValueKind.Map, clr, e.Source)
                    {
                        Key = Value(e.Arguments[0], pair[0]),
                        Value = Value(e.Arguments[1], pair[1]),
                        Collection = definition is "System.Collections.Generic.SortedDictionary`2"
                            or "System.Collections.Generic.SortedList`2"
                            ? CollectionKind.SortedDictionary
                            : CollectionKind.Dictionary,
                    };
                    NameCollection(value);
                    return value;
                }
                case "pair":
                case "tuple":
                {
                    var args = clr.IsGenericType ? clr.GetGenericArguments() : System.Type.EmptyTypes;
                    if (args.Length != e.Arguments.Count || args.Length > 7)
                        return Unsupported(clr, e.Source, NoForm(clr, e.Source));
                    var value = new ValueModel(ValueKind.Tuple, clr, e.Source) { IsPair = e.Kind == "pair" };
                    int size = 0;
                    for (int i = 0; i < args.Length; i++)
                    {
                        var item = Value(e.Arguments[i], args[i]);
                        value.Items.Add(item);
                        value.Offsets.Add(size);
                        size = size < 0 || item.Size < 0 ? -1 : size + item.Size;
                    }

                    value.Size = size;
                    return value;
                }
                case "grid":
                {
                    // grid(rank,E): the argument text is "rank,E".
                    int comma = e.Text.IndexOf(',');
                    var elementType = clr.IsArray ? clr.GetElementType() : null;
                    if (comma < 0 || elementType == null ||
                        !int.TryParse(e.Text.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture,
                            out int rank) || rank != clr.GetArrayRank())
                        return Unsupported(clr, e.Source, NoForm(clr, e.Kind));
                    return new ValueModel(ValueKind.Grid, clr, e.Source)
                    {
                        Rank = rank,
                        Element = Value(WireEncoding.Parse(e.Text.Substring(comma + 1)), elementType),
                    };
                }
                case "bigint":
                    return new ValueModel(ValueKind.BigInteger, clr, e.Source);
                case "version":
                    return new ValueModel(ValueKind.Version, clr, e.Source);
                case "datetimeoffset":
                    return new ValueModel(ValueKind.DateTimeOffset, clr, e.Source) { Size = 10 };
                default:
                    return Unsupported(clr, e.Source, NoForm(clr, e.Kind));
            }
        }

        /// <summary>
        /// A generic <c>[NinoType]</c> with its arguments. The layout names only the closed type
        /// (<c>type(Box&lt;int&gt;)</c>): .NET writes each argument with the formatter it finds for the argument's type at
        /// run time, which <see cref="ArgumentEncoding"/> gives as an encoding.
        /// </summary>
        private ValueModel Generic(Type clr, TypeModel definition, string encoding, bool slot = true)
        {
            var value = new ValueModel(ValueKind.Generic, clr, encoding) { Type = definition };
            // A closed type written behind a type id: its own slot. A closed type's body (slot false) is not.
            if (slot && definition.Header == HeaderKind.TypeId && !clr.ContainsGenericParameters)
                value.Slot = SlotOf(clr, value);
            foreach (var argument in clr.GetGenericArguments())
            {
                string? text = ArgumentEncoding(argument);
                value.Arguments.Add(text == null
                    ? Unsupported(argument, "", $"{argument.FullName ?? argument.Name}, a type argument of {clr.FullName ?? clr.Name}, has no {{lang}} form")
                    : Value(WireEncoding.Parse(text), argument));
            }

            return value;
        }

        /// <summary>
        /// The encoding of a value of a type argument: what Nino.Core's formatter for the type writes at run time
        /// (<c>CachedFormatter&lt;T&gt;</c>), which a generic type's formatter calls for its type parameters. It
        /// differs from a member's encoding where the generator copies a member as raw memory: a closed generic
        /// struct that is not a [NinoType], a <c>ValueTuple</c>, a <c>KeyValuePair</c> or a <c>T?</c> is written
        /// item by item as a type argument, and as raw memory as a member (<c>NinoReflection.IsRawShape</c>). An
        /// array or <c>List</c> of <c>T?</c> with a raw <c>T</c> is the exception: the runtime binds
        /// <c>NullableArrayFormatter</c> / <c>NullableListFormatter</c>, a fixed stride (<see cref="NullableStride"/>).
        /// Null for a type whose formatter is not known here (a <c>Tuple</c>, a type of another library): the
        /// generic type is then left out where it has that argument.
        /// </summary>
        internal string? ArgumentEncoding(Type type)
        {
            if (type.IsGenericParameter || Find(type) != null)
                return $"type({type.Name})";
            if (type.IsEnum)
                return PrimitiveOf(type.GetEnumUnderlyingType()) is { } underlying ? $"raw({PrimitiveSize(underlying)})" : null;
            if (PrimitiveOf(type) is { } primitive)
                return $"raw({PrimitiveSize(primitive)})";
            switch (type.FullName)
            {
                case "System.String":
                    return "string";
                case "System.Object":
                    return "object";
            }

            if (type.IsArray)
                return type.GetArrayRank() == 1 &&
                       (NullableStride(type.GetElementType()!) ?? ArgumentEncoding(type.GetElementType()!)) is { } item
                    ? $"seq({item})"
                    : null;
            if (!type.IsGenericType)
                return null;
            var args = type.GetGenericArguments();
            string definition = type.GetGenericTypeDefinition().FullName ?? "";
            if (Find(type.GetGenericTypeDefinition()) != null)
                return $"type({type.Name})";
            string?[] parts = args.Select(ArgumentEncoding).ToArray();
            if (parts.Any(p => p == null))
                return null;
            switch (definition)
            {
                case "System.Collections.Generic.List`1":
                    return $"seq({NullableStride(args[0]) ?? parts[0]})";
                case "System.Collections.Generic.HashSet`1":
                    return $"seq({parts[0]})";
                case "System.Collections.Generic.Dictionary`2":
                    return $"map({parts[0]},{parts[1]})";
                case "System.Nullable`1":
                    return $"nullable({parts[0]})";
                case NinoNullable:
                    // NinoNullableFormatter: its T is always raw.
                    return RawArgument(args[0]) is { } raw ? $"nullable({raw.Source})" : null;
                case "System.Collections.Generic.KeyValuePair`2":
                    return $"pair({parts[0]},{parts[1]})";
            }

            if (definition.StartsWith("System.ValueTuple`", StringComparison.Ordinal) && args.Length <= 7)
                return $"tuple({string.Join(",", parts)})";
            return null;
        }

        /// <summary>Nino.Core's <c>NinoNullable&lt;T&gt;</c>, which Nino writes and publishes exactly as <c>T?</c>.</summary>
        private const string NinoNullable = "Nino.Core.NinoNullable`1";

        /// <summary>Whether <paramref name="type"/> is a <c>T?</c> or a <c>NinoNullable&lt;T&gt;</c>: one value on the wire.</summary>
        internal static bool IsNullable(Type type) => Generic(type) is "System.Nullable`1" or NinoNullable;

        /// <summary>
        /// The element of a <c>T?[]</c> or <c>List&lt;T?&gt;</c> with a raw <c>T</c> (or of <c>NinoNullable&lt;T&gt;</c>,
        /// the same bytes), as <c>NullableArrayFormatter</c> writes it (wire-format spec §6.2): the presence byte, padding to <c>T</c>'s canonical alignment <c>A</c>,
        /// then <c>T</c>, a stride of <c>A + size(T)</c>; null when <paramref name="type"/> is not such a <c>T?</c>.
        /// </summary>
        private string? NullableStride(Type type)
        {
            if (!IsNullable(type))
                return null;
            var value = type.GetGenericArguments()[0];
            if (RawArgument(value) is not { } raw)
                return null;
            int align = CanonicalLayout.Align(value);
            if (align <= 0)
                return null;
            return $"raw({align + raw.Size}){{hasValue:System.Boolean@0:raw(1),value:{value.FullName}@{align}:{raw.Source}}}";
        }

        /// <summary>
        /// The raw encoding of a type argument the runtime copies as raw memory (<c>NinoReflection.IsRawUnmanaged</c>):
        /// a primitive other than a pointer-sized integer, an enum, or a <c>[NinoType]</c> struct written raw.
        /// </summary>
        private WireEncoding? RawArgument(Type type)
        {
            if (type.IsEnum)
                return PrimitiveOf(type.GetEnumUnderlyingType()) is { } underlying
                    ? WireEncoding.Parse($"raw({PrimitiveSize(underlying)})")
                    : null;
            if (PrimitiveOf(type) is { } primitive)
                return primitive is PrimitiveKind.NInt or PrimitiveKind.NUInt
                    ? null
                    : WireEncoding.Parse($"raw({PrimitiveSize(primitive)})");
            return Find(type) is { Layout: { Raw: true, RawEncoding: { Size: > 0 } encoding } } ? encoding : null;
        }

        /// <summary>Records a collection that holds itself (its elements, keys or values are <c>type(itself)</c>).</summary>
        private void NameCollection(ValueModel value)
        {
            if (value.Children().Any(c => c.Kind == ValueKind.Named && c.NamedType == value.Clr))
                NamedCollections.TryAdd(value.ClrName, value);
        }

        /// <summary>A value copied as raw memory: a primitive, an enum, or a struct in its canonical layout.</summary>
        private ValueModel Raw(WireEncoding e, Type clr)
        {
            if (IsHostSizedVector(clr))
                return HostSizedVector(clr, e.Source);
            if (clr.IsEnum)
            {
                var model = EnumOf(clr);
                if (model.Underlying is not { } underlying)
                    return Unsupported(clr, e.Source, $"{clr.FullName}'s underlying type has no {{lang}} form");
                return new ValueModel(ValueKind.Enum, clr, e.Source)
                {
                    Enum = model,
                    Primitive = underlying,
                    Size = e.Size >= 0 ? e.Size : PrimitiveSize(underlying),
                };
            }

            if (PrimitiveOf(clr) is { } primitive)
            {
                var value = new ValueModel(ValueKind.Primitive, clr, e.Source)
                {
                    Primitive = primitive,
                    Size = e.Size >= 0 ? e.Size : PrimitiveSize(primitive),
                };
                // Int128 and the like publish their fields: a language without them reads the struct of its fields.
                if (e.Fields.Count != 0 && clr.IsValueType)
                    value.Fallback = RawStruct(e, clr);
                return value;
            }

            // A pointer (delegate* too) is 8 bytes, zero-extended, as nuint (spec §5.2).
            if (clr.IsPointer || clr.IsFunctionPointer)
                return new ValueModel(ValueKind.Primitive, clr, e.Source) { Primitive = PrimitiveKind.NUInt, Size = 8 };
            if (!clr.IsValueType)
                return Unsupported(clr, e.Source, NoForm(clr, e.Source));
            if (e.Fields.Count == 0)
            {
                // `raw` without a size: an empty struct is one byte; anything else is sized only at run time.
                bool empty = e.Size < 0 && clr.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                         BindingFlags.NonPublic).Length == 0;
                return empty ? RawStruct(e, clr) : Unsupported(clr, e.Source, NoForm(clr, e.Source));
            }

            string definition = Generic(clr) ?? "";
            if (IsNullable(clr) && e.Fields.Count == 2)
            {
                // T? (or NinoNullable<T>, published as T?) in the canonical Nullable<T> layout: the flag, then the value at its offset (zeroed when absent).
                var (flag, field) = (e.Fields[0], e.Fields[1]);
                return new ValueModel(ValueKind.RawNullable, clr, e.Source)
                {
                    Element = Raw(field.Encoding, clr.GetGenericArguments()[0]),
                    FlagOffset = flag.Offset,
                    ValueOffset = field.Offset,
                    Size = e.Size,
                    Packed = false,
                };
            }

            if (definition.StartsWith("System.ValueTuple`", StringComparison.Ordinal) ||
                definition == "System.Collections.Generic.KeyValuePair`2")
            {
                // Raw tuples and pairs: each item at its offset.
                var value = new ValueModel(ValueKind.RawTuple, clr, e.Source)
                {
                    Size = e.Size,
                    IsPair = definition == "System.Collections.Generic.KeyValuePair`2",
                    Packed = Packed(e),
                };
                foreach (var field in e.Fields)
                {
                    var fieldType = FieldType(clr, field.Name);
                    value.Items.Add(fieldType == null
                        ? Unsupported(clr, field.Encoding.Source, $"field {field.Name} of {clr.FullName} was not found")
                        : Raw(field.Encoding, fieldType));
                    value.Offsets.Add(field.Offset);
                }

                // The same bytes as a struct of its items at their offsets, for a language whose tuples hold no gaps
                // (an item order other than declaration order, or padding: 64-bit CoreCLR's auto layout).
                value.Fallback = RawStruct(e, clr);
                return value;
            }

            return RawStruct(e, clr);
        }

        private ValueModel RawStruct(WireEncoding e, Type clr)
        {
            if (!_structs.TryGetValue(clr, out var model))
            {
                model = new RawStructModel(clr)
                {
                    Layout = Find(clr),
                    Size = e.Fields.Count == 0 && e.Size < 0 ? 1 : e.Size,
                    Align = Math.Max(CanonicalLayout.Align(clr), 1),
                    Packed = Packed(e),
                };
                _structs[clr] = model;
                foreach (var field in e.Fields)
                {
                    var fieldType = FieldType(clr, field.Name);
                    model.Fields.Add(new RawFieldModel(field.Name, field.Offset, fieldType == null
                        ? Unsupported(clr, field.Encoding.Source, $"field {field.Name} of {clr.FullName} was not found")
                        : Raw(field.Encoding, fieldType)));
                }
            }

            return new ValueModel(ValueKind.RawStruct, clr, e.Source)
            {
                Struct = model,
                Size = model.Size,
                Packed = model.Packed,
            };
        }

        /// <summary>Whether the fields of a raw struct fill it with no gap: each starts where the one before ends.</summary>
        private static bool Packed(WireEncoding e)
        {
            int end = 0;
            foreach (var field in e.Fields)
            {
                if (field.Offset != end || field.Encoding.Size < 0)
                    return false;
                end += field.Encoding.Size;
            }

            return end == e.Size;
        }

        private EnumModel EnumOf(Type clr)
        {
            if (_enums.TryGetValue(clr, out var model))
                return model;
            model = new EnumModel(clr)
            {
                Underlying = PrimitiveOf(clr.GetEnumUnderlyingType()),
                Flags = clr.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.FlagsAttribute"),
            };
            foreach (var field in clr.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                string value = Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture)!;
                model.Constants.Add(new EnumConstant(field.Name, value));
                if (value == "0")
                    model.HasZero = true;
            }

            _enums[clr] = model;
            return model;
        }
    }
}

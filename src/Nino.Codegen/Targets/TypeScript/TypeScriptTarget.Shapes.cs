using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    public sealed partial class TypeScriptTarget
    {
        /// <summary>
        /// The shape of a member of <paramref name="owner"/>: in a generic definition, the values holding its type
        /// parameters are read with the codecs of its arguments (<c>c</c>, the type's <c>X$Codecs</c>).
        /// </summary>
        internal TsShape ShapeIn(TypeModel owner, ValueModel value)
        {
            var outer = _context;
            _context = owner.IsGenericDefinition ? owner : null;
            try
            {
                return Shape(value);
            }
            finally
            {
                _context = outer;
            }
        }

        /// <summary>The codecs a generic type's codec builds (<c>c.d0</c>...), in the order they are built.</summary>
        internal List<TsDerived> DerivedOf(TypeModel type)
        {
            if (!_derived.TryGetValue(type, out var list))
                _derived[type] = list = new List<TsDerived>();
            return list;
        }

        /// <summary>A generic type with its arguments, read and written through a codec built once.</summary>
        private TsShape Generic(ValueModel value)
        {
            var definition = value.Type!;
            if (!_supported.Contains(definition))
                throw new UnsupportedException($"{definition.FullName} is not written to TypeScript");
            if (definition.Header == HeaderKind.TypeId)
                return SlotShape(value);
            var shape = new TsShape("generic")
            {
                Type = definition, Items = value.Arguments.Select(Shape).ToList(), Opens = true,
                Header = definition.Header == HeaderKind.None ? "none" : "null",
                Slots = value.SlotArguments.Select(Shape).ToList(),
            };
            if (_context != null && value.IsSelf(_context))
            {
                shape.Field = "c.self";
                return shape;
            }

            string key = shape.Identity;
            if (value.IsOpen && _context != null)
            {
                var list = DerivedOf(_context);
                var derived = list.FirstOrDefault(d => d.Key == key);
                if (derived == null)
                    list.Add(derived = new TsDerived("d" + list.Count, key, shape));
                shape.Field = "c." + derived.Name;
                return shape;
            }

            if (!_closedByKey.TryGetValue(key, out var closed))
            {
                closed = new TsDerived("codec$" + _closed.Count, key, shape);
                _closedByKey[key] = closed;
                _closed.Add(closed);
            }

            shape.Field = closed.Name;
            return shape;
        }

        /// <summary>
        /// A generic type written behind a type id (<c>Shelf&lt;int&gt;</c>): closed, the slot's codec the module builds
        /// (<c>slot$0</c>); in a generic definition, with its parameters, the slot its codec was given (<c>c.s0</c>).
        /// </summary>
        private TsShape SlotShape(ValueModel value)
        {
            var shape = new TsShape("generic")
            {
                Type = value.Type, Items = value.Arguments.Select(Shape).ToList(), Opens = true, Header = "typeid",
            };
            if (value.Slot is { } model)
            {
                if (!_slotByModel.TryGetValue(model, out var slot))
                {
                    slot = new TsSlot("slot$" + _slots.Count, model, shape);
                    _slotByModel[model] = slot;
                    _slots.Add(slot);
                    // Its closed types are shaped too, so a slot holding one that cannot be written is found here.
                    foreach (var closed in model.ClosedAssignable.Where(_closedSupported.Contains))
                        ClosedShape(closed);
                }

                shape.Field = slot.Name;
                return shape;
            }

            int index = _context?.SlotParameters.FindIndex(p => p.TypeKey == value.TypeKey) ?? -1;
            if (index < 0)
                throw new UnsupportedException($"{value.ClrName} is a slot outside its generic type");
            shape.Field = "c.s" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            shape.Context = _context;
            return shape;
        }

        /// <summary>A base class or interface with the arguments a type gives it: its TypeScript type.</summary>
        internal TsShape BaseShape(TypeModel owner, ValueModel value)
        {
            var outer = _context;
            _context = owner.IsGenericDefinition ? owner : null;
            try
            {
                return new TsShape("generic") { Type = value.Type, Items = value.Arguments.Select(Shape).ToList() };
            }
            finally
            {
                _context = outer;
            }
        }

        /// <summary>A value holding a type parameter of the type being written: its code takes that type's codecs.</summary>
        private TsShape InContext(ValueModel value, TsShape shape)
        {
            if (_context != null && value.IsOpen)
                shape.Context = _context;
            return shape;
        }

        /// <summary>The TypeScript shape of a value: what the template writes for it.</summary>
        internal TsShape Shape(ValueModel value)
        {
            switch (value.Kind)
            {
                case ValueKind.Primitive:
                case ValueKind.Enum:
                case ValueKind.RawStruct:
                case ValueKind.RawNullable:
                case ValueKind.RawTuple:
                    return RawShape(value);
                case ValueKind.String:
                    return new TsShape("string");
                case ValueKind.Utf8:
                    return new TsShape("utf8");
                case ValueKind.Object:
                    return new TsShape("object") { Opens = true, Reaches = BodyStructs(_ => true) };
                case ValueKind.Type:
                    return TypeShape(value.Type!);
                case ValueKind.Parameter:
                    if (_context == null)
                        throw new UnsupportedException($"{value.ParameterName} is a type parameter outside its type");
                    return new TsShape("param")
                    {
                        Parameter = value.ParameterName, Field = "c.p" + value.Parameter, Opens = true,
                    };
                case ValueKind.Generic:
                    return Generic(value);
                case ValueKind.Named:
                    throw new UnsupportedException($"{value.ClrName} is not written to TypeScript");
                case ValueKind.Nullable:
                    return InContext(value, Nullable(Shape(value.Element!)));
                case ValueKind.Sequence:
                {
                    var element = Shape(value.Element!);
                    bool set = value.Collection is CollectionKind.HashSet or CollectionKind.SortedSet ||
                               CodegenModel.Implements(value.Clr, "System.Collections.Generic.ISet`1") != null;
                    if (value.IsByteArray)
                        return new TsShape("bytes");
                    string? array = null;
                    if (_options.TypedNumericArrays && !set && element.Kind == "prim" &&
                        NumberArrays.TryGetValue(element.Primitive!, out array) && array == "Uint8Array")
                        return new TsShape("bytes");
                    // With --numeric-arrays typed, other fixed-size numbers are a typed array, read and written one by
                    // one while it fits V8's heap (64 bytes), else copied in one block.
                    return InContext(value, Holding(new TsShape("seq")
                    {
                        Element = element, Set = set, ArrayType = array,
                        InlineLength = array == null ? 0 : 64 / element.Size,
                    }, element));
                }
                case ValueKind.Map:
                {
                    var (key, mapValue) = (Shape(value.Key!), Shape(value.Value!));
                    return InContext(value, Holding(new TsShape("map") { Key = key, Value = mapValue }, key, mapValue));
                }
                case ValueKind.Tuple:
                {
                    // The items one after another: at fixed offsets when they all have a fixed size.
                    var items = value.Items.Select(Shape).ToList();
                    var offsets = new List<int>();
                    int size = 0;
                    foreach (var item in items)
                    {
                        offsets.Add(size);
                        size += item.Size;
                    }

                    return InContext(value, Holding(Tuple(items, offsets, items.All(i => i.Fixed) ? size : -1,
                        items.Any(i => i.Zeroed)), items.ToArray()));
                }
                case ValueKind.Unsupported:
                    throw new UnsupportedException(Problem(value));
                default:
                    // grid, bigint, version, datetimeoffset.
                    throw new UnsupportedException($"{value.ClrName} ({KindName(value)}) has no TypeScript form");
            }
        }

        private static string KindName(ValueModel value)
        {
            int open = value.Encoding.IndexOf('(');
            return open < 0 ? value.Encoding : value.Encoding.Substring(0, open);
        }

        private static string Problem(ValueModel value) => value.Problem!.Replace("{lang}", "TypeScript");

        /// <summary>A presence byte, then the value if present (<c>T?</c>, <c>Tuple</c>, a sealed class).</summary>
        private static TsShape Nullable(TsShape inner) =>
            new("nullable") { Inner = inner, Opens = inner.Opens, Reaches = inner.Reaches };

        /// <summary>What a value made of <paramref name="parts"/> holds.</summary>
        private static TsShape Holding(TsShape shape, params TsShape[] parts)
        {
            shape.Opens = parts.Any(p => p.Opens);
            shape.Reaches = parts.SelectMany(p => p.Reaches).Distinct().ToList();
            return shape;
        }

        /// <summary>
        /// A tuple or pair as a TypeScript tuple: its items at <paramref name="offsets"/> when they are all of fixed
        /// size (<paramref name="size"/> bytes in all), else one after another.
        /// </summary>
        private static TsShape Tuple(List<TsShape> items, List<int> offsets, int size, bool zeroed) =>
            new(size >= 0 ? "fixedtuple" : "tuple")
            {
                Items = items, Offsets = offsets, Size = size, Zeroed = size >= 0 && zeroed,
            };

        /// <summary>A <c>[NinoType]</c> member, with the header of its kind.</summary>
        internal TsShape TypeShape(TypeModel type)
        {
            if (!_supported.Contains(type))
                throw new UnsupportedException($"{type.FullName} is not written to TypeScript");
            if (type.Raw)
                return Shape(type.RawValue!);
            bool body = type.IsClassBody;
            var reaches = body ? new List<TypeModel>() : new List<TypeModel> { type };
            return type.Header switch
            {
                HeaderKind.None => new TsShape("type") { Type = type, Header = "none", Opens = body, Reaches = reaches },
                // A tracked sealed class: its reference header instead of the presence byte.
                HeaderKind.Presence when type.TrackReferences => new TsShape("type")
                {
                    Type = type, Header = "ref", Opens = body, Reaches = reaches,
                },
                HeaderKind.Presence => Nullable(new TsShape("type")
                {
                    Type = type, Header = "null", Opens = body, Reaches = reaches,
                }),
                // Any type assignable to it, by the type id in front of its body.
                _ => new TsShape("type")
                {
                    Type = type, Header = "typeid", Opens = true, Reaches = BodyStructs(type.IsAssignableFrom),
                },
            };
        }

        /// <summary>A value copied as raw memory: a primitive, an enum, or a struct in its canonical layout.</summary>
        private TsShape RawShape(ValueModel value)
        {
            switch (value.Kind)
            {
                case ValueKind.Enum:
                {
                    var model = value.Enum!;
                    if (!_enums.Contains(model))
                        _enums.Add(model);
                    var underlying = Primitive(model.Underlying!.Value) ?? throw new UnsupportedException(
                        $"{model.FullName}'s underlying type has no TypeScript form");
                    return new TsShape("enum")
                    {
                        Enum = model,
                        Inner = underlying,
                        Size = underlying.Size,
                        DefaultCast = !model.HasZero && !model.Flags,
                    };
                }
                case ValueKind.Primitive:
                    if (Primitive(value.Primitive) is { } primitive)
                        return primitive;
                    if (value.Fallback != null)
                        return RawShape(value.Fallback);
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no TypeScript form");
                case ValueKind.RawNullable:
                {
                    // T? in the canonical Nullable<T> layout: the flag, then the value at its offset (zeroed when absent).
                    var inner = RawShape(value.Element!);
                    return new TsShape("rawnullable")
                    {
                        Inner = inner, FlagOffset = value.FlagOffset, ValueOffset = value.ValueOffset,
                        Size = value.Size, Zeroed = true,
                    };
                }
                case ValueKind.RawTuple:
                {
                    // Raw tuples and pairs are TypeScript tuples too: each item at its offset.
                    var items = value.Items.Select(RawShape).ToList();
                    return Tuple(items, value.Offsets.ToList(), value.Size, !value.Packed || items.Any(i => i.Zeroed));
                }
                case ValueKind.RawStruct:
                {
                    var model = value.Struct!;
                    // DateTimeOffset as the struct of its canonical fields holds a DateTime to the tick and of kind
                    // Unspecified, which a Date (milliseconds, written as UTC) cannot give back.
                    if (model.Empty || model.FullName == "System.DateTimeOffset")
                        throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no TypeScript form");
                    int index = _rawStructs.FindIndex(r => r.Struct == model);
                    if (index >= 0)
                        return _rawStructs[index].Shape;
                    var shape = new TsShape("rawstruct") { Struct = model, Size = model.Size };
                    _rawStructs.Add((model, shape));
                    // Also adds the enums and structs of its fields. Padding is zeroed (as Nino zeroes it), and so is
                    // anything its fields skip.
                    var fields = model.Fields.Select(f => RawShape(f.Value)).ToList();
                    shape.Zeroed = !model.Packed || fields.Any(f => f.Zeroed);
                    return shape;
                }
                case ValueKind.Unsupported:
                    throw new UnsupportedException(Problem(value));
                default:
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no TypeScript form");
            }
        }

        /// <summary>
        /// The typed array an array or list (not a set) of a primitive is held in: the fixed-size numbers, whose bytes
        /// on the wire are those of the typed array on a little-endian host. <c>bool</c> (validated 0 or 1, read as
        /// <c>boolean</c>), <c>char</c> (a string), <c>decimal</c>, <c>TimeSpan</c> and enums (their constants' type)
        /// stay arrays.
        /// </summary>
        private static readonly Dictionary<string, string> NumberArrays = new()
        {
            ["i8"] = "Int8Array", ["u8"] = "Uint8Array", ["i16"] = "Int16Array", ["u16"] = "Uint16Array",
            ["i32"] = "Int32Array", ["u32"] = "Uint32Array", ["i64"] = "BigInt64Array", ["u64"] = "BigUint64Array",
            ["f32"] = "Float32Array", ["f64"] = "Float64Array",
        };

        /// <summary>The primitives TypeScript reads and writes alone, and at an offset of a view.</summary>
        private static TsShape? Primitive(PrimitiveKind kind)
        {
            string? name = kind switch
            {
                PrimitiveKind.Bool => "bool",
                PrimitiveKind.U8 => "u8",
                PrimitiveKind.I8 => "i8",
                PrimitiveKind.I16 => "i16",
                PrimitiveKind.U16 => "u16",
                PrimitiveKind.I32 => "i32",
                PrimitiveKind.U32 => "u32",
                PrimitiveKind.I64 or PrimitiveKind.NInt => "i64",
                PrimitiveKind.U64 or PrimitiveKind.NUInt => "u64",
                PrimitiveKind.F32 => "f32",
                PrimitiveKind.F64 => "f64",
                PrimitiveKind.Char => "char",
                PrimitiveKind.Decimal => "decimal",
                PrimitiveKind.Guid => "guid",
                PrimitiveKind.DateTime => "dateTime",
                PrimitiveKind.TimeSpan => "timeSpan",
                _ => null,
            };
            return name == null ? null : new TsShape("prim") { Primitive = name, Size = CodegenModel.PrimitiveSize(kind) };
        }
    }
}

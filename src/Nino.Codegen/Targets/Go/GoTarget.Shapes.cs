using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;
using Nino.Core.Internal;

namespace Nino.Codegen.Targets
{
    public sealed partial class GoTarget
    {
        /// <summary>
        /// The Go shape of a value. <paramref name="key"/>: a dictionary key, where a string is a <c>string</c> (null
        /// is invalid) rather than a <c>*string</c>.
        /// </summary>
        internal GoShape Shape(ValueModel value, bool key)
        {
            switch (value.Kind)
            {
                case ValueKind.Primitive:
                case ValueKind.Enum:
                case ValueKind.RawStruct:
                case ValueKind.RawNullable:
                case ValueKind.RawTuple:
                    return Raw(value);
                case ValueKind.String:
                    return new GoShape(key ? "keystring" : "string") { MinSize = 1 };
                case ValueKind.Utf8:
                    return new GoShape(key ? "keyutf8" : "utf8") { MinSize = 1 };
                case ValueKind.Object:
                    _usesObject = true;
                    return new GoShape("object") { MinSize = 4 };
                case ValueKind.Type:
                    return TypeShape(value.Type!);
                case ValueKind.Parameter:
                {
                    if (_context == null)
                        throw new UnsupportedException($"{value.ParameterName} is a type parameter outside its type");
                    string field = "c.p" + value.Parameter.ToString(CultureInfo.InvariantCulture);
                    return new GoShape("param")
                        { Parameter = value.ParameterName, Field = field, MinExpression = field + ".MinSize" };
                }
                case ValueKind.Generic:
                {
                    var definition = value.Type!;
                    if (!_supported.Contains(definition))
                        throw new UnsupportedException($"{definition.FullName} is not written to Go");
                    if (definition.Header == HeaderKind.TypeId)
                        return SlotShape(value);
                    var shape = new GoShape("generic")
                    {
                        Type = definition, Items = value.Arguments.Select(a => Shape(a, false)).ToList(),
                        Slots = value.SlotArguments.Select(a => Shape(a, false)).ToList(),
                        MinSize = definition.IsValueType ? 0 : 1,
                    };
                    if (_context != null && value.IsSelf(_context.Type))
                    {
                        shape.Field = "c.self";
                        return shape;
                    }

                    return value.IsOpen ? Open(value, shape, true) : Closed(shape);
                }
                case ValueKind.Named:
                {
                    if (!_model.NamedCollections.TryGetValue(value.NamedTypeName ?? "", out var collection))
                        throw new UnsupportedException($"{value.ClrName} is not written to Go");
                    var named = _namedCollections.FirstOrDefault(n => n.FullName == value.NamedTypeName);
                    if (named == null)
                    {
                        named = new GoNamed(TypeNames.Pascal(string.Concat(
                            TypeNames.WithoutArity(value.NamedTypeName!.Split('.', '+').Last())
                                .Split('_').Select(TypeNames.Pascal))), value.NamedTypeName!);
                        _namedCollections.Add(named);
                        named.Value = Shape(collection, false);
                    }

                    return new GoShape("named") { Named = named, MinSize = 4 };
                }
                case ValueKind.Nullable:
                {
                    var inner = Shape(value.Element!, false);
                    return Open(value, new GoShape("nullable") { Inner = inner, MinSize = 1 }, false);
                }
                case ValueKind.Sequence:
                {
                    var element = Shape(value.Element!, false);
                    bool set = value.Collection is CollectionKind.HashSet or CollectionKind.SortedSet ||
                               CodegenModel.Implements(value.Clr, "System.Collections.Generic.ISet`1") != null;
                    string mode =
                        element.Kind == "prim" && element.Primitive == "U8" ? "bytes"
                        : set && Comparable(element) ? "unique"
                        : set && element.Kind is "string" or "utf8" ? "stringset"
                        : element.Kind == "prim" && element.Primitive == "Bool" ? "bools"
                        : element.Block ? "block"
                        : element.Kind == "prim" && BlockValues.Contains(element.Primitive!) ? "values"
                        : element.Kind is "rawstruct" or "rawnullable" or "rawtuple" ? "fixed"
                        : "slice";
                    return Open(value, new GoShape("seq") { Element = element, Mode = mode, MinSize = 4 }, false);
                }
                case ValueKind.Map:
                {
                    var (k, v) = (Shape(value.Key!, true), Shape(value.Value!, false));
                    return Open(value, new GoShape("map")
                    {
                        Key = k, Value = v,
                        Mode = k.Block && v.Block ? "block"
                            : Comparable(k) || k.Kind is "keystring" or "keyutf8" ? "unique" : "entries",
                        MinSize = 4,
                    }, false);
                }
                case ValueKind.Tuple:
                {
                    var items = value.Items.Select(i => Shape(i, false)).ToList();
                    if (!value.IsPair)
                        _tuples.Add(items.Count);
                    var tuple = new GoShape("tuple") { Items = items, Pair = value.IsPair, MinSize = items.Sum(i => i.MinSize) };
                    if (items.Any(i => i.MinExpression != null))
                        tuple.MinExpression = string.Join(" + ", items.Select(i => i.Min));
                    return Open(value, tuple, false);
                }
                case ValueKind.Grid:
                    _usesGrid = true;
                    return Open(value,
                        new GoShape("grid") { Element = Shape(value.Element!, false), Rank = value.Rank, MinSize = 4 }, false);
                case ValueKind.BigInteger:
                    return new GoShape("bigint") { MinSize = 4 };
                case ValueKind.Version:
                    return new GoShape("version") { MinSize = 4 };
                case ValueKind.DateTimeOffset:
                    return new GoShape("datetimeoffset") { MinSize = 10 };
                case ValueKind.Unsupported:
                    throw new UnsupportedException(value.Problem!.Replace("{lang}", "Go"));
                default:
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no Go form");
            }
        }

        /// <summary>
        /// A generic type written behind a type id (<c>Shelf&lt;int&gt;</c>), a value of its generic interface
        /// (<c>Shelf[int32]</c>): closed, read and written by the slot's functions (<c>readSlot0</c>); in a generic
        /// definition, with its parameters, by the slot codec its codec was given (<c>c.s0</c>).
        /// </summary>
        private GoShape SlotShape(ValueModel value)
        {
            var shape = new GoShape("genslot")
            {
                Type = value.Type, Items = value.Arguments.Select(a => Shape(a, false)).ToList(), MinSize = 4,
            };
            if (value.Slot is { } model)
            {
                if (!_slotByModel.TryGetValue(model, out var slot))
                {
                    slot = new GoSlot(this, "slot" + _slots.Count.ToString(CultureInfo.InvariantCulture), model, shape);
                    _slotByModel[model] = slot;
                    _slots.Add(slot);
                    // Its closed types are shaped too, so a slot holding one that cannot be written is found here.
                    foreach (var closed in model.ClosedAssignable.Where(_closedSupported.Contains))
                        ClosedShape(closed);
                }

                shape.Slot = slot;
                return shape;
            }

            int index = _context?.Type.SlotParameters.FindIndex(p => p.TypeKey == value.TypeKey) ?? -1;
            if (index < 0)
                throw new UnsupportedException($"{value.ClrName} is a slot outside its generic type");
            shape.Field = "c.s" + index.ToString(CultureInfo.InvariantCulture);
            return shape;
        }

        /// <summary>
        /// A generic slot a type sits in (one of <see cref="TypeModel.SlotBases"/>): the arguments its marker method
        /// takes (<c>isShelf(int32)</c>), in the type's own terms.
        /// </summary>
        internal GoMarker Markers(TypeModel type, ValueModel slot)
        {
            var definition = slot.Type!;
            if (!_supported.Contains(definition))
                throw new UnsupportedException($"{definition.FullName} is not written to Go");
            var outer = _context;
            _context = type.IsGenericDefinition ? GenericOf(type) : null;
            try
            {
                var items = slot.Arguments.Select(a => Shape(a, false)).ToList();
                return new GoMarker(SlotName(definition), items);
            }
            finally
            {
                _context = outer;
            }
        }

        /// <summary>The Go type of a shape, as text that is equal for equal Go types (the template spells it).</summary>
        private static string GoTypeKey(GoShape s) => s.Kind switch
        {
            "prim" => s.Primitive!,
            "enum" => "enum:" + s.Enum!.FullName,
            "rawstruct" or "emptystruct" => "struct:" + s.Struct!.FullName,
            "rawnullable" or "nullable" => "*" + GoTypeKey(s.Inner!),
            "rawtuple" or "tuple" => (s.Pair ? "entry" : "tuple") + "[" + string.Join(",", s.Items.Select(GoTypeKey)) + "]",
            "string" or "utf8" => "*string",
            "keystring" or "keyutf8" => "string",
            "struct" or "class" or "slot" => s.Kind + ":" + s.Type!.FullName,
            "param" => "param:" + s.Parameter,
            "generic" or "genslot" => s.Kind + ":" + s.Type!.FullName + "[" + string.Join(",", s.Items.Select(GoTypeKey)) + "]",
            "named" => "named:" + s.Named!.FullName,
            "seq" => "[]" + GoTypeKey(s.Element!),
            "map" => "[]entry[" + GoTypeKey(s.Key!) + "," + GoTypeKey(s.Value!) + "]",
            "grid" => "grid[" + GoTypeKey(s.Element!) + "]",
            _ => s.Kind,
        };

        /// <summary>Whether values of the shape are Go-comparable and compared by value in .NET (sets, keys).</summary>
        private static bool Comparable(GoShape shape) => shape.Kind is "prim" or "enum" or "rawstruct" or "rawtuple";

        /// <summary>A <c>[NinoType]</c> member, with the header of its kind.</summary>
        private GoShape TypeShape(TypeModel type)
        {
            if (!_supported.Contains(type))
                throw new UnsupportedException($"{type.FullName} is not written to Go");
            if (type.Raw)
                return Raw(type.RawValue!);
            return type.Header switch
            {
                HeaderKind.None => new GoShape("struct") { Type = type, MinSize = StructMinSize(type) },
                HeaderKind.Presence => new GoShape("class") { Type = type, MinSize = type.TrackReferences ? 4 : 1 },
                _ => new GoShape("slot") { Type = type, MinSize = 4 },
            };
        }

        private readonly HashSet<TypeModel> _sizing = new();

        /// <summary>The fewest bytes a struct's body takes (a collection's elements are checked against it).</summary>
        private int StructMinSize(TypeModel type)
        {
            if (type.Format != BodyFormat.Positional)
                return 4;
            // A generic struct's members are sized by its arguments: none is counted on.
            if (type.IsGenericDefinition)
                return 0;
            if (!_sizing.Add(type))
                return 0;
            try
            {
                return type.Members.Sum(m => Shape(m.Value, false).MinSize);
            }
            finally
            {
                _sizing.Remove(type);
            }
        }

        private static readonly Dictionary<PrimitiveKind, (string Name, bool Block, bool Validated)> Primitives = new()
        {
            [PrimitiveKind.Bool] = ("Bool", false, true),
            [PrimitiveKind.I8] = ("I8", true, false),
            [PrimitiveKind.U8] = ("U8", true, false),
            [PrimitiveKind.I16] = ("I16", true, false),
            [PrimitiveKind.U16] = ("U16", true, false),
            [PrimitiveKind.I32] = ("I32", true, false),
            [PrimitiveKind.U32] = ("U32", true, false),
            [PrimitiveKind.I64] = ("I64", true, false),
            [PrimitiveKind.U64] = ("U64", true, false),
            [PrimitiveKind.NInt] = ("I64", true, false),
            [PrimitiveKind.NUInt] = ("U64", true, false),
            [PrimitiveKind.F16] = ("Half", true, false),
            [PrimitiveKind.F32] = ("F32", true, false),
            [PrimitiveKind.F64] = ("F64", true, false),
            [PrimitiveKind.Char] = ("Char", true, false),
            [PrimitiveKind.I128] = ("Int128", false, false),
            [PrimitiveKind.U128] = ("UInt128", false, false),
            [PrimitiveKind.Guid] = ("Guid", false, false),
            [PrimitiveKind.TimeSpan] = ("TimeSpan", true, false),
            [PrimitiveKind.Decimal] = ("Decimal", false, true),
            [PrimitiveKind.DateTime] = ("DateTime", false, true),
            [PrimitiveKind.DateOnly] = ("DateOnly", false, true),
            [PrimitiveKind.TimeOnly] = ("TimeOnly", false, true),
        };

        /// <summary>
        /// The primitives other than numbers whose arrays are one block (<c>nino.ReadBlock</c>, the runtime's
        /// <c>BlockValue</c>): the checked ones checked as when read alone.
        /// </summary>
        private static readonly HashSet<string> BlockValues = new(StringComparer.Ordinal)
            { "DateTime", "DateOnly", "TimeOnly", "Decimal", "Guid", "Int128", "UInt128" };

        /// <summary>A value copied as raw memory.</summary>
        private GoShape Raw(ValueModel value)
        {
            switch (value.Kind)
            {
                case ValueKind.Primitive:
                    if (Primitives.TryGetValue(value.Primitive, out var p))
                        return new GoShape("prim")
                        {
                            Primitive = p.Name, Size = value.Size, MinSize = value.Size, Block = p.Block,
                            // Read in a run of fixed-size members: any bit pattern is valid.
                            Runnable = !p.Validated,
                        };
                    if (value.Fallback != null)
                        return Raw(value.Fallback);
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no Go form");
                case ValueKind.Enum:
                {
                    var model = value.Enum!;
                    if (!_enums.Contains(model))
                        _enums.Add(model);
                    if (!Primitives.TryGetValue(value.Primitive, out var u))
                        throw new UnsupportedException($"{model.FullName}'s underlying type has no Go form");
                    var inner = new GoShape("prim") { Primitive = u.Name, Size = value.Size, MinSize = value.Size };
                    return new GoShape("enum")
                    {
                        Enum = model, Inner = inner, Size = inner.Size, MinSize = inner.Size, Block = true, Runnable = true,
                    };
                }
                case ValueKind.RawStruct:
                {
                    var model = value.Struct!;
                    if (!_rawStructs.Contains(model))
                    {
                        _rawStructs.Add(model);
                        foreach (var field in model.Fields)
                            Raw(field.Value);
                    }

                    if (model.Empty)
                        return new GoShape("emptystruct") { Struct = model, Size = 1, MinSize = 1 };
                    return new GoShape("rawstruct")
                        { Struct = model, Size = model.Size, MinSize = model.Size, RawBlock = RawBlock(model) };
                }
                case ValueKind.RawNullable:
                {
                    var inner = Raw(value.Element!);
                    return new GoShape("rawnullable")
                    {
                        Inner = inner, FlagOffset = value.FlagOffset, ValueOffset = value.ValueOffset,
                        Size = value.Size, MinSize = value.Size,
                    };
                }
                case ValueKind.RawTuple:
                {
                    var items = value.Items.Select(Raw).ToList();
                    if (!value.IsPair)
                        _tuples.Add(items.Count);
                    return new GoShape("rawtuple")
                    {
                        Items = items, Offsets = value.Offsets.ToList(), Pair = value.IsPair, Size = value.Size,
                        MinSize = value.Size,
                    };
                }
                case ValueKind.Unsupported:
                    throw new UnsupportedException(value.Problem!.Replace("{lang}", "Go"));
                default:
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no Go form");
            }
        }

        /// <summary>
        /// Whether a raw struct can be copied as memory (<see cref="GoRawBlock"/>): its fields, nested raw structs'
        /// included, are numbers, enums, bools and other values every bit pattern of which is valid (no
        /// <c>decimal</c>, <c>DateTime</c>, <c>DateOnly</c>, <c>TimeOnly</c>, which are checked; no pointer, no
        /// empty struct, which Go gives no byte). Null when it cannot.
        /// </summary>
        internal GoRawBlock? RawBlock(RawStructModel model)
        {
            if (_rawBlocks.TryGetValue(model, out var known))
                return known;
            _rawBlocks[model] = null;
            if (model.Empty)
                return null;
            var block = new GoRawBlock(this, model);
            foreach (var field in model.Fields)
            {
                if (!BlockField(block, field.Value, field.Offset))
                    return null;
            }

            _rawBlocks[model] = block;
            return block;
        }

        private bool BlockField(GoRawBlock block, ValueModel value, int offset)
        {
            switch (value.Kind)
            {
                case ValueKind.Primitive:
                    if (!Primitives.TryGetValue(value.Primitive, out var p))
                        return value.Fallback != null && BlockField(block, value.Fallback, offset);
                    if (p.Name == "Bool")
                        block.BoolOffsets.Add(offset);
                    else if (p.Validated)
                        return false;
                    block.Fill(offset, value.Size);
                    return true;
                case ValueKind.Enum:
                    block.Fill(offset, value.Size);
                    return true;
                case ValueKind.RawStruct:
                {
                    var inner = RawBlock(value.Struct!);
                    if (inner == null)
                        return false;
                    block.BoolOffsets.AddRange(inner.BoolOffsets.Select(o => o + offset));
                    block.Filled.UnionWith(inner.Filled.Select(o => o + offset));
                    block.Nested.Add(inner);
                    return true;
                }
                default:
                    return false;
            }
        }
    }
}

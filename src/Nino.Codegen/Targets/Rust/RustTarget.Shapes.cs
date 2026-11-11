using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    public sealed partial class RustTarget
    {
        /// <summary>The Rust shape of a value. <paramref name="key"/>: a map key, never null (.NET throws on a null key).</summary>
        internal RustShape Shape(ValueModel value, bool key)
        {
            switch (value.Kind)
            {
                case ValueKind.Primitive:
                case ValueKind.Enum:
                case ValueKind.RawStruct:
                case ValueKind.RawNullable:
                    return Raw(value);
                case ValueKind.RawTuple:
                {
                    // Items one after another with no gap are the same bytes as a tuple of them; others (padding, or
                    // CoreCLR's auto layout, which puts larger items first) a struct of the items at their offsets.
                    var items = value.Items.Select(Raw).ToList();
                    int at = 0;
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (value.Offsets[i] != at)
                            return value.Fallback != null
                                ? Raw(value.Fallback)
                                : throw new UnsupportedException(
                                    $"{value.ClrName} ({value.Encoding}) has padding, which a Rust tuple cannot hold");
                        at += items[i].Size;
                    }

                    return new RustShape("tuple") { Items = items, Hashable = items.All(i => i.Hashable) };
                }
                case ValueKind.String:
                    return new RustShape(key ? "keystring" : "string")
                        { Wide = _options.WideStrings, Hashable = true };
                case ValueKind.Utf8:
                    return new RustShape(key ? "keyutf8" : "utf8") { Hashable = true };
                case ValueKind.Object:
                    _usesObject = true;
                    return new RustShape("object") { MayDefer = true };
                case ValueKind.Type:
                    return TypeShape(value.Type!);
                case ValueKind.Parameter:
                    // A type parameter: its argument's NinoRead decides (read_member asks it whether it may defer); a
                    // parameter a map key or set element holds is bound by Eq + Hash, and checked where it is closed.
                    return new RustShape("param") { Parameter = value.ParameterName, MayDefer = true, Hashable = true };
                case ValueKind.Generic:
                {
                    var definition = value.Type!;
                    if (!_supported.Contains(definition))
                        throw new UnsupportedException($"{definition.FullName} is not written to Rust");
                    if (definition.Header == HeaderKind.TypeId)
                        return SlotShape(value);
                    return GenericShape(value);
                }
                case ValueKind.Named:
                {
                    if (!_model.NamedCollections.TryGetValue(value.NamedTypeName ?? "", out var collection))
                        throw new UnsupportedException($"{value.ClrName} is not written to Rust");
                    var named = _namedCollections.FirstOrDefault(n => n.FullName == value.NamedTypeName);
                    if (named == null)
                    {
                        named = new RustNamed(TypeNames.Joined(TypeNames.WithoutArity(
                            value.NamedTypeName!.Split('.', '+').Last())), value.NamedTypeName!);
                        _namedCollections.Add(named);
                        named.Value = Shape(collection, false);
                    }

                    return new RustShape("named") { Named = named, MayDefer = true };
                }
                case ValueKind.Nullable:
                {
                    var inner = Shape(value.Element!, false);
                    return new RustShape("nullable") { Inner = inner, MayDefer = inner.MayDefer, Hashable = inner.Hashable };
                }
                case ValueKind.Sequence:
                {
                    if (value.Element!.Kind == ValueKind.RawNullable)
                        return new RustShape("seq") { Element = Raw(value.Element).Inner, Mode = "rawnullables" };

                    // A set's elements may be null (.NET writes a HashSet<string> holding null), so a string element
                    // is an Option as in a list; only a map's keys are never null.
                    var element = Shape(value.Element, false);
                    bool set = value.Collection is CollectionKind.HashSet or CollectionKind.SortedSet ||
                               CodegenModel.Implements(value.Clr, "System.Collections.Generic.ISet`1") != null;
                    if (set && !element.Hashable)
                        throw new UnsupportedException(
                            $"{value.ClrName}: a set of values Rust cannot hash (a set needs Eq and Hash)");
                    return new RustShape("seq") { Element = element, Mode = set ? "set" : "vec", MayDefer = element.MayDefer };
                }
                case ValueKind.Map:
                {
                    var (k, v) = (Shape(value.Key!, true), Shape(value.Value!, false));
                    if (!k.Hashable)
                        throw new UnsupportedException(
                            $"{value.ClrName}: keys Rust cannot hash (a map needs Eq and Hash)");
                    return new RustShape("map") { Key = k, Value = v, MayDefer = k.MayDefer || v.MayDefer };
                }
                case ValueKind.Tuple:
                {
                    var items = value.Items.Select(i => Shape(i, false)).ToList();
                    return new RustShape("tuple")
                        { Items = items, MayDefer = items.Any(i => i.MayDefer), Hashable = items.All(i => i.Hashable) };
                }
                case ValueKind.Grid:
                {
                    var element = Shape(value.Element!, false);
                    return new RustShape("grid") { Element = element, Rank = value.Rank, MayDefer = element.MayDefer };
                }
                case ValueKind.BigInteger:
                    return new RustShape("bigint") { Hashable = true };
                case ValueKind.Version:
                    return new RustShape("version") { Hashable = true };
                case ValueKind.DateTimeOffset:
                    return new RustShape("datetimeoffset") { Hashable = true };
                case ValueKind.Unsupported:
                    throw new UnsupportedException(value.Problem!.Replace("{lang}", "Rust"));
                default:
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no Rust form");
            }
        }

        /// <summary>The shape of a value of <paramref name="type"/>'s, whose open slots are its slot parameters.</summary>
        internal RustShape ShapeIn(TypeModel type, ValueModel value)
        {
            var outer = _context;
            _context = type;
            try
            {
                return Shape(value, false);
            }
            finally
            {
                _context = outer;
            }
        }

        /// <summary>
        /// The names of a generic definition's slot parameters (<see cref="TypeModel.SlotParameters"/>): type parameters
        /// after its own, bound to the enum of each slot where it is closed (<c>S0</c>, <c>S1</c>, ...).
        /// </summary>
        internal static List<string> SlotParameterNames(TypeModel type)
        {
            var taken = new HashSet<string>(type.TypeParameters, StringComparer.Ordinal);
            var names = new List<string>();
            for (int i = 0; i < type.SlotParameters.Count; i++)
            {
                string name = "S" + i.ToString(CultureInfo.InvariantCulture);
                while (!taken.Add(name))
                    name += "_";
                names.Add(name);
            }

            return names;
        }

        /// <summary>
        /// A generic struct or class with its arguments, then the enums of the slots it holds (its slot arguments).
        /// </summary>
        private RustShape GenericShape(ValueModel value)
        {
            var definition = value.Type!;
            var arguments = value.Arguments.Select(a => Shape(a, false)).ToList();
            foreach (int i in HashedParameters(definition))
                if (!arguments[i].Hashable)
                    throw new UnsupportedException(
                        $"{TypeNames.Runtime(value.Clr)}: {definition.TypeParameters[i]} is a key or set element, which Rust cannot hash");
            return new RustShape(definition.IsValueType ? "generic" : "genericclass")
            {
                Type = definition, Items = arguments, MayDefer = true,
                Slots = value.SlotArguments.Select(SlotShape).ToList(),
            };
        }

        /// <summary>
        /// A generic type written behind a type id with its arguments (<c>Shelf&lt;int&gt;</c>): the enum of the types
        /// that slot allows (<c>ShelfInt32Ref</c>), its closed types among them.
        /// </summary>
        private RustShape SlotShape(ValueModel value)
        {
            if (value.Slot is not { } slot)
            {
                // Open (Shelf<T> in a generic definition): a type parameter of the definition holding it, which each
                // closed use binds to the enum of its closed slot.
                int index = _context?.SlotParameters.FindIndex(p => p.TypeKey == value.TypeKey) ?? -1;
                if (index < 0)
                    throw new UnsupportedException($"{TypeNames.Runtime(value.Clr)} is a slot no type parameter names");
                return new RustShape("genslot") { RefName = SlotParameterNames(_context!)[index], MayDefer = true };
            }

            if (!_slotNames.TryGetValue(slot, out var name))
            {
                name = TypeNames.Closed(slot.Clr) + "Ref";
                _slotNames[slot] = name;
            }

            return new RustShape("genslot") { RefName = name, MayDefer = true };
        }

        /// <summary>A <c>[NinoType]</c> member, with the header of its kind.</summary>
        private RustShape TypeShape(TypeModel type)
        {
            if (!_supported.Contains(type))
                throw new UnsupportedException($"{type.FullName} is not written to Rust");
            if (type.Raw)
                return Raw(type.RawValue!);
            return type.Header switch
            {
                HeaderKind.None => new RustShape("struct") { Type = type },
                HeaderKind.Presence when type.TrackReferences => new RustShape("shared") { Type = type, MayDefer = true },
                HeaderKind.Presence => new RustShape("class") { Type = type, MayDefer = true },
                _ => new RustShape("slot") { Type = type, MayDefer = true },
            };
        }

        /// <summary>A value copied as raw memory: a number, a BCL value, an enum's integer or a raw struct.</summary>
        private RustShape Raw(ValueModel value)
        {
            switch (value.Kind)
            {
                case ValueKind.Primitive:
                    return new RustShape("prim")
                    {
                        Primitive = value.Primitive.ToString(), Size = value.Size,
                        Align = PrimitiveAlign(value.Primitive),
                        Hashable = value.Primitive is not (PrimitiveKind.F32 or PrimitiveKind.F64),
                    };
                case ValueKind.Enum:
                {
                    var model = value.Enum!;
                    if (model.Underlying == null)
                        throw new UnsupportedException($"{model.FullName}'s underlying type has no Rust form");
                    if (!_enums.Contains(model))
                        _enums.Add(model);
                    var inner = new RustShape("prim")
                    {
                        Primitive = model.Underlying.Value.ToString(), Size = value.Size, Align = value.Size,
                        Hashable = true,
                    };
                    return new RustShape("enum") { Enum = model, Inner = inner, Size = value.Size, Align = value.Size, Hashable = true };
                }
                case ValueKind.RawStruct:
                {
                    var model = value.Struct!;
                    var fields = model.Fields.Select(f => f.Value.Kind is ValueKind.Primitive or ValueKind.Enum or ValueKind.RawStruct
                            or ValueKind.RawTuple or ValueKind.RawNullable
                        ? Raw(f.Value)
                        : throw new UnsupportedException(
                            $"{model.FullName}.{f.Name} ({f.Value.Encoding}) has no Rust form inside a raw struct")).ToList();
                    if (!_rawStructs.Contains(model))
                        _rawStructs.Add(model);
                    return RawShapes[model] = new RustShape("rawstruct")
                    {
                        Struct = model, Size = model.Empty ? 1 : model.Size, Align = model.Align,
                        Hashable = fields.All(f => f.Hashable),
                    };
                }
                case ValueKind.RawNullable:
                {
                    // nino::Raw for Option<T>: the flag, then T at T's alignment, as Nullable<T>'s canonical layout.
                    var inner = Raw(value.Element!);
                    if (value.FlagOffset != 0 || value.ValueOffset != inner.Align || value.Size != inner.Align + inner.Size)
                        throw new UnsupportedException(
                            $"{value.ClrName} ({value.Encoding}) is not the flag then the value at its alignment");
                    return new RustShape("rawnullable")
                    {
                        Inner = inner, Size = value.Size, Align = inner.Align, Hashable = inner.Hashable,
                    };
                }
                case ValueKind.RawTuple when value.Fallback != null:
                    // Inside a raw struct, a struct of its items at their offsets, which implements nino::Raw.
                    return Raw(value.Fallback);
                case ValueKind.Unsupported:
                    throw new UnsupportedException(value.Problem!.Replace("{lang}", "Rust"));
                default:
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no Rust form inside raw memory");
            }
        }

        /// <summary>The alignment of a primitive in raw memory (<c>nino::Raw::ALIGN</c>).</summary>
        private static int PrimitiveAlign(PrimitiveKind kind) => kind switch
        {
            PrimitiveKind.Bool or PrimitiveKind.I8 or PrimitiveKind.U8 => 1,
            PrimitiveKind.I16 or PrimitiveKind.U16 or PrimitiveKind.F16 or PrimitiveKind.Char => 2,
            PrimitiveKind.I32 or PrimitiveKind.U32 or PrimitiveKind.F32 or PrimitiveKind.DateOnly or PrimitiveKind.Guid
                or PrimitiveKind.Rune => 4,
            PrimitiveKind.I128 or PrimitiveKind.U128 => 16,
            _ => 8,
        };

        /// <summary>
        /// The parameters of a generic definition that are map keys or set elements in it (directly, or through the
        /// generic types it holds): bound by <c>Eq + Hash</c>, and given only arguments Rust can hash.
        /// </summary>
        internal HashSet<int> HashedParameters(TypeModel type)
        {
            if (_hashedParameters.TryGetValue(type, out var hashed))
                return hashed;
            hashed = new HashSet<int>();
            _hashedParameters[type] = hashed;
            foreach (var value in type.Members.SelectMany(m => m.Value.Walk()))
            {
                IEnumerable<ValueModel> keys = value.Kind switch
                {
                    ValueKind.Map => new[] { value.Key! },
                    ValueKind.Sequence when value.Collection is CollectionKind.HashSet or CollectionKind.SortedSet ||
                                            CodegenModel.Implements(value.Clr, "System.Collections.Generic.ISet`1") != null
                        => new[] { value.Element! },
                    ValueKind.Generic when value.Type != type => HashedParameters(value.Type!).Select(i => value.Arguments[i]),
                    _ => Array.Empty<ValueModel>(),
                };
                foreach (var parameter in keys.SelectMany(k => k.Walk()).Where(v => v.Kind == ValueKind.Parameter))
                    hashed.Add(parameter.Parameter);
            }

            return hashed;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Nino.Codegen.Layouts;

namespace Nino.Codegen.Model
{
    /// <summary>
    /// The layout intermediate representation of an assembly: every <c>[NinoType]</c> its published layouts (and those
    /// of the assemblies it references) describe, each member's encoding resolved against its .NET type into a
    /// <see cref="ValueModel"/>. Language-neutral: what a target can express, and how, is the target's decision.
    /// Holds the metadata context the types live in: dispose it after rendering.
    /// </summary>
    public sealed class CodegenModel : IDisposable
    {
        private readonly LayoutReader _reader;
        private readonly Dictionary<Type, TypeModel> _byClr = new();
        private readonly Dictionary<Type, EnumModel> _enums = new();
        private readonly Dictionary<Type, RawStructModel> _structs = new();

        private CodegenModel(LayoutReader reader)
        {
            _reader = reader;
        }

        /// <summary>The name of the assembly the model was read from.</summary>
        public string AssemblyName => _reader.Assembly.GetName().Name ?? "";

        /// <summary>Every <c>[NinoType]</c> with a layout, in the order the layouts were read.</summary>
        public List<TypeModel> Types { get; } = new();

        /// <summary>
        /// The closed generic <c>[NinoType]</c>s the assembly names that can be created (not abstract, not
        /// interfaces), by name: what a polymorphic slot can hold besides the types with layouts.
        /// </summary>
        public List<ClosedTypeModel> ClosedTypes { get; } = new();

        /// <summary>The enums the values hold, in the order they were met.</summary>
        public IEnumerable<EnumModel> Enums => _enums.Values;

        /// <summary>The raw structs the values hold (<c>[NinoType]</c> or not), in the order they were met.</summary>
        public IEnumerable<RawStructModel> RawStructs => _structs.Values;

        /// <summary>
        /// The collections of themselves (<c>class Tree : List&lt;Tree&gt;</c>, no layout of their own) by their type:
        /// the value a member of the type is written as, whose elements are <see cref="ValueKind.Named"/> values of
        /// the same type.
        /// </summary>
        public Dictionary<string, ValueModel> NamedCollections { get; } = new(StringComparer.Ordinal);

        public void Dispose() => _reader.Dispose();

        /// <summary>Reads the layouts of <paramref name="assemblyPath"/> without loading it.</summary>
        public static CodegenModel Load(string assemblyPath, IEnumerable<string> references)
        {
            var reader = new LayoutReader(assemblyPath, references);
            try
            {
                var model = new CodegenModel(reader);
                model.Build(reader.ReadAll(), reader.ClosedTypes());
                return model;
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        public TypeModel? Find(string fullName) => Types.FirstOrDefault(t => t.FullName == fullName);

        internal TypeModel? Find(Type type) => _byClr.TryGetValue(type, out var model) ? model : null;

        /// <summary>
        /// Why a generic definition cannot be generated as a generic type in any language, or null when it can.
        /// Written behind a type id, its id is the hash of the closed type's .NET name, which a type argument's
        /// type in another language does not determine (a Rust <c>Vec&lt;i32&gt;</c> is <c>int[]</c> and
        /// <c>List&lt;int&gt;</c>). A generic type that holds, through other generic types, itself with other
        /// arguments (<c>Node&lt;T&gt;</c> holding <c>Node&lt;List&lt;T&gt;&gt;</c>) has no end to the types its codecs are
        /// built from. Holding itself with its own parameters (<c>Node&lt;T&gt;</c> holding <c>Node&lt;T&gt;</c>) is fine.
        /// </summary>
        public static string? GenericProblem(TypeModel type)
        {
            if (!type.IsGenericDefinition)
                return null;
            if (type.Header == HeaderKind.TypeId)
                return "a generic type written behind a type id (a class that is not sealed, an abstract class or an " +
                       "interface) is not supported: its type id depends on the type arguments' .NET names";
            var seen = new HashSet<TypeModel>();
            var pending = new Stack<TypeModel>();
            pending.Push(type);
            while (pending.Count != 0)
            {
                var next = pending.Pop();
                foreach (var used in OpenUses(next))
                {
                    if (used == type)
                        return "it holds itself with other type arguments, through " + next.FullName;
                    if (seen.Add(used))
                        pending.Push(used);
                }
            }

            return null;
        }

        /// <summary>
        /// The generic definitions a generic definition holds with arguments made of its own parameters, other than
        /// itself with its parameters in order: the codecs it builds from the codecs of its arguments.
        /// </summary>
        private static IEnumerable<TypeModel> OpenUses(TypeModel type) =>
            type.Members.SelectMany(m => m.Value.Walk())
                .Where(v => v.Kind == ValueKind.Generic && v.IsOpen && !v.IsSelf(type))
                .Select(v => v.Type!);

        // ---------------------------------------------------------------- building

        private void Build(Dictionary<Type, TypeLayout> layouts, IReadOnlyCollection<Type> closedTypes)
        {
            foreach (var layout in layouts.Values)
            {
                var model = new TypeModel(layout);
                Types.Add(model);
                _byClr[layout.Type] = model;
            }

            foreach (var type in Types)
            {
                var layout = type.Layout;
                for (var t = type.Clr.BaseType; t != null; t = t.BaseType)
                {
                    if (_byClr.TryGetValue(t, out var baseType))
                    {
                        type.BaseType = baseType;
                        break;
                    }
                }

                foreach (var i in type.Clr.GetInterfaces())
                {
                    if (_byClr.TryGetValue(i, out var interfaceType))
                        type.Interfaces.Add(interfaceType);
                }

                if (layout.Raw)
                    type.RawValue = layout.RawEncoding == null
                        ? Unsupported(type.Clr, "", $"{type.FullName} has no {{lang}} form")
                        : Raw(layout.RawEncoding, type.Clr);
                if (layout.Elements != null)
                {
                    var element = Implements(type.Clr, "System.Collections.Generic.IEnumerable`1")
                        ?.GetGenericArguments()[0];
                    type.Elements = element == null
                        ? Unsupported(type.Clr, layout.Elements.Source, $"{type.FullName} has no {{lang}} form")
                        : Value(layout.Elements, element);
                }

                foreach (var member in layout.Members)
                {
                    var value = member.Type == null
                        ? Unsupported(type.Clr, member.Encoding.Source, $"member {member.Name} was not found")
                        : Value(member.Encoding, member.Type);
                    var model = new MemberModel(member.Name, member.TypeName, value)
                    {
                        Key = member.Key,
                        DeclaringClr = member.DeclaringType,
                    };
                    model.FormerKeys.AddRange(member.FormerKeys);
                    if (member.DeclaringType != null)
                        model.DeclaringType = Find(member.DeclaringType);
                    type.Members.Add(model);
                }
            }

            foreach (var type in Types.Where(t => t.Header == HeaderKind.TypeId))
                type.Assignable.AddRange(Types.Where(t =>
                    t.TypeId != 0 && t.IsConcrete && type.Clr.IsAssignableFrom(t.Clr)));

            // The closed generic types the assembly names whose definitions have layouts and can be created.
            foreach (var clr in closedTypes.Where(t => !t.ContainsGenericParameters))
            {
                var definition = Find(clr.GetGenericTypeDefinition());
                if (definition == null || !definition.IsConcrete || definition.Raw)
                    continue;
                ClosedTypes.Add(new ClosedTypeModel(clr,
                    Generic(clr, definition, "type(" + TypeNames.Runtime(clr) + ")")));
            }

            ClosedTypes.Sort((a, b) => string.CompareOrdinal(a.RuntimeName, b.RuntimeName));
            foreach (var type in Types.Where(t => t.Header == HeaderKind.TypeId))
                type.ClosedAssignable.AddRange(ClosedTypes.Where(c => type.Clr.IsAssignableFrom(c.Clr)));
        }

        private static ValueModel Unsupported(Type clr, string encoding, string problem) =>
            new(ValueKind.Unsupported, clr, encoding) { Problem = problem };

        private static string NoForm(Type clr) => $"{clr.FullName} has no {{lang}} form";

        private static string NoForm(Type clr, string encoding) => $"{clr.FullName} ({encoding}) has no {{lang}} form";

        /// <summary>A member's encoding resolved against its type.</summary>
        private ValueModel Value(WireEncoding e, Type clr)
        {
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
                    // T?, Lazy<T>: a presence byte, then T. Tuple<...>: a presence byte, then its items.
                    var definition = clr.IsGenericType ? clr.GetGenericTypeDefinition().FullName : null;
                    if (definition is "System.Nullable`1" or "System.Lazy`1")
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
        private ValueModel Generic(Type clr, TypeModel definition, string encoding)
        {
            var value = new ValueModel(ValueKind.Generic, clr, encoding) { Type = definition };
            foreach (var argument in clr.GetGenericArguments())
            {
                string? text = ArgumentEncoding(argument, false);
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
        /// struct, a <c>ValueTuple</c>, a <c>KeyValuePair</c> or a <c>T?</c> is written item by item as a type
        /// argument, and as raw memory as a member. Null for a type whose formatter is not known here (a collection of
        /// <c>T?</c> or of <c>KeyValuePair</c>, which .NET may copy as raw memory, a <c>Tuple</c>, a type of another
        /// library): the generic type is then left out where it has that argument.
        /// </summary>
        internal string? ArgumentEncoding(Type type, bool element)
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
                return type.GetArrayRank() == 1 && ArgumentEncoding(type.GetElementType()!, true) is { } item
                    ? $"seq({item})"
                    : null;
            if (!type.IsGenericType)
                return null;
            var args = type.GetGenericArguments();
            string definition = type.GetGenericTypeDefinition().FullName ?? "";
            if (Find(type.GetGenericTypeDefinition()) != null)
                return $"type({type.Name})";
            string?[] parts = args.Select(a => ArgumentEncoding(a, definition.StartsWith("System.ValueTuple`",
                StringComparison.Ordinal) ? false : true)).ToArray();
            if (parts.Any(p => p == null))
                return null;
            switch (definition)
            {
                case "System.Collections.Generic.List`1":
                case "System.Collections.Generic.HashSet`1":
                    return $"seq({parts[0]})";
                case "System.Collections.Generic.Dictionary`2":
                    return $"map({parts[0]},{parts[1]})";
                case "System.Nullable`1" when !element:
                    return $"nullable({ArgumentEncoding(args[0], false)})";
                case "System.Collections.Generic.KeyValuePair`2" when !element:
                    return $"pair({ArgumentEncoding(args[0], false)},{ArgumentEncoding(args[1], false)})";
            }

            if (definition.StartsWith("System.ValueTuple`", StringComparison.Ordinal) && args.Length <= 7)
                return $"tuple({string.Join(",", parts)})";
            return null;
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
            if (definition == "System.Nullable`1" && e.Fields.Count == 2)
            {
                // T? in the canonical Nullable<T> layout: the flag, then the value at its offset (zeroed when absent).
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

        // ---------------------------------------------------------------- CLR helpers

        internal static PrimitiveKind? PrimitiveOf(Type clr) => clr.FullName switch
        {
            "System.Boolean" => PrimitiveKind.Bool,
            "System.SByte" => PrimitiveKind.I8,
            "System.Byte" => PrimitiveKind.U8,
            "System.Int16" => PrimitiveKind.I16,
            "System.UInt16" => PrimitiveKind.U16,
            "System.Int32" => PrimitiveKind.I32,
            "System.UInt32" => PrimitiveKind.U32,
            "System.Int64" => PrimitiveKind.I64,
            "System.UInt64" => PrimitiveKind.U64,
            "System.IntPtr" => PrimitiveKind.NInt,
            "System.UIntPtr" => PrimitiveKind.NUInt,
            "System.Half" => PrimitiveKind.F16,
            "System.Single" => PrimitiveKind.F32,
            "System.Double" => PrimitiveKind.F64,
            "System.Char" => PrimitiveKind.Char,
            "System.Decimal" => PrimitiveKind.Decimal,
            "System.Guid" => PrimitiveKind.Guid,
            "System.DateTime" => PrimitiveKind.DateTime,
            "System.TimeSpan" => PrimitiveKind.TimeSpan,
            "System.DateOnly" => PrimitiveKind.DateOnly,
            "System.TimeOnly" => PrimitiveKind.TimeOnly,
            "System.Int128" => PrimitiveKind.I128,
            "System.UInt128" => PrimitiveKind.U128,
            "System.Text.Rune" => PrimitiveKind.Rune,
            _ => null,
        };

        public static int PrimitiveSize(PrimitiveKind kind) => kind switch
        {
            PrimitiveKind.Bool or PrimitiveKind.I8 or PrimitiveKind.U8 => 1,
            PrimitiveKind.I16 or PrimitiveKind.U16 or PrimitiveKind.F16 or PrimitiveKind.Char => 2,
            PrimitiveKind.I32 or PrimitiveKind.U32 or PrimitiveKind.F32 or PrimitiveKind.DateOnly
                or PrimitiveKind.Rune => 4,
            PrimitiveKind.Decimal or PrimitiveKind.Guid or PrimitiveKind.I128 or PrimitiveKind.U128 => 16,
            _ => 8,
        };

        private static string? Generic(Type type) => type.IsGenericType ? type.GetGenericTypeDefinition().FullName : null;

        internal static Type? Implements(Type type, string definition)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == definition)
                return type;
            return type.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition().FullName == definition);
        }

        private static Type? FieldType(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return (type.GetField(name, flags) ?? type.GetField($"<{name}>k__BackingField", flags))?.FieldType;
        }
    }
}

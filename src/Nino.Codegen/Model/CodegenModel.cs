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
        private readonly Dictionary<string, SlotModel> _slots = new(StringComparer.Ordinal);
        private readonly Queue<SlotModel> _newSlots = new();

        /// <summary>The most closed generic types and slots <see cref="Close"/> adds before it gives up.</summary>
        private const int MaxClosure = 1024;

        /// <summary>The most slot parameters a generic definition takes (<see cref="TypeModel.SlotParameters"/>).</summary>
        private const int MaxSlotParameters = 32;

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

        /// <summary>
        /// The closed generic types written behind type ids (<see cref="SlotModel"/>) that the values hold, or that the
        /// codecs of the generic types they hold need (<see cref="ValueModel.SlotArguments"/>), ordered by name.
        /// </summary>
        public List<SlotModel> Slots { get; } = new();

        /// <summary>What the model could not describe, in every language.</summary>
        public List<string> Warnings { get; } = new();

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
        /// Why a generic definition cannot be generated as a generic type in a language whose generics are
        /// instantiated ahead of time (Go, Rust), or null when it can. A generic type that holds, through other generic
        /// types, itself with other arguments (<c>Node&lt;T&gt;</c> holding <c>Node&lt;List&lt;T&gt;&gt;</c>) has no end to the
        /// types its codecs are built from: Go rejects the instantiation cycle, Rust overflows checking it. Holding
        /// itself with its own parameters (<c>Node&lt;T&gt;</c> holding <c>Node&lt;T&gt;</c>) is fine. A language that builds
        /// those codecs when the data needs them (TypeScript) can write it.
        /// </summary>
        public static string? GenericProblem(TypeModel type)
        {
            if (!type.IsGenericDefinition)
                return null;
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
            OwnValues(type).SelectMany(v => v.Walk())
                .Where(v => v.Kind == ValueKind.Generic && v.IsOpen && !v.IsSelf(type))
                .Select(v => v.Type!);

        /// <summary>The values a type's body is made of: its members', its elements', or its raw value.</summary>
        internal static IEnumerable<ValueModel> OwnValues(TypeModel type)
        {
            if (type.RawValue != null)
                yield return type.RawValue;
            if (type.Elements != null)
                yield return type.Elements;
            foreach (var member in type.Members)
                yield return member.Value;
        }

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
                    if (_byClr.TryGetValue(Definition(t), out var baseType))
                    {
                        type.BaseType = baseType;
                        type.BaseValue = t.IsGenericType ? Generic(t, baseType, "type(" + TypeNames.Runtime(t) + ")", false) : null;
                        break;
                    }
                }

                foreach (var i in type.Clr.GetInterfaces())
                {
                    if (_byClr.TryGetValue(Definition(i), out var interfaceType))
                    {
                        type.Interfaces.Add(interfaceType);
                        type.InterfaceValues.Add(i.IsGenericType
                            ? Generic(i, interfaceType, "type(" + TypeNames.Runtime(i) + ")", false)
                            : null);
                    }
                }

                if (layout.Raw)
                    type.RawValue = layout.RawEncoding == null
                        ? Unsupported(type.Clr, "", $"{type.FullName} has no {{lang}} form")
                        : Raw(layout.RawEncoding, type.Clr);
                if (layout.Elements != null)
                {
                    // The collection's own encoding (seq(...) or map(...)), resolved against the type itself; its
                    // kind is that of the collection it derives from.
                    var elements = Value(layout.Elements, type.Clr);
                    if (elements.Kind is ValueKind.Sequence or ValueKind.Map && elements.Collection == CollectionKind.Other)
                        elements.Collection = CollectionOf(type.Clr, elements.Kind == ValueKind.Map);
                    type.Elements = elements.Kind is ValueKind.Sequence or ValueKind.Map or ValueKind.Unsupported
                        ? elements
                        : Unsupported(type.Clr, layout.Elements.Source, $"{type.FullName}'s elements have no {{lang}} form");
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

            // The generic slots each type can sit in, with the arguments it gives them.
            foreach (var type in Types.Where(t => t.IsConcrete))
            {
                foreach (var candidate in BasesOf(type.Clr).Where(b => b.IsGenericType))
                {
                    if (Find(Definition(candidate)) is not { Header: HeaderKind.TypeId, IsGenericDefinition: true } definition)
                        continue;
                    // A generic definition itself: its parameters are the arguments.
                    type.SlotBases.Add(Generic(candidate, definition, "type(" + TypeNames.Runtime(candidate) + ")", false));
                }
            }

            // The closed generic types the assembly names whose definitions have layouts and can be created.
            foreach (var clr in closedTypes.Where(t => !t.ContainsGenericParameters))
            {
                var definition = Find(clr.GetGenericTypeDefinition());
                if (definition == null || !definition.IsConcrete || definition.Raw)
                    continue;
                ClosedTypes.Add(new ClosedTypeModel(clr,
                    Generic(clr, definition, "type(" + TypeNames.Runtime(clr) + ")", false)));
            }

            FindSlotParameters();
            Close();

            ClosedTypes.Sort((a, b) => string.CompareOrdinal(a.RuntimeName, b.RuntimeName));
            foreach (var type in Types.Where(t => t.Header == HeaderKind.TypeId))
                type.ClosedAssignable.AddRange(ClosedTypes.Where(c => type.Clr.IsAssignableFrom(c.Clr)));
            Slots.AddRange(_slots.Values.OrderBy(v => v.RuntimeName, StringComparer.Ordinal));
            foreach (var slot in Slots)
            {
                slot.Assignable.AddRange(Types.Where(t => t.TypeId != 0 && t.IsConcrete && !t.IsGenericDefinition &&
                                                          AssignableTo(t.Clr, slot.Clr))
                    .OrderBy(t => t.RuntimeName, StringComparer.Ordinal));
                slot.ClosedAssignable.AddRange(ClosedTypes.Where(c => AssignableTo(c.Clr, slot.Clr)));
            }
        }

        // ---------------------------------------------------------------- generic types behind type ids

        /// <summary>
        /// The slot parameters of every generic definition (<see cref="TypeModel.SlotParameters"/>): the open generic
        /// types written behind type ids it holds, then, until none is added, those the generic types it holds need,
        /// with its arguments.
        /// </summary>
        private void FindSlotParameters()
        {
            var definitions = Types.Where(t => t.IsGenericDefinition).ToList();
            var keys = definitions.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.Ordinal));
            foreach (var type in definitions)
                foreach (var value in OwnValues(type).SelectMany(v => v.Walk()))
                    if (value.Kind == ValueKind.Generic && value.IsOpen && value.Type!.Header == HeaderKind.TypeId &&
                        keys[type].Add(value.TypeKey))
                        type.SlotParameters.Add(value);

            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var type in definitions.Where(t => t.SlotProblem == null))
                {
                    foreach (var value in OwnValues(type).SelectMany(v => v.Walk()).ToList())
                    {
                        if (value.Kind != ValueKind.Generic || !value.IsOpen || value.IsSelf(type) ||
                            value.Type!.Header == HeaderKind.TypeId)
                            continue;
                        foreach (var parameter in value.Type.SlotParameters.ToList())
                        {
                            var clr = Substitute(parameter.Clr, value.Clr.GetGenericArguments());
                            if (!clr.ContainsGenericParameters || !keys[type].Add(TypeKey(clr)))
                                continue;
                            if (type.SlotParameters.Count == MaxSlotParameters)
                            {
                                type.SlotProblem = "it holds generic types written behind type ids with ever more " +
                                                   "type arguments, through " + value.Type.FullName;
                                break;
                            }

                            type.SlotParameters.Add(Value(WireEncoding.Parse("type(" + TypeNames.Runtime(clr) + ")"), clr));
                            changed = true;
                        }

                        if (type.SlotProblem != null)
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Gives every generic value its slot arguments, and adds the closed types and slots that needs, until it adds
        /// none: each slot allows the closed types of the generic definitions assignable to it, with the arguments that
        /// makes them assignable, whether the assembly names them or not (<c>ListShelf&lt;int&gt;</c> for a member of
        /// <c>Shelf&lt;int&gt;</c>); their codecs need more slots in turn.
        /// </summary>
        private void Close()
        {
            var pending = new Queue<ValueModel>();
            foreach (var type in Types)
                foreach (var value in OwnValues(type))
                    pending.Enqueue(value);
            foreach (var closed in ClosedTypes)
                pending.Enqueue(closed.Value);
            var named = new HashSet<string>(ClosedTypes.Select(c => c.RuntimeName), StringComparer.Ordinal);
            int added = 0;
            while (pending.Count != 0 || _newSlots.Count != 0)
            {
                while (pending.Count != 0)
                {
                    foreach (var value in pending.Dequeue().Walk())
                    {
                        if (value.Kind != ValueKind.Generic || value.Slot != null || value.SlotArguments.Count != 0 ||
                            value.Type!.SlotParameters.Count == 0)
                            continue;
                        var arguments = value.Clr.GetGenericArguments();
                        foreach (var parameter in value.Type.SlotParameters)
                        {
                            var clr = Substitute(parameter.Clr, arguments);
                            value.SlotArguments.Add(Value(WireEncoding.Parse("type(" + TypeNames.Runtime(clr) + ")"), clr));
                        }
                    }
                }

                if (_newSlots.Count == 0)
                    break;
                var slot = _newSlots.Dequeue();
                if (++added > MaxClosure)
                {
                    Warnings.Add($"The closed generic types written behind type ids have no end (past {slot.RuntimeName}): " +
                                 $"the slots after the first {MaxClosure} are left out.");
                    _newSlots.Clear();
                    break;
                }

                // The concrete generic definitions assignable to the slot's definition, closed to fit the slot.
                foreach (var definition in Types.Where(t => t.IsGenericDefinition && t.IsConcrete && !t.Raw))
                {
                    foreach (var candidate in BasesOf(definition.Clr))
                    {
                        if (!candidate.IsGenericType ||
                            Definition(candidate) != Definition(slot.Clr))
                            continue;
                        var bindings = new Type?[definition.Clr.GetGenericArguments().Length];
                        if (!Unify(candidate, slot.Clr, bindings) || bindings.Any(b => b == null))
                            continue;
                        Type clr;
                        try
                        {
                            clr = definition.Clr.MakeGenericType(bindings!);
                        }
                        catch (ArgumentException)
                        {
                            // The arguments break the definition's constraints: no such type.
                            continue;
                        }

                        if (!named.Add(TypeNames.Runtime(clr)))
                            continue;
                        var model = new ClosedTypeModel(clr, Generic(clr, definition, "type(" + TypeNames.Runtime(clr) + ")", false));
                        ClosedTypes.Add(model);
                        pending.Enqueue(model.Value);
                    }
                }
            }
        }

        /// <summary>The slot of a closed generic type written behind a type id, added the first time it is met.</summary>
        private SlotModel SlotOf(Type clr, ValueModel value)
        {
            string name = TypeNames.Runtime(clr);
            if (_slots.TryGetValue(name, out var slot))
                return slot;
            slot = new SlotModel(clr, value);
            _slots[name] = slot;
            _newSlots.Enqueue(slot);
            return slot;
        }

        /// <summary>A generic type's definition, or the type itself.</summary>
        private static Type Definition(Type type) =>
            type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;

        /// <summary>The type, its base types and its interfaces.</summary>
        private static IEnumerable<Type> BasesOf(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                yield return t;
            foreach (var i in type.GetInterfaces())
                yield return i;
        }

        /// <summary>Whether a value of <paramref name="type"/> sits in a slot of <paramref name="slot"/> (by name).</summary>
        private static bool AssignableTo(Type type, Type slot)
        {
            string key = TypeKey(slot);
            return BasesOf(type).Any(b => TypeKey(b) == key);
        }

        /// <summary>
        /// <paramref name="type"/> with the type parameters of the generic definition it is written in replaced by
        /// <paramref name="arguments"/> (by position).
        /// </summary>
        private static Type Substitute(Type type, Type[] arguments)
        {
            if (type.IsGenericParameter)
                return arguments[type.GenericParameterPosition];
            if (type.IsArray)
            {
                var element = Substitute(type.GetElementType()!, arguments);
                return type.GetArrayRank() == 1 && type.Name.EndsWith("[]", StringComparison.Ordinal)
                    ? element.MakeArrayType()
                    : element.MakeArrayType(type.GetArrayRank());
            }

            if (!type.IsGenericType || !type.ContainsGenericParameters)
                return type;
            return type.GetGenericTypeDefinition()
                .MakeGenericType(type.GetGenericArguments().Select(a => Substitute(a, arguments)).ToArray());
        }

        /// <summary>
        /// Binds the type parameters of <paramref name="pattern"/> (by position, into <paramref name="bindings"/>) so
        /// that it is <paramref name="actual"/>; false when no binding does.
        /// </summary>
        private static bool Unify(Type pattern, Type actual, Type?[] bindings)
        {
            if (pattern.IsGenericParameter)
            {
                ref var bound = ref bindings[pattern.GenericParameterPosition];
                if (bound == null)
                {
                    bound = actual;
                    return true;
                }

                return TypeKey(bound) == TypeKey(actual);
            }

            if (pattern.IsArray)
                return actual.IsArray && pattern.GetArrayRank() == actual.GetArrayRank() &&
                       Unify(pattern.GetElementType()!, actual.GetElementType()!, bindings);
            if (pattern.IsGenericType)
            {
                if (!actual.IsGenericType || TypeKey(Definition(pattern)) != TypeKey(Definition(actual)))
                    return false;
                var (p, a) = (pattern.GetGenericArguments(), actual.GetGenericArguments());
                for (int i = 0; i < p.Length; i++)
                    if (!Unify(p[i], a[i], bindings))
                        return false;
                return true;
            }

            return TypeKey(pattern) == TypeKey(actual);
        }

        /// <summary>A .NET type as text, type parameters by position (<c>Conformance.Crate`1[!0]</c>).</summary>
        internal static string TypeKey(Type type)
        {
            if (type.IsGenericParameter)
                return "!" + type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture);
            if (type.IsArray)
                return TypeKey(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            // A definition is itself with its own parameters (Crate`1[!0]), as a value of it in its members is.
            if (type.IsGenericType)
                return (type.GetGenericTypeDefinition().FullName ?? type.Name) + "[" +
                       string.Join(",", type.GetGenericArguments().Select(TypeKey)) + "]";
            return type.FullName ?? type.Name;
        }

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
                case "System.Collections.Generic.KeyValuePair`2":
                    return $"pair({parts[0]},{parts[1]})";
            }

            if (definition.StartsWith("System.ValueTuple`", StringComparison.Ordinal) && args.Length <= 7)
                return $"tuple({string.Join(",", parts)})";
            return null;
        }

        /// <summary>
        /// The element of a <c>T?[]</c> or <c>List&lt;T?&gt;</c> with a raw <c>T</c>, as <c>NullableArrayFormatter</c>
        /// writes it (wire-format spec §6.2): the presence byte, padding to <c>T</c>'s canonical alignment <c>A</c>,
        /// then <c>T</c>, a stride of <c>A + size(T)</c>; null when <paramref name="type"/> is not such a <c>T?</c>.
        /// </summary>
        private string? NullableStride(Type type)
        {
            if (Generic(type) != "System.Nullable`1")
                return null;
            var value = type.GetGenericArguments()[0];
            if (RawArgument(value) is not { } raw)
                return null;
            int align = CanonicalAlign(value);
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

        /// <summary>
        /// The canonical alignment of a raw type (wire-format spec §5.2): of a leaf, its own; of a struct, the largest
        /// of its fields' (each limited to <c>[StructLayout(Pack)]</c>); 0 when it cannot be told.
        /// </summary>
        private static int CanonicalAlign(Type type)
        {
            if (type.IsEnum)
                type = type.GetEnumUnderlyingType();
            if (PrimitiveOf(type) is { } primitive)
                return primitive switch
                {
                    PrimitiveKind.Decimal => 8,
                    PrimitiveKind.Guid => 4,
                    PrimitiveKind.I128 or PrimitiveKind.U128 => 16,
                    PrimitiveKind.NInt or PrimitiveKind.NUInt => 0,
                    _ => PrimitiveSize(primitive),
                };
            if (!type.IsValueType)
                return 0;
            int pack = type.StructLayoutAttribute?.Pack ?? 0;
            int align = 1;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                int a = CanonicalAlign(field.FieldType);
                if (a <= 0)
                    return 0;
                if (pack > 0 && a > pack)
                    a = pack;
                align = Math.Max(align, a);
            }

            return align;
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
